using System.Net;
using System.Net.Http;
using ForEach.Infrastructure.External.Meta.Ads;
using ForEach.Infrastructure.External.Meta.Credentials;
using ForEach.Infrastructure.External.Meta.Leads;
using ForEach.Infrastructure.External.Meta.Messenger;
using ForEach.Infrastructure.External.Meta.Webhooks;
using ForEach.Infrastructure.External.Meta.WhatsApp;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// The review findings: a send whose fate is unknown must not look retryable, a reply that is not
/// shaped as expected must not throw after the message left, the webhook parsers must keep their
/// promise never to throw, lists must follow their cursor, and the caller's own cancellation must
/// reach the caller.
/// </summary>
public class ReviewFixTests
{
    // ---- 1 + 2: unknown outcome on sends ---------------------------------------------------------

    [Fact]
    public async Task A_messenger_send_that_times_out_is_unknown_and_not_retryable()
    {
        var result = await Messenger(new ScriptedHandler(WaitForever), TimeSpan.FromMilliseconds(50))
            .SendTextAsync("1", "Zdravo");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsOutcomeUnknown);
        Assert.Equal(MetaOutcome.Unknown, result.Outcome);
        Assert.False(result.IsWorthRetrying);
    }

    [Theory]
    [InlineData("""{"message_id":12345}""")]
    [InlineData("<html>ok</html>")]
    [InlineData("[]")]
    [InlineData("""{"message_id":null}""")]
    public async Task A_messenger_200_that_cannot_be_read_is_unknown_and_not_retryable(string body)
    {
        var result = await Messenger(new ScriptedHandler(_ => Reply(body))).SendTextAsync("1", "Zdravo");

        Assert.True(result.IsOutcomeUnknown);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_messenger_dns_failure_is_retryable()
    {
        var result = await Messenger(Throwing(HttpRequestError.NameResolutionError))
            .SendTextAsync("1", "Zdravo");

        Assert.False(result.IsOutcomeUnknown);
        Assert.Equal(MetaOutcome.Failed, result.Outcome);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_messenger_connection_broken_after_sending_is_unknown()
    {
        var result = await Messenger(Throwing(HttpRequestError.ResponseEnded))
            .SendTextAsync("1", "Zdravo");

        Assert.True(result.IsOutcomeUnknown);
        Assert.False(result.IsWorthRetrying);
    }

    [Theory]
    [InlineData("""{"messages":[{"id":12345}]}""")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("""{"messages":[1]}""")]
    [InlineData("<html>ok</html>")]
    public async Task A_whatsapp_200_that_cannot_be_read_is_unknown_without_throwing(string body)
    {
        var result = await WhatsApp(new ScriptedHandler(_ => Reply(body))).SendTextAsync("381601234567", "Zdravo");

        Assert.True(result.IsOutcomeUnknown);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_whatsapp_send_that_times_out_is_unknown_but_a_template_list_that_times_out_is_not()
    {
        var client = WhatsApp(new ScriptedHandler(WaitForever), TimeSpan.FromMilliseconds(50));

        var send = await client.SendTextAsync("381601234567", "Zdravo");

        Assert.True(send.IsOutcomeUnknown);
        Assert.False(send.IsWorthRetrying);

        var list = await client.GetTemplatesAsync();

        Assert.False(list.IsOutcomeUnknown);
        Assert.True(list.IsWorthRetrying);
    }

    [Fact]
    public async Task A_whatsapp_dns_failure_on_send_is_retryable()
    {
        var result = await WhatsApp(Throwing(HttpRequestError.ConnectionError))
            .SendTextAsync("381601234567", "Zdravo");

        Assert.False(result.IsOutcomeUnknown);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public async Task Creating_an_audience_that_times_out_or_answers_garbage_is_unknown()
    {
        var timeout = await Audiences(new ScriptedHandler(WaitForever), TimeSpan.FromMilliseconds(50))
            .CreateAsync("Kupci", null);

        Assert.True(timeout.IsOutcomeUnknown);
        Assert.False(timeout.IsWorthRetrying);

        var garbage = await Audiences(new ScriptedHandler(_ => Reply("<html/>"))).CreateAsync("Kupci", null);

        Assert.True(garbage.IsOutcomeUnknown);

        var dns = await Audiences(Throwing(HttpRequestError.SecureConnectionError))
            .CreateAsync("Kupci", null);

        Assert.False(dns.IsOutcomeUnknown);
        Assert.True(dns.IsWorthRetrying);
    }

    [Fact]
    public async Task A_read_that_times_out_stays_a_retryable_transport_failure()
    {
        var result = await Leads(new ScriptedHandler(WaitForever), TimeSpan.FromMilliseconds(50))
            .GetLeadAsync("9001");

        Assert.False(result.IsOutcomeUnknown);
        Assert.True(result.IsWorthRetrying);
    }

    // ---- 3: parsers never throw --------------------------------------------------------------------

    public static TheoryData<string> Hostile =>
    [
        "[]", "1", "null", "\"x\"", "true",
        """{"entry":[1]}""",
        """{"entry":[{"messaging":[1]}]}""",
        """{"entry":[{"messaging":[{"message":1}]}]}""",
        """{"entry":[{"messaging":[{"message":{"mid":"m","attachments":[1]},"sender":{"id":"s"},"timestamp":"abc"}]}]}""",
        """{"entry":[{"messaging":[{"message":{"mid":"m","text":"x"},"sender":{"id":"s"},"timestamp":99999999999999999}]}]}""",
        """{"entry":[{"messaging":[{"message":{"mid":"m","text":"x"},"sender":{"id":"s"},"timestamp":"1700000000000"}]}]}""",
        """{"entry":[{"changes":[1]}]}""",
        """{"entry":[{"changes":[{"value":1}]}]}""",
        """{"entry":[{"changes":[{"value":{"contacts":[1],"messages":[1]}}]}]}""",
        """{"entry":[{"changes":[{"value":{"contacts":[1],"statuses":[1,{"id":"w","status":"sent","timestamp":99999999999999999,"errors":[1]}]}}]}]}""",
        """{"entry":[{"changes":[{"value":{"messages":[{"id":"w","from":"1","timestamp":"-5","type":"text","text":1}]}}]}]}""",
        """{"entry":[{"changes":[{"field":"leadgen","value":{"leadgen_id":"1","created_time":"x"}}]}]}""",
        """{"entry":[{"changes":[{"field":"leadgen","value":{"leadgen_id":"1","created_time":99999999999999999}}]}]}""",
        """{"entry":[{"changes":[{"field":"leadgen","value":{"leadgen_id":"1","created_time":"1700000000"}}]}]}"""
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void The_messenger_parser_never_throws(string payload) =>
        Assert.NotNull(MessengerWebhookParser.Parse(payload, MetaChannel.Messenger));

    [Theory]
    [MemberData(nameof(Hostile))]
    public void The_whatsapp_parser_never_throws(string payload)
    {
        Assert.NotNull(WhatsAppWebhookParser.Parse(payload));
        Assert.NotNull(WhatsAppWebhookParser.ParseStatuses(payload));
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void The_leadgen_parser_never_throws(string payload) =>
        Assert.NotNull(LeadGenWebhookParser.Parse(payload));

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("""{"data":[1,{"name":"a","language":"sr","components":1},{"name":"b","language":"sr","components":[1]}]}""")]
    public void The_template_reader_never_throws(string payload) =>
        Assert.NotNull(WhatsAppTemplateReader.Read(payload));

    [Fact]
    public void An_out_of_range_or_odd_timestamp_falls_back_to_now_and_a_string_one_is_read()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var odd = MessengerWebhookParser.Parse("""{"entry":[{"messaging":[{"message":{"mid":"m","text":"x"},"sender":{"id":"s"},"timestamp":99999999999999999}]}]}""", MetaChannel.Messenger);

        Assert.True(Assert.Single(odd).SentAt >= before);

        var text = MessengerWebhookParser.Parse(
            """{"entry":[{"messaging":[{"message":{"mid":"m","text":"x"},"sender":{"id":"s"},"timestamp":"1700000000000"}]}]}""",
            MetaChannel.Messenger);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            Assert.Single(text).SentAt);
    }

    [Fact]
    public async Task Lead_lists_with_junk_items_yield_the_good_ones()
    {
        var result = await Leads(new ScriptedHandler(_ => Reply(
                """{"data":[1,"x",null,{"id":"1","is_organic":true},{"form_id":"no id"}]}""")))
            .GetFormLeadsAsync("77", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var lead = Assert.Single(result.Value!);

        Assert.Equal("1", lead.Id);
        Assert.True(lead.IsOrganic);
    }

    [Fact]
    public async Task A_single_lead_without_an_id_is_a_failure_not_an_empty_success()
    {
        var result = await Leads(new ScriptedHandler(_ => Reply("""{"form_id":"5"}"""))).GetLeadAsync("9001");

        Assert.False(result.IsSuccess);
    }

    // ---- 4: pagination -------------------------------------------------------------------------------

    [Fact]
    public async Task Form_leads_follow_paging_next()
    {
        var calls = new List<string>();

        var handler = new ScriptedHandler(request =>
        {
            calls.Add(request.RequestUri!.ToString());

            return Reply(calls.Count == 1
                ? """{"data":[{"id":"1"}],"paging":{"next":"https://graph.facebook.com/v21.0/77/leads?after=C2&access_token=x"}}"""
                : """{"data":[{"id":"2"}]}""");
        });

        var result = await Leads(handler)
            .GetFormLeadsAsync("77", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["1", "2"], result.Value!.Select(l => l.Id));
        Assert.Equal(2, calls.Count);
        Assert.Contains("after=C2", calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forms_follow_paging_next()
    {
        var calls = 0;

        var handler = new ScriptedHandler(_ => Reply(++calls == 1
            ? """{"data":[{"id":"1","name":"A"}],"paging":{"next":"https://graph.facebook.com/v21.0/page-99/leadgen_forms?after=C2"}}"""
            : """{"data":[{"id":"2","name":"B"}]}"""));

        var result = await Leads(handler).GetFormsAsync();

        Assert.Equal(["1", "2"], result.Value!.Select(f => f.Id));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Daily_insights_follow_paging_next()
    {
        var calls = 0;

        var handler = new ScriptedHandler(_ => Reply(++calls == 1
            ? """{"data":[{"campaign_id":"1","date_start":"2026-08-20"}],"paging":{"next":"https://graph.facebook.com/v21.0/act_99/insights?after=C2"}}"""
            : """{"data":[{"campaign_id":"2","date_start":"2026-08-20"}]}"""));

        var result = await Insights(handler).GetDailyAsync(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20));

        Assert.Equal(["1", "2"], result.Value!.Select(r => r.CampaignId));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Templates_follow_paging_next()
    {
        var calls = 0;

        var handler = new ScriptedHandler(_ => Reply(++calls == 1
            ? """{"data":[{"name":"a","language":"sr"}],"paging":{"next":"https://graph.facebook.com/v21.0/waba-1/message_templates?after=C2"}}"""
            : """{"data":[{"name":"b","language":"sr"}]}"""));

        var result = await WhatsApp(handler).GetTemplatesAsync();

        Assert.Equal(["a", "b"], result.Value!.Select(t => t.Name));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_cursor_that_never_ends_stops_at_fifty_pages_and_never_leaves_the_graph_host()
    {
        var calls = 0;

        var endless = new ScriptedHandler(_ =>
        {
            calls++;

            return Reply("""{"data":[],"paging":{"next":"https://graph.facebook.com/v21.0/page-99/leadgen_forms?after=again"}}""");
        });

        Assert.True((await Leads(endless).GetFormsAsync()).IsSuccess);
        Assert.Equal(50, calls);

        calls = 0;

        var elsewhere = new ScriptedHandler(_ =>
        {
            calls++;

            return Reply("""{"data":[],"paging":{"next":"https://evil.example/steal"}}""");
        });

        Assert.True((await Leads(elsewhere).GetFormsAsync()).IsSuccess);
        Assert.Equal(1, calls);
    }

    // ---- 5: error table -------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, null, MetaFailureKind.Configuration)]
    [InlineData(10, 2018278, MetaFailureKind.PolicyRefused)]
    [InlineData(200, null, MetaFailureKind.Configuration)]
    [InlineData(299, null, MetaFailureKind.Configuration)]
    [InlineData(131042, null, MetaFailureKind.Configuration)]
    [InlineData(131031, null, MetaFailureKind.Configuration)]
    [InlineData(131008, null, MetaFailureKind.CallerMistake)]
    [InlineData(131009, null, MetaFailureKind.CallerMistake)]
    [InlineData(131021, null, MetaFailureKind.CallerMistake)]
    public void Error_codes_are_classified(int code, int? subcode, MetaFailureKind expected) =>
        Assert.Equal(expected, new MetaError(code, subcode, "t", "m", null).Kind);

    [Theory]
    [InlineData("""{"error":{"code":"190","message":"x"}}""", 190)]
    [InlineData("""{"error":{"code":190.0,"message":"x"}}""", 190)]
    [InlineData("""{"error":{"code":"abc","message":"x"}}""", 0)]
    [InlineData("""{"error":{"code":true,"error_subcode":"2018278","message":"x"}}""", 0)]
    public void An_error_code_of_an_unexpected_kind_is_read_without_throwing(string body, int code)
    {
        var error = MetaError.TryRead(body);

        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
    }

    // ---- 6: caller cancellation ---------------------------------------------------------------------

    [Fact]
    public async Task A_cancelled_caller_gets_the_cancellation_from_every_client()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ok = new ScriptedHandler(_ => Reply("{}"));
        var token = cts.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Messenger(ok).SendTextAsync("1", "x", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WhatsApp(ok).SendTextAsync("1", "x", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WhatsApp(ok).GetTemplatesAsync(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Leads(ok).GetLeadAsync("1", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Leads(ok).SubscribeFieldsAsync(["leadgen"], token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Insights(ok).GetCurrencyAsync(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Audiences(ok).CreateAsync("a", null, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Messenger(ok).GetDisplayNameAsync("1", token));
    }

    // ---- 7: button reply ----------------------------------------------------------------------------

    [Fact]
    public void A_template_quick_reply_reads_as_the_words_the_customer_tapped()
    {
        var message = Assert.Single(WhatsAppWebhookParser.Parse(
            """
            {"entry":[{"changes":[{"value":{"messages":[{"id":"w1","from":"381601234567","timestamp":"1700000000",
            "type":"button","button":{"payload":"P","text":"Da, potvrđujem"}}]}}]}]}
            """));

        Assert.Equal("Da, potvrđujem", message.Body);
        Assert.Equal("P", message.ReplyPayload);
    }

    // ---- 8: phone hashing ----------------------------------------------------------------------------

    [Fact]
    public void The_default_country_code_may_be_written_with_a_plus()
    {
        Assert.Equal(AudienceHash.Phone("060 1234567", "381"), AudienceHash.Phone("060 1234567", "+381"));
    }

    [Fact]
    public void A_national_number_is_prefixed_even_when_it_starts_with_the_country_digits()
    {
        Assert.Equal(AudienceHash.Phone("+49 491 123456", "49"), AudienceHash.Phone("0491 123456", "49"));
    }

    [Fact]
    public void A_trunk_zero_in_brackets_is_dropped()
    {
        Assert.Equal(AudienceHash.Phone("+381 60 1234567", "381"), AudienceHash.Phone("+381 (0)60 1234567", "381"));
    }

    // ---- 9: small ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_local_since_is_converted_to_utc_and_a_user_id_is_escaped()
    {
        string? query = null;

        var handler = new ScriptedHandler(request =>
        {
            query ??= request.RequestUri!.ToString();

            return Reply("""{"data":[]}""");
        });

        var local = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local);
        var expected = new DateTimeOffset(local.ToUniversalTime()).ToUnixTimeSeconds();

        await Leads(handler).GetFormLeadsAsync("77", local);

        Assert.Contains($"\"value\":{expected}", Uri.UnescapeDataString(query!), StringComparison.Ordinal);

        await Messenger(handler).GetDisplayNameAsync("a/b?c");

        Assert.Contains("a%2Fb%3Fc", handler.LastRawUrl, StringComparison.Ordinal);
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> WaitForever(HttpRequestMessage request)
    {
        await Task.Delay(Timeout.Infinite, request.Options.TryGetValue(CancellationKey, out var token) ? token : default);

        return Reply("{}");
    }

    private static readonly HttpRequestOptionsKey<CancellationToken> CancellationKey = new("ct");

    private static ScriptedHandler Throwing(HttpRequestError error) =>
        new(new Func<HttpRequestMessage, HttpResponseMessage>(_ => throw new HttpRequestException(error, "boom")));

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static MetaAppCredentials App =>
        new() { AppId = "app-1", AppSecret = "tajna", GraphApiVersion = "v21.0" };

    private static MessengerClient Messenger(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), App, new MetaPageCredentials { PageId = "page-99", PageAccessToken = "tok" }, timeout);

    private static LeadAdsClient Leads(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), App, new MetaPageCredentials { PageId = "page-99", PageAccessToken = "tok" }, timeout);

    private static WhatsAppCloudClient WhatsApp(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), App,
            new WhatsAppCredentials { PhoneNumberId = "phone-1", WabaId = "waba-1", AccessToken = "tok" }, timeout);

    private static AdsInsightsClient Insights(ScriptedHandler handler) =>
        new(new HttpClient(handler), App, "act_99", "tok");

    private static CustomAudienceClient Audiences(ScriptedHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), App, "act_99", "tok", timeout);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = request => Task.FromResult(respond(request));

        public ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        public string LastRawUrl { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastRawUrl = request.RequestUri!.OriginalString;
            request.Options.Set(CancellationKey, cancellationToken);

            return await _respond(request);
        }
    }
}
