using System.Net;
using ForEach.Infrastructure.External.Meta.Credentials;
using ForEach.Infrastructure.External.Meta.Messenger;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// The Page's side of Messenger and Instagram.
///
/// Two calls, and both fail in ways that look like something else if they are got wrong: a message
/// sent as anything but a RESPONSE is refused with an error naming the token rather than the type,
/// and a 200 with no message_id leaves a row the customer's reply will never thread to.
///
/// The failure messages are Meta's own on purpose. "This message is sent outside of allowed
/// window", "invalid access token" and "message undeliverable" are three different afternoons, and
/// a client that flattened them into "sending failed" would leave somebody re-typing a reply that
/// can never go.
/// </summary>
public class MessengerClientTests
{
    [Fact]
    public async Task A_reply_is_posted_to_the_page_as_a_RESPONSE()
    {
        var handler = new RecordingHandler("""{"message_id":"m_ABC"}""");

        var result = await Client(handler).SendTextAsync("1234567890", "Dobar dan.");

        Assert.True(result.IsSuccess);
        Assert.Equal("m_ABC", result.Value);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);

        Assert.StartsWith(
            "https://graph.facebook.com/v21.0/page-99/messages",
            handler.Last.RequestUri!.ToString(), StringComparison.Ordinal);

        // RESPONSE, because that is what this is: an answer inside the window the customer opened.
        // Any other messaging_type claims a permission the Page does not have, and Meta polices
        // exactly that — with an error naming the token rather than the type.
        Assert.Contains("\"messaging_type\":\"RESPONSE\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"recipient\":{\"id\":\"1234567890\"}", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("Dobar dan.", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metas_own_words_survive_the_failure()
    {
        var handler = new RecordingHandler(
            """{"error":{"message":"This message is sent outside of allowed window.","code":10}}""",
            HttpStatusCode.BadRequest);

        var result = await Client(handler).SendTextAsync("1234567890", "Dobar dan.");

        Assert.False(result.IsSuccess);
        Assert.Contains("outside of allowed window", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_that_is_not_json_still_says_something_true()
    {
        // A gateway between us and Meta answers HTML. The status is all there is, and it beats an
        // empty message or a parse exception surfacing as "sending failed".
        var handler = new RecordingHandler("<html>502</html>", HttpStatusCode.BadGateway);

        var result = await Client(handler).SendTextAsync("1234567890", "Dobar dan.");

        Assert.False(result.IsSuccess);

        // A transport failure rather than something attributed to Meta: they said nothing, and a
        // caller reading Kind would otherwise be told a gateway's opinion was theirs.
        Assert.Null(result.Error);
        Assert.Contains("502", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_accepted_message_with_no_id_is_treated_as_a_failure()
    {
        // A row written under an invented id is a row the customer's reply never threads to, and
        // the echo of our own message arrives through the webhook and is filed a second time.
        var handler = new RecordingHandler("""{"ok":true}""");

        var result = await Client(handler).SendTextAsync("1234567890", "Dobar dan.");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_display_name_that_cannot_be_read_is_absent_rather_than_fatal()
    {
        // The name is decoration on a message. Refusing the message because we could not label it
        // would lose the thing that matters to protect the thing that does not.
        var handler = new RecordingHandler("""{"error":{"message":"nope"}}""", HttpStatusCode.Forbidden);

        Assert.Null(await Client(handler).GetDisplayNameAsync("1234567890"));
    }

    [Fact]
    public async Task A_person_with_only_first_and_last_name_is_still_named()
    {
        // Instagram answers with `name`; Messenger has answered with the parts. Both have to work,
        // because the alternative shows a numeric id where a person's name belongs.
        var handler = new RecordingHandler("""{"first_name":"Milan","last_name":"Petrović"}""");

        Assert.Equal("Milan Petrović", await Client(handler).GetDisplayNameAsync("1234567890"));
    }

    // ---- harness -------------------------------------------------------------------------------

    private static MessengerClient Client(RecordingHandler handler) =>
        new(new HttpClient(handler),
            new MetaAppCredentials { AppId = "app-1", AppSecret = "tajna", GraphApiVersion = "v21.0" },
            new MetaPageCredentials { PageId = "page-99", PageAccessToken = "tajni-token" });

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
