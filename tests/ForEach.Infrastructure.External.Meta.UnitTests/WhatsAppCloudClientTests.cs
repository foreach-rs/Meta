using System.Net;
using ForEach.Infrastructure.External.Meta.Credentials;
using ForEach.Infrastructure.External.Meta.WhatsApp;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Sending on the Cloud API.
///
/// The same host as the Page client and a different everything else: a bearer token instead of a
/// query parameter, a phone number id instead of a Page, `messaging_product` on every request, and
/// the message id one array deep instead of flat. Each of those, got wrong, produces an error
/// naming something other than the mistake — an unsupported post request, or a 190 about "the"
/// token when there are two.
/// </summary>
public class WhatsAppCloudClientTests
{
    [Fact]
    public async Task A_reply_is_posted_to_the_phone_number_as_a_text_message()
    {
        var handler = new RecordingHandler("""{"messages":[{"id":"wamid.OUT"}]}""");

        var result = await Client(handler).SendTextAsync("381641234567", "Dobar dan.");

        Assert.True(result.IsSuccess);

        // One array deep, not the Page API's flat message_id. Reading the wrong field returns null
        // on a successful send, and the row then carries no id for the delivery receipts to match.
        Assert.Equal("wamid.OUT", result.Value);

        Assert.Equal(
            "https://graph.facebook.com/v21.0/phone-1/messages",
            handler.Last!.RequestUri!.ToString());

        // Required on every Cloud API request and set by nothing else. Without it the API answers
        // "unsupported post request", which reads like a wrong URL rather than a missing field.
        Assert.Contains("\"messaging_product\":\"whatsapp\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_travels_as_a_bearer_header_and_not_in_the_url()
    {
        // The Cloud API accepts both. A query string ends up in proxy and gateway logs, and a
        // token in a log is a token that has to be rotated.
        var handler = new RecordingHandler("""{"messages":[{"id":"wamid.OUT"}]}""");

        await Client(handler).SendTextAsync("381641234567", "Dobar dan.");

        Assert.Equal("Bearer", handler.Last!.Headers.Authorization?.Scheme);
        Assert.Equal("tajni-token", handler.Last.Headers.Authorization?.Parameter);

        Assert.DoesNotContain(
            "tajni-token", handler.Last.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_template_names_its_language_and_fills_the_body()
    {
        var handler = new RecordingHandler("""{"messages":[{"id":"wamid.OUT"}]}""");

        await Client(handler).SendTemplateAsync(
            "381641234567", "reorder_reminder", "sr", ["Kafić Zvezda", "12.03.2026."]);

        // The language of the approved template rather than of the customer: a mismatch answers
        // "template name does not exist", which sends whoever reads it looking for a template that
        // is sitting right there, approved.
        Assert.Contains("\"code\":\"sr\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"body\"", handler.LastBody, StringComparison.Ordinal);
        // Asserted on the ASCII half: the serialiser escapes ć to ć, which is valid JSON and
        // exactly what has always gone over the wire.
        Assert.Contains("12.03.2026.", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_error_Meta_gives_is_the_error_reported()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"Message failed to send because more than 24 hours have passed.","code":131047}}""",
            HttpStatusCode.BadRequest);

        var result = await Client(handler).SendTextAsync("381641234567", "Dobar dan.");

        Assert.False(result.IsSuccess);
        Assert.Contains("24 hours", result.FailureMessage, StringComparison.Ordinal);

        // And sorted, so the caller knows not to try again: a re-engagement refusal is permanent
        // until the customer writes first.
        Assert.Equal(MetaFailureKind.PolicyRefused, result.Error!.Kind);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_gateway_answering_instead_of_Meta_is_a_transport_failure()
    {
        // Not an error envelope, so there is nothing of Meta's to quote. Calling it "Meta 0" would
        // put words in their mouth, and the distinction is what tells a caller to retry.
        var result = await Client(new RecordingHandler("<html>502</html>", HttpStatusCode.BadGateway))
            .SendTextAsync("381641234567", "Dobar dan.");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public async Task An_accepted_message_with_no_id_is_treated_as_a_failure()
    {
        // A row under an invented id is a row no delivery receipt will ever match, so the message
        // would stay "sent" whatever actually happened to it.
        var result = await Client(new RecordingHandler("""{"messages":[]}"""))
            .SendTextAsync("381641234567", "Dobar dan.");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task The_template_list_is_read_off_the_business_account()
    {
        var handler = new RecordingHandler(
            """
            {"data":[{"name":"reorder_reminder","language":"sr","status":"APPROVED","category":"UTILITY",
              "components":[{"type":"BODY","text":"Poštovani {{1}}, vreme je za novu porudžbinu."}]}]}
            """);

        var result = await Client(handler).GetTemplatesAsync();

        Assert.True(result.IsSuccess);

        var template = Assert.Single(result.Value!);
        Assert.Equal("reorder_reminder", template.Name);
        Assert.Equal(1, template.ParameterCount);

        // The account rather than the number: templates hang off the WABA, and asking the phone
        // number id answers with an error about an unknown edge.
        Assert.Contains("waba-1/message_templates", handler.Last!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    // ---- harness -------------------------------------------------------------------------------

    private static WhatsAppCloudClient Client(RecordingHandler handler) =>
        new(new HttpClient(handler),
            new MetaAppCredentials { AppId = "app-1", AppSecret = "tajna", GraphApiVersion = "v21.0" },
            new WhatsAppCredentials
            {
                PhoneNumberId = "phone-1",
                WabaId = "waba-1",
                AccessToken = "tajni-token"
            });

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        /// <summary>Read here, because the client disposes its request before an assertion runs.</summary>
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;

            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
