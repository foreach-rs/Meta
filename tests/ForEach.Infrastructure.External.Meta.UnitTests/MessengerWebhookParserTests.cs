using ForEach.Infrastructure.External.Meta.Webhooks;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Messenger's and Instagram's envelope, reduced to the messages inside it.
///
/// A batch carries entries, each entry carries events, and most of those events are not messages:
/// delivery receipts, read receipts, reactions, typing. The rules worth holding are which of them
/// count, and — the one with a wrong answer nobody would see — which person a message belongs to.
/// </summary>
public class MessengerWebhookParserTests
{
    [Fact]
    public void A_customers_message_is_read_with_the_person_it_came_from()
    {
        var message = Assert.Single(MessengerWebhookParser.Parse(
            Batch("""
                  {"sender":{"id":"kupac-1"},"recipient":{"id":"stranica-1"},"timestamp":1750000000000,
                   "message":{"mid":"mid-1","text":"Da li radite subotom?"}}
                  """),
            MetaChannel.Messenger));

        Assert.Equal("kupac-1", message.ThreadKey);
        Assert.Equal("mid-1", message.ExternalMessageId);
        Assert.Equal("Da li radite subotom?", message.Body);
        Assert.False(message.IsEcho);
        Assert.Equal(new DateTime(2025, 6, 15, 15, 6, 40, DateTimeKind.Utc), message.SentAt);
    }

    [Fact]
    public void Our_own_reply_is_filed_under_the_customer_it_went_to()
    {
        // The failure this prevents is invisible from a screen: an echo keyed on its sender would
        // file our reply under the Page's own id and open a conversation with ourselves, while the
        // customer's thread silently stops showing what we told them.
        var message = Assert.Single(MessengerWebhookParser.Parse(
            Batch("""
                  {"sender":{"id":"stranica-1"},"recipient":{"id":"kupac-1"},"timestamp":1750000000000,
                   "message":{"mid":"mid-2","text":"Radimo do 14h.","is_echo":true}}
                  """),
            MetaChannel.Messenger));

        Assert.True(message.IsEcho);
        Assert.Equal("kupac-1", message.ThreadKey);
    }

    [Fact]
    public void An_event_that_is_not_a_message_is_not_one()
    {
        // Delivery and read receipts arrive in the same array and have senders. Taking anything
        // with a sender would fill the inbox with rows nobody wrote.
        Assert.Empty(MessengerWebhookParser.Parse(
            Batch("""{"sender":{"id":"kupac-1"},"delivery":{"mids":["mid-1"],"watermark":1750000000000}}"""),
            MetaChannel.Messenger));

        // And no mid means nothing to be idempotent on: a row that could be written twice is worse
        // than one not written at all.
        Assert.Empty(MessengerWebhookParser.Parse(
            Batch("""{"sender":{"id":"kupac-1"},"message":{"text":"bez mid-a"}}"""),
            MetaChannel.Messenger));
    }

    [Fact]
    public void A_message_that_is_only_an_attachment_still_says_something()
    {
        // Named by kind rather than left blank: "[prilog: image]" is a row somebody knows to open,
        // and an empty one is a row they scroll past.
        var message = Assert.Single(MessengerWebhookParser.Parse(
            Batch("""
                  {"sender":{"id":"kupac-1"},"message":{"mid":"mid-3",
                   "attachments":[{"type":"image","payload":{"url":"https://x/y.jpg"}}]}}
                  """),
            MetaChannel.Messenger));

        Assert.Equal("[prilog: image]", message.Body);
    }

    [Fact]
    public void One_odd_entry_does_not_cost_the_batch()
    {
        // Meta will not resend ten messages because the eleventh was strange, so the parser answers
        // with what it understood rather than throwing.
        var messages = MessengerWebhookParser.Parse(
            """
            {"object":"page","entry":[
              {"id":"1","messaging":[{"sender":{"id":"kupac-1"},"message":{"mid":"m1","text":"jedan"}}]},
              {"id":"2","nesto_drugo":{}},
              {"id":"3","messaging":[{"sender":{"id":"kupac-2"},"message":{"mid":"m2","text":"dva"}}]}]}
            """,
            MetaChannel.Messenger);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void A_payload_that_is_not_json_yields_nothing_rather_than_throwing()
    {
        // The stored row keeps the payload and the job stamps the error. Taking the queue down
        // over one unreadable body would stop every message behind it.
        Assert.Empty(MessengerWebhookParser.Parse("<html>nope</html>", MetaChannel.Messenger));
        Assert.Empty(MessengerWebhookParser.Parse(string.Empty, MetaChannel.Instagram));
    }

    [Fact]
    public void A_message_with_no_timestamp_lands_now_rather_than_in_1970()
    {
        // Otherwise it sorts to the bottom of the thread for ever, which is a message nobody reads
        // again.
        var message = Assert.Single(MessengerWebhookParser.Parse(
            Batch("""{"sender":{"id":"kupac-1"},"message":{"mid":"mid-4","text":"bez vremena"}}"""),
            MetaChannel.Instagram));

        Assert.True(message.SentAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(MetaChannel.Instagram, message.Channel);
    }

    private static string Batch(string messagingEvent) =>
        $$"""{"object":"page","entry":[{"id":"stranica-1","messaging":[{{messagingEvent}}]}]}""";
}
