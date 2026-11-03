using ForEach.Infrastructure.External.Meta.Webhooks;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Form submissions, announced.
///
/// The webhook carries no answers at all — only ids — and that is the whole shape of this feature:
/// the lead is fetched afterwards with the Page token, so nothing personal travels in a payload
/// anybody could forge, and a forged event could at most make us fetch a lead we may already read.
///
/// What can go wrong here is quiet. A submission read by the message parser yields nothing and
/// says nothing, which from a screen is indistinguishable from a form nobody has filled in.
/// </summary>
public class LeadGenWebhookParserTests
{
    [Fact]
    public void A_submission_is_read_as_the_id_to_fetch()
    {
        var submission = Assert.Single(LeadGenWebhookParser.Parse(
            """
            {"object":"page","entry":[{"id":"stranica-1","time":1750000000,
              "changes":[{"field":"leadgen","value":{
                "leadgen_id":"9001","form_id":"77","page_id":"stranica-1","ad_id":"55",
                "created_time":1750000000}}]}]}
            """));

        Assert.Equal("9001", submission.LeadId);
        Assert.Equal("77", submission.FormId);
        Assert.Equal("55", submission.AdId);

        // When they pressed send, not when we heard: the fetch may be minutes later, or days later
        // after a token has been fixed, and the two are different questions.
        Assert.Equal(new DateTime(2025, 6, 15, 15, 6, 40, DateTimeKind.Utc), submission.CreatedAt);
    }

    [Fact]
    public void An_organic_submission_names_no_advertisement()
    {
        // A form on the Page itself. No ad_id, and none missing — the host records it as organic
        // rather than as a lead whose campaign went astray.
        var submission = Assert.Single(LeadGenWebhookParser.Parse(
            """
            {"object":"page","entry":[{"id":"stranica-1","changes":[{"field":"leadgen",
              "value":{"leadgen_id":"9002","form_id":"77","created_time":1750000000}}]}]}
            """));

        Assert.Null(submission.AdId);

        // The page id falls back to the entry's own, which is where it always is.
        Assert.Equal("stranica-1", submission.PageId);
    }

    [Fact]
    public void Ids_sent_as_numbers_are_read_as_ids()
    {
        // Meta sends these as strings and has been seen sending them as numbers. Reading only
        // strings drops the form id on exactly the payload shaped the other way, and a lead with
        // no form is a lead no filter finds.
        var submission = Assert.Single(LeadGenWebhookParser.Parse(
            """
            {"object":"page","entry":[{"id":"stranica-1","changes":[{"field":"leadgen",
              "value":{"leadgen_id":"9003","form_id":77,"created_time":1750000000}}]}]}
            """));

        Assert.Equal("77", submission.FormId);
    }

    [Fact]
    public void A_batch_of_several_is_read_whole()
    {
        var submissions = LeadGenWebhookParser.Parse(
            """
            {"object":"page","entry":[
              {"id":"stranica-1","changes":[
                {"field":"leadgen","value":{"leadgen_id":"1","created_time":1750000000}},
                {"field":"leadgen","value":{"leadgen_id":"2","created_time":1750000001}}]},
              {"id":"stranica-1","changes":[
                {"field":"leadgen","value":{"leadgen_id":"3","created_time":1750000002}}]}]}
            """);

        Assert.Equal(["1", "2", "3"], submissions.Select(s => s.LeadId));
    }

    [Fact]
    public void The_neighbours_noise_is_not_a_lead()
    {
        // Subscribing to one field subscribes to the rest of the Page's chatter. `feed` and
        // `messages` arrive here too, and neither is a form submission.
        Assert.Empty(LeadGenWebhookParser.Parse(
            """{"object":"page","entry":[{"id":"1","changes":[{"field":"feed","value":{"item":"status"}}]}]}"""));

        Assert.Empty(LeadGenWebhookParser.Parse(
            """{"object":"page","entry":[{"id":"1","messaging":[{"sender":{"id":"5"},"message":{"mid":"m1"}}]}]}"""));
    }

    [Fact]
    public void A_submission_with_no_id_is_skipped_rather_than_stored_empty()
    {
        // There would be nothing to fetch, and the row would be an intake nobody could ever
        // complete — it would sit in the pending list for ever looking like work.
        Assert.Empty(LeadGenWebhookParser.Parse(
            """{"object":"page","entry":[{"id":"1","changes":[{"field":"leadgen","value":{"form_id":"77"}}]}]}"""));
    }

    [Fact]
    public void A_payload_that_is_not_json_yields_nothing_rather_than_throwing()
    {
        // The stored row keeps the payload and the job stamps the error. Taking the queue down
        // over one unreadable body would stop every submission behind it.
        Assert.Empty(LeadGenWebhookParser.Parse("<html>nope</html>"));
        Assert.Empty(LeadGenWebhookParser.Parse(string.Empty));
    }

    [Fact]
    public void A_submission_with_no_time_lands_now_rather_than_in_1970()
    {
        // Otherwise it sorts to the bottom of the pending list for ever, and a lead nobody sees is
        // a lead nobody telephones — which on this feature is the entire failure.
        var submission = Assert.Single(LeadGenWebhookParser.Parse(
            """{"object":"page","entry":[{"id":"1","changes":[{"field":"leadgen","value":{"leadgen_id":"9004"}}]}]}"""));

        Assert.True(submission.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
    }
}
