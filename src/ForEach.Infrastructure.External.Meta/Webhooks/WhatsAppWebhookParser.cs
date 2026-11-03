using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>
/// The Cloud API's envelope, which is not the Messenger one.
///
/// Same webhook, same signature, same stored row — and a different shape inside. Messenger puts
/// events under `entry[].messaging[]`; WhatsApp puts them under `entry[].changes[].value`, with
/// messages and delivery receipts in separate arrays and the person's name in a third. A single
/// parser trying to read both would be a switch in the middle of the loop, and the branch nobody
/// exercises is the one that silently returns nothing.
///
/// Like the Messenger parser it answers with what it understood rather than throwing. Meta will
/// not resend a batch because one entry inside it was odd.
/// </summary>
public static class WhatsAppWebhookParser
{
    /// <summary>
    /// The inbound messages in one batch. Receipts are left to <see cref="ParseStatuses"/>, and
    /// anything unrecognised is skipped rather than thrown on.
    /// </summary>
    public static IReadOnlyList<MetaInboundMessage> Parse(string payload)
    {
        var messages = new List<MetaInboundMessage>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return messages;
        }

        using (document)
        {
            foreach (var value in Values(document.RootElement))
            {
                if (!value.TryGetProperty("messages", out var inbound)
                    || inbound.ValueKind != JsonValueKind.Array)
                {
                    // A batch of nothing but delivery receipts, which is most of them. Read
                    // elsewhere; here it is simply not a message.
                    continue;
                }

                // One name for the whole change, because the Cloud API sends it once beside the
                // messages rather than on each. Without it a thread is titled with a phone number
                // — readable, unlike a Messenger id, but still not who it is.
                var names = Names(value);

                foreach (var element in inbound.EnumerateArray())
                {
                    var message = ReadMessage(element, names);

                    if (message is not null) messages.Add(message);
                }
            }
        }

        return messages;
    }

    /// <summary>
    /// The delivery receipts in the same batch.
    ///
    /// A separate pass rather than a second return value, because most batches are one or the
    /// other and a caller that had to unpack both would have to care which. Meta sends a receipt
    /// per stage per message, both ways, so this is by volume the commoner half of the webhook.
    /// </summary>
    public static IReadOnlyList<WhatsAppDeliveryReceipt> ParseStatuses(string payload)
    {
        var receipts = new List<WhatsAppDeliveryReceipt>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return receipts;
        }

        using (document)
        {
            foreach (var value in Values(document.RootElement))
            {
                if (!value.TryGetProperty("statuses", out var statuses)
                    || statuses.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in statuses.EnumerateArray())
                {
                    var id = String(element, "id");

                    if (id is null || Status(String(element, "status")) is not { } status) continue;

                    receipts.Add(new WhatsAppDeliveryReceipt(
                        id, status, Error(element), Timestamp(element)));
                }
            }
        }

        return receipts;
    }

    /// <summary>
    /// Meta's word for how far the message got.
    ///
    /// Null for anything unrecognised rather than a guess: "deleted" and "warning" are statuses
    /// too, and mapping an unknown one onto Sent would walk a read message backwards.
    /// </summary>
    private static WhatsAppDeliveryStatus? Status(string? status) => status switch
    {
        "sent" => WhatsAppDeliveryStatus.Sent,
        "delivered" => WhatsAppDeliveryStatus.Delivered,
        "read" => WhatsAppDeliveryStatus.Read,
        "failed" => WhatsAppDeliveryStatus.Failed,
        _ => null
    };

    /// <summary>
    /// Why it failed, in Meta's words. The title rather than the code, because the code is a number
    /// somebody would have to look up and the title is a sentence they can act on — "message
    /// undeliverable" and "re-engagement message" are different problems with different fixes.
    /// </summary>
    private static string? Error(JsonElement element)
    {
        if (!element.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var error in errors.EnumerateArray())
        {
            var title = String(error, "title") ?? String(error, "message");

            if (title is not null) return title;
        }

        return null;
    }

    /// <summary>
    /// Every `changes[].value` in the batch, whatever else is around it.
    ///
    /// Written defensively on purpose: this walks a payload from the public internet that has
    /// already been through a signature check but no schema, and an entry with no changes, a
    /// change with no value or a value of the wrong kind must cost the rest of the batch nothing.
    /// </summary>
    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        if (!root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Object)
                {
                    yield return value;
                }
            }
        }
    }

    private static MetaInboundMessage? ReadMessage(JsonElement element, Dictionary<string, string> names)
    {
        // No id, no message: without it there is nothing to be idempotent on, and Meta re-delivers
        // anything it did not get a prompt 200 for.
        var id = String(element, "id");
        var from = String(element, "from");

        if (id is null || from is null) return null;

        names.TryGetValue(from, out var displayName);

        return new MetaInboundMessage(
            Channel: MetaChannel.WhatsApp,

            // The number without a plus, which is what the API calls wa_id and what a reply has to
            // be addressed to. Normalising it to +381… here would produce a thread key no send
            // could use.
            ThreadKey: from,
            ExternalMessageId: id,

            // Inbound always: the Cloud API reports our own sends as statuses rather than as
            // messages, so there is no echo to recognise on this channel.
            IsEcho: false,
            From: from,
            FromDisplayName: displayName,
            Body: Body(element),
            SentAt: Timestamp(element));
    }

    /// <summary>
    /// What the message says, or what kind of thing arrived instead.
    ///
    /// A caption is preferred over the placeholder because a photographed order with "ovo nam
    /// treba za petak" written under it is the text — losing it would leave a row saying an image
    /// arrived, on a channel where the image usually is the order.
    /// </summary>
    private static string Body(JsonElement element)
    {
        var type = String(element, "type") ?? "poruka";

        if (element.TryGetProperty(type, out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            var text = String(payload, "body") ?? String(payload, "caption");

            if (text is not null) return type == "text" ? text : $"[prilog: {type}] {text}";

            // An interactive reply and a button reply both carry the words the customer tapped,
            // which read as their answer and are the only thing in the row worth showing.
            var tapped = String(payload, "title") ?? Nested(payload, "button_reply", "title")
                         ?? Nested(payload, "list_reply", "title");

            if (tapped is not null) return tapped;

            // A reaction is an emoji on somebody else's message. Kept, because a thumbs-up is a
            // yes and a thread that dropped it reads as an unanswered question.
            var emoji = String(payload, "emoji");

            if (emoji is not null) return emoji;
        }

        return $"[prilog: {type}]";
    }

    private static Dictionary<string, string> Names(JsonElement value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!value.TryGetProperty("contacts", out var contacts)
            || contacts.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var contact in contacts.EnumerateArray())
        {
            var waId = String(contact, "wa_id");

            var name = contact.TryGetProperty("profile", out var profile)
                ? String(profile, "name")
                : null;

            if (waId is not null && name is not null) names[waId] = name;
        }

        return names;
    }

    /// <summary>
    /// The Cloud API's timestamp: seconds as a string, where Messenger sends milliseconds as a
    /// number. Reading one as the other puts a message in 1970 or in fifty thousand years, and
    /// either way it leaves the thread at an end nobody scrolls to.
    /// </summary>
    private static DateTime Timestamp(JsonElement element) =>
        String(element, "timestamp") is { } value && long.TryParse(value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

    private static string? Nested(JsonElement element, string name, string inner) =>
        element.TryGetProperty(name, out var value) ? String(value, inner) : null;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
