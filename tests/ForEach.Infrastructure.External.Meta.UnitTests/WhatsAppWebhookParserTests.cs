using ForEach.Infrastructure.External.Meta.Webhooks;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// The Cloud API's envelope, which is not the Messenger one.
///
/// Same webhook and same stored row, different shape inside — messages under
/// `entry[].changes[].value.messages[]`, the person's name in a `contacts` array beside them, and
/// timestamps in seconds where Messenger sends milliseconds. Each of those differences fails
/// quietly if it is read as the other: a thread titled with a phone number, a message dated 1970,
/// or an inbox that stays empty while Meta reports every delivery as a success.
/// </summary>
public class WhatsAppWebhookParserTests
{
    private const string Customer = "381641234567";

    [Fact]
    public void A_text_message_becomes_one_inbound_message_from_the_number()
    {
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"text","text":{"body":"Treba nam 5000 kesica"}}""")));

        Assert.Equal(MetaChannel.WhatsApp, message.Channel);

        // Never an echo on this channel: the Cloud API reports our own sends as statuses rather
        // than as messages, so there is nothing here to mistake for one.
        Assert.False(message.IsEcho);

        // The number without a plus — what the API calls wa_id, and what a reply has to be
        // addressed to. Normalising it to +381… would produce a thread key no send could use.
        Assert.Equal(Customer, message.ThreadKey);
        Assert.Equal("Treba nam 5000 kesica", message.Body);
        Assert.Equal("Milan Petrović", message.FromDisplayName);
    }

    [Fact]
    public void The_timestamp_is_read_as_seconds_rather_than_milliseconds()
    {
        // Messenger sends milliseconds and WhatsApp sends seconds, as a string. Reading one as the
        // other puts the message in 1970 or in fifty thousand years — either way at an end of the
        // thread nobody scrolls to.
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"text","text":{"body":"Zdravo"}}""")));

        Assert.Equal(new DateTime(2025, 6, 15, 15, 6, 40, DateTimeKind.Utc), message.SentAt);
    }

    [Fact]
    public void A_photo_with_a_caption_keeps_the_caption()
    {
        // On this channel the photograph usually IS the order, and the words under it are the
        // instruction. A row saying only that an image arrived loses the half that can be read.
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"image","image":{"id":"media-1","caption":"Ovo nam treba za petak"}}""")));

        Assert.Contains("Ovo nam treba za petak", message.Body, StringComparison.Ordinal);
        Assert.Contains("image", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_with_no_caption_says_what_arrived()
    {
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"document","document":{"id":"media-1","mime_type":"application/pdf"}}""")));

        Assert.Equal("[prilog: document]", message.Body);
    }

    [Fact]
    public void A_tapped_button_reads_as_the_words_on_it()
    {
        // The customer answered; they just did not type. Storing "[prilog: interactive]" would
        // leave a thread whose reply is invisible and whose question looks unanswered.
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"interactive","interactive":{"type":"button_reply","button_reply":{"id":"da","title":"Da, potvrđujem"}}}""")));

        Assert.Equal("Da, potvrđujem", message.Body);
    }

    [Fact]
    public void A_reaction_is_kept_because_a_thumbs_up_is_an_answer()
    {
        var message = Assert.Single(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"reaction","reaction":{"message_id":"wamid.XYZ","emoji":"👍"}}""")));

        Assert.Equal("👍", message.Body);
    }

    [Fact]
    public void A_batch_of_nothing_but_delivery_receipts_yields_no_messages()
    {
        // Most batches are these. Taking a status as a message would fill the inbox with rows
        // nobody wrote — and with the customer's number as their own words.
        Assert.Empty(WhatsAppWebhookParser.Parse(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.ABC","status":"delivered","timestamp":"1750000000","recipient_id":"381641234567"}]}}]}]}"""));
    }

    [Fact]
    public void A_message_with_no_id_is_not_a_message()
    {
        // Without an id there is nothing to be idempotent on, and Meta re-delivers anything it did
        // not get a prompt 200 for — so an id-less row would arrive again on the next retry.
        Assert.Empty(WhatsAppWebhookParser.Parse(Payload(
            """{"from":"381641234567","timestamp":"1750000000","type":"text","text":{"body":"Zdravo"}}""")));
    }

    [Fact]
    public void One_odd_change_does_not_cost_the_rest_of_the_batch()
    {
        // Meta will not resend a batch for the sake of one entry inside it.
        var messages = WhatsAppWebhookParser.Parse(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"field":"account_update"}]},{"nesto_drugo":true},{"changes":[{"value":{"messages":[{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"text","text":{"body":"Prva"}}]}}]}]}""");

        Assert.Equal("Prva", Assert.Single(messages).Body);
    }

    [Fact]
    public void A_payload_that_is_not_json_yields_nothing_rather_than_throwing()
    {
        // The row keeps the payload and the job stamps the error. There is nothing here to salvage
        // and no reason to take the queue down over it.
        Assert.Empty(WhatsAppWebhookParser.Parse("<html>nope</html>"));
    }

    [Fact]
    public void A_number_we_have_no_profile_name_for_is_still_ingested()
    {
        var message = Assert.Single(WhatsAppWebhookParser.Parse(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"messages":[{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"text","text":{"body":"Zdravo"}}]}}]}]}"""));

        Assert.Null(message.FromDisplayName);
        Assert.Equal("Zdravo", message.Body);
    }

    [Fact]
    public void A_status_batch_is_read_as_receipts()
    {
        var receipts = WhatsAppWebhookParser.ParseStatuses(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.OUT","status":"read","timestamp":"1750000000","recipient_id":"381641234567"}]}}]}]}""");

        var receipt = Assert.Single(receipts);
        Assert.Equal("wamid.OUT", receipt.ExternalMessageId);
        Assert.Equal(WhatsAppDeliveryStatus.Read, receipt.Status);
        Assert.Null(receipt.Error);
    }

    [Fact]
    public void A_failure_carries_Metas_own_reason()
    {
        // The title rather than the code: "Re-engagement message" tells somebody what to do next,
        // and 131047 is a number they would have to look up.
        var receipt = Assert.Single(WhatsAppWebhookParser.ParseStatuses(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.OUT","status":"failed","timestamp":"1750000000","errors":[{"code":131047,"title":"Re-engagement message"}]}]}}]}]}"""));

        Assert.Equal(WhatsAppDeliveryStatus.Failed, receipt.Status);
        Assert.Equal("Re-engagement message", receipt.Error);
    }

    [Fact]
    public void A_status_nobody_recognises_is_dropped_rather_than_guessed()
    {
        // "deleted" and "warning" are statuses too. Mapping an unknown one onto Sent would walk a
        // read message backwards, which is the one thing this pipeline must never do.
        Assert.Empty(WhatsAppWebhookParser.ParseStatuses(
            """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.OUT","status":"deleted","timestamp":"1750000000"}]}}]}]}"""));
    }

    [Fact]
    public void A_batch_of_messages_carries_no_receipts()
    {
        Assert.Empty(WhatsAppWebhookParser.ParseStatuses(Payload(
            """{"from":"381641234567","id":"wamid.ABC","timestamp":"1750000000","type":"text","text":{"body":"Zdravo"}}""")));
    }

    private static string Payload(string message) =>
        """{"object":"whatsapp_business_account","entry":[{"changes":[{"value":{"contacts":[{"wa_id":"381641234567","profile":{"name":"Milan Petrović"}}],"messages":["""
        + message
        + "]}}]}]}";
}
