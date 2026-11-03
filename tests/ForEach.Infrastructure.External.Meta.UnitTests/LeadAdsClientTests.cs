using System.Net;
using ForEach.Infrastructure.External.Meta.Credentials;
using ForEach.Infrastructure.External.Meta.Leads;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Fetching what somebody actually wrote on a form.
///
/// The webhook carries an id; this is where the lead arrives. Two things here fail in ways nobody
/// would see: `created_time` is an ISO string on this edge and seconds on the webhook, so reading
/// one as the other dates a lead to 1970 and buries it; and a permission problem arrives as a plain
/// code 100 whose message is the only thing separating it from a wrong parameter.
/// </summary>
public class LeadAdsClientTests
{
    [Fact]
    public async Task A_lead_comes_back_with_its_answers_and_its_advertisement()
    {
        var handler = new RecordingHandler(
            """
            {"id":"9001","created_time":"2026-03-12T09:15:00+0100","form_id":"77",
             "ad_id":"55","ad_name":"Kesice proleće","adset_id":"44","adset_name":"Kafići Beograd",
             "campaign_id":"33","campaign_name":"Proleće 2026","is_organic":false,"platform":"ig",
             "field_data":[
               {"name":"full_name","values":["Milan Petrović"]},
               {"name":"phone_number","values":["+381641234567"]},
               {"name":"koliko_kesica_mesecno","values":["oko 5000"]}]}
            """);

        var result = await Client(handler).GetLeadAsync("9001");

        Assert.True(result.IsSuccess);

        var lead = result.Value!;
        Assert.Equal("9001", lead.Id);
        Assert.Equal("Proleće 2026", lead.CampaignName);
        Assert.Equal("ig", lead.Platform);
        Assert.False(lead.IsOrganic);

        // The custom question is usually the point of the form, and it comes back beside the
        // standard ones rather than being dropped for having no column to live in.
        Assert.Equal("oko 5000", lead.Value("koliko_kesica_mesecno"));
        Assert.Equal("Milan Petrović", lead.Value("full_name"));

        // The token goes in the query string here because that is what this edge takes; the whole
        // URL is built from the credentials rather than from a base address.
        Assert.StartsWith(
            "https://graph.facebook.com/v21.0/9001?fields=",
            handler.Last!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_submission_time_is_read_as_the_ISO_string_it_is()
    {
        // Seconds on the webhook, an ISO string here. Reading this one as seconds puts the lead in
        // 1970, at the bottom of every list, where nobody telephones it.
        var result = await Client(new RecordingHandler(
            """{"id":"9001","created_time":"2026-03-12T09:15:00+0100","field_data":[]}""")).GetLeadAsync("9001");

        Assert.Equal(new DateTime(2026, 3, 12, 8, 15, 0, DateTimeKind.Utc), result.Value!.CreatedAt);
    }

    [Fact]
    public async Task A_checkbox_answer_keeps_every_value()
    {
        // Nearly always one value, and a client that assumed one would lose the rest on exactly the
        // questions worth asking.
        var result = await Client(new RecordingHandler(
            """
            {"id":"9002","created_time":"2026-03-12T09:15:00+0000",
             "field_data":[{"name":"proizvodi","values":["kesice","kašike","salvete"]}]}
            """)).GetLeadAsync("9002");

        var field = Assert.Single(result.Value!.Fields);
        Assert.Equal(3, field.Values.Count);
        Assert.Equal("kesice", result.Value.Value("proizvodi"));
    }

    [Fact]
    public async Task A_field_name_in_the_wrong_case_is_still_found()
    {
        // Form authors type `Email` where the standard field is `email`. A case-sensitive lookup
        // leaves the address in the answers and none in the column anybody searches.
        var result = await Client(new RecordingHandler(
            """{"id":"9003","created_time":"2026-03-12T09:15:00+0000","field_data":[{"name":"Email","values":["kupac@example.com"]}]}""")).GetLeadAsync("9003");

        Assert.Equal("kupac@example.com", result.Value!.Value("email"));
    }

    [Fact]
    public async Task An_organic_lead_names_no_campaign_and_nothing_is_missing()
    {
        var result = await Client(new RecordingHandler(
            """{"id":"9004","created_time":"2026-03-12T09:15:00+0000","is_organic":true,"field_data":[]}""")).GetLeadAsync("9004");

        Assert.True(result.Value!.IsOrganic);
        Assert.Null(result.Value.CampaignId);
    }

    [Fact]
    public async Task Leads_Access_being_refused_comes_back_in_Metas_own_words()
    {
        // A plain 100, whose message is the only thing separating "you may not read leads" from
        // "you asked for a field that does not exist". Sorted as a caller mistake so nothing
        // retries it, and quoted verbatim so somebody can act on it.
        var result = await Client(new RecordingHandler(
            """{"error":{"message":"(#100) Requires leads_retrieval permission","type":"OAuthException","code":100}}""",
            HttpStatusCode.BadRequest)).GetLeadAsync("9005");

        Assert.False(result.IsSuccess);
        Assert.Equal(MetaFailureKind.CallerMistake, result.Error!.Kind);
        Assert.Contains("leads_retrieval", result.FailureMessage, StringComparison.Ordinal);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public async Task The_backfill_asks_Meta_to_filter_by_time_rather_than_fetching_everything()
    {
        var handler = new RecordingHandler("""{"data":[]}""");

        await Client(handler).GetFormLeadsAsync("77", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var url = Uri.UnescapeDataString(handler.Last!.RequestUri!.ToString());

        Assert.Contains("77/leads", url, StringComparison.Ordinal);
        Assert.Contains("time_created", url, StringComparison.Ordinal);

        // 2026-03-01 in Unix seconds. Applied on Meta's side, so a re-read of one day costs one
        // day rather than the form's whole history.
        Assert.Contains("1772323200", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_forms_come_back_with_their_names_and_their_questions()
    {
        var handler = new RecordingHandler(
            """
            {"data":[{"id":"77","name":"Kesice — proleće","status":"ACTIVE",
              "questions":[{"key":"full_name","label":"Ime i prezime"}]}]}
            """);

        var result = await Client(handler).GetFormsAsync();

        var form = Assert.Single(result.Value!);
        Assert.Equal("Kesice — proleće", form.Name);

        // Kept raw: the labels are what let an old submission be read after somebody has renamed
        // the question in Ads Manager.
        Assert.Contains("Ime i prezime", form.Questions!, StringComparison.Ordinal);

        Assert.Contains("stranica-1/leadgen_forms", handler.Last!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribing_the_page_is_what_makes_the_webhook_fire_at_all()
    {
        // Without it everything downstream is correct and idle, which looks exactly like a form
        // nobody has filled in — the commonest reason a lead integration appears not to work.
        var handler = new RecordingHandler("""{"success":true}""");

        var result = await Client(handler).SubscribeFieldsAsync(["leadgen"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);

        Assert.Contains(
            "subscribed_apps", handler.Last.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    // ---- harness -------------------------------------------------------------------------------

    private static LeadAdsClient Client(RecordingHandler handler) =>
        new(new HttpClient(handler),
            new MetaAppCredentials { AppId = "app-1", AppSecret = "tajna", GraphApiVersion = "v21.0" },
            new MetaPageCredentials { PageId = "stranica-1", PageAccessToken = "tajni-token" });

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;

            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
