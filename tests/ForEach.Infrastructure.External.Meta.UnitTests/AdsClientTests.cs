using System.Net;
using ForEach.Infrastructure.External.Meta.Ads;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Reading what advertising cost, and pushing an audience back.
///
/// Three things here fail without an error anywhere. Meta sends every number as a *string*, so a
/// spend of "12.50" read under a Serbian culture becomes twelve and a half thousand and the report
/// says a campaign cost a thousand times what it did. Results are not a field but an entry in an
/// actions array under one of three names, so reading the wrong one shows nought leads for a
/// campaign that is working. And an audience row is positional: an address where a number belongs
/// uploads cleanly and matches nobody.
/// </summary>
public class AdsClientTests
{
    [Fact]
    public async Task A_day_of_a_campaign_comes_back_with_its_spend_and_its_leads()
    {
        var handler = new AdsHandler("""
            {"data":[{
                "campaign_id":"120", "campaign_name":"Šećer proleće",
                "spend":"12.50", "impressions":"4210", "clicks":"93",
                "actions":[{"action_type":"link_click","value":"93"},
                           {"action_type":"leadgen.other","value":"7"}],
                "date_start":"2026-08-20"
            }]}
            """);

        var result = await Insights(handler).GetDailyAsync(
            new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.True(result.IsSuccess);

        var row = Assert.Single(result.Value!);

        Assert.Equal("120", row.CampaignId);
        Assert.Equal(12.50m, row.Spend);
        Assert.Equal(4210, row.Impressions);
        Assert.Equal(93, row.Clicks);
        Assert.Equal(7, row.Results);
        Assert.Equal(new DateOnly(2026, 8, 20), row.Date);
    }

    [Fact]
    public async Task Results_are_counted_under_every_name_Meta_uses_for_a_lead()
    {
        // An Instant Form reports `leadgen.other`, an on-site form reports `lead`, and newer
        // objectives report the grouped variant. A client reading one of the three shows nought.
        var handler = new AdsHandler("""
            {"data":[{
                "campaign_id":"1", "campaign_name":"A", "spend":"1", "date_start":"2026-08-20",
                "actions":[{"action_type":"lead","value":"2"},
                           {"action_type":"onsite_conversion.lead_grouped","value":"3"},
                           {"action_type":"post_engagement","value":"99"}]
            }]}
            """);

        var result = await Insights(handler).GetDailyAsync(
            new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.Equal(5, Assert.Single(result.Value!).Results);
    }

    [Fact]
    public async Task A_row_with_no_date_is_dropped_rather_than_dated_to_today()
    {
        // The whole point of asking day by day is that a row can be re-read and overwritten. A row
        // with no date has no key, and storing it against today would double-count it tomorrow.
        var handler = new AdsHandler("""
            {"data":[{"campaign_id":"1","campaign_name":"A","spend":"1"}]}
            """);

        var result = await Insights(handler).GetDailyAsync(
            new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task The_account_id_is_prefixed_whichever_way_the_host_stored_it()
    {
        var handler = new AdsHandler("""{"data":[]}""");

        await Insights(handler, "1234567890").GetDailyAsync(
            new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.Contains("act_1234567890/insights", handler.Last!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_audience_row_keeps_the_address_and_the_number_in_the_order_the_schema_names()
    {
        // Positional, and silently wrong the other way round: Meta accepts it, hashes match
        // nothing, and the audience comes back smaller with no error to look at.
        var handler = new AdsHandler("""{"num_received":2}""");

        var result = await Audiences(handler).AddAsync(
            "23848", [("email-hash", "phone-hash"), ("only-email", null)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);

        // Form-encoded on the wire, so read back the way Meta will.
        var sent = Uri.UnescapeDataString(handler.LastBody);

        Assert.Contains("""["EMAIL","PHONE"]""", sent, StringComparison.Ordinal);
        Assert.Contains("""["email-hash","phone-hash"]""", sent, StringComparison.Ordinal);

        // The missing half is an empty string rather than a short row: a row of one value shifts
        // every value after it into the wrong column.
        Assert.Contains("""["only-email",""]""", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_number_Meta_actually_received_is_what_comes_back()
    {
        // Routinely lower than what was sent — rows it cannot use are dropped without a word — so
        // a client returning the sent count would overstate every audience it builds.
        var handler = new AdsHandler("""{"num_received":1}""");

        var result = await Audiences(handler).AddAsync("1", [("a", null), ("b", null), ("c", null)]);

        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task An_empty_upload_never_leaves_the_process()
    {
        var handler = new AdsHandler("""{"num_received":0}""");

        var result = await Audiences(handler).AddAsync("1", []);

        Assert.True(result.IsSuccess);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_refusal_from_Meta_is_reported_rather_than_read_as_an_empty_day()
    {
        var handler = new AdsHandler(
            """{"error":{"message":"(#200) Requires ads_read permission","code":200}}""",
            HttpStatusCode.Forbidden);

        var result = await Insights(handler).GetDailyAsync(
            new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.False(result.IsSuccess);
    }

    // ---- hashing -------------------------------------------------------------------------------

    [Fact]
    public void An_address_is_trimmed_and_lower_cased_before_hashing()
    {
        // Meta's rule exactly. Anything more — stripping dots, dropping a plus-tag — hashes to a
        // value their side never produces, so the person is uploaded and matches nobody.
        Assert.Equal(
            AudienceHash.Email("marko@kupac.rs"),
            AudienceHash.Email("  Marko@Kupac.RS  "));

        Assert.NotEqual(
            AudienceHash.Email("marko@kupac.rs"),
            AudienceHash.Email("mar.ko@kupac.rs"));
    }

    [Fact]
    public void A_national_number_gets_its_country_code_and_loses_its_trunk_zero()
    {
        // 060/123-4567 is 381601234567 to Meta. Uploaded as 0601234567 it matches nobody, which is
        // the failure mode that looks like "our customers are not on Facebook".
        Assert.Equal(
            AudienceHash.Phone("+381601234567", "381"),
            AudienceHash.Phone("060/123-4567", "381"));
    }

    [Fact]
    public void A_number_already_written_internationally_is_left_alone()
    {
        // No trunk-zero stripping here: a leading zero inside a country code is a real digit, and
        // removing it would corrupt numbers this application never sees but the library might.
        // A plus or a 00 marks the number as international; without either it is national, even
        // when it happens to start with the country-code digits.
        Assert.Equal(
            AudienceHash.Phone("+381601234567", "381"),
            AudienceHash.Phone("+381 60 123 4567", "381"));

        Assert.Equal(
            AudienceHash.Phone("+381601234567", "381"),
            AudienceHash.Phone("00381 60 123 4567", "381"));
    }

    [Fact]
    public void Nothing_worth_sending_hashes_to_nothing()
    {
        Assert.Null(AudienceHash.Email(null));
        Assert.Null(AudienceHash.Email("   "));
        Assert.Null(AudienceHash.Phone("", "381"));

        // Punctuation only: there are no digits, so there is no number — and a hash of the empty
        // string is a perfectly valid-looking value that would be uploaded as a person.
        Assert.Null(AudienceHash.Phone("- / ()", "381"));
    }

    [Fact]
    public void The_hash_is_the_format_Meta_asks_for()
    {
        // Against a published digest rather than against itself: a test comparing two calls of the
        // same function would pass on any consistent wrong algorithm, and "wrong algorithm" here
        // means every person uploaded matches nobody while the call succeeds.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            AudienceHash.Email("abc"));

        Assert.Matches("^[0-9a-f]{64}$", AudienceHash.Email("marko@kupac.rs")!);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static AdsInsightsClient Insights(AdsHandler handler, string account = "act_99") =>
        new(new HttpClient(handler), App, account, "ads-token");

    private static CustomAudienceClient Audiences(AdsHandler handler) =>
        new(new HttpClient(handler), App, "act_99", "ads-token");

    private static MetaAppCredentials App =>
        new() { AppId = "1", AppSecret = "s", GraphApiVersion = "v21.0" };

    private sealed class AdsHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
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
