using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>
/// Meta's webhook envelope, reduced to the messages inside it.
///
/// A batch carries entries, each entry carries events, and most of those events are not messages
/// at all: delivery receipts, read receipts, reactions, typing. Taking anything with a sender as a
/// message would fill the inbox with rows nobody wrote — so this looks for a message id and
/// ignores everything without one.
///
/// It never throws on a shape it does not recognise. A batch of ten with one odd entry has to
/// yield the nine, because Meta will not resend the batch for the sake of the one — and the whole
/// point of storing the raw payload first is that nothing is lost when a parser is surprised.
/// </summary>
public static class MessengerWebhookParser
{
    /// <summary>
    /// The messages in one batch, filed under the channel the envelope was classified as. Empty,
    /// never an exception, for anything it does not recognise.
    /// </summary>
    public static IReadOnlyList<MetaInboundMessage> Parse(string payload, MetaChannel channel)
    {
        var messages = new List<MetaInboundMessage>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Not JSON at all. The row keeps the payload and the job stamps the error; there is
            // nothing here to salvage and no reason to take the queue down over it.
            return messages;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("entry", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return messages;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("messaging", out var events)
                    || events.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in events.EnumerateArray())
                {
                    var message = ReadMessage(element, channel);

                    if (message is not null) messages.Add(message);
                }
            }
        }

        return messages;
    }

    private static MetaInboundMessage? ReadMessage(JsonElement element, MetaChannel channel)
    {
        if (!element.TryGetProperty("message", out var message)) return null;

        // No mid, no message. Without it there is nothing to be idempotent on, and a row that
        // could be written twice is worse than one not written at all.
        var mid = String(message, "mid");

        if (mid is null) return null;

        var isEcho = message.TryGetProperty("is_echo", out var echo)
                     && echo.ValueKind == JsonValueKind.True;

        var sender = String(element.TryGetProperty("sender", out var from) ? from : default, "id");
        var recipient = String(element.TryGetProperty("recipient", out var to) ? to : default, "id");

        // The thread is the person, in both directions. On an echo the customer is the RECIPIENT —
        // keying that on the sender would file our own reply under the Page's id and start a
        // conversation with ourselves.
        var counterpart = isEcho ? recipient : sender;

        if (counterpart is null) return null;

        var text = String(message, "text");

        // A message can be an attachment and nothing else. The placeholder is what keeps it from
        // reading as an empty row: something arrived, and somebody has to open it to see what.
        var body = text ?? AttachmentPlaceholder(message);

        // Raw, and clipped by nobody here: how long a body may be is a property of the host's
        // database rather than of Meta's payload, and a library that decided it would be wrong for
        // the next product by exactly the width of its own columns.
        return new MetaInboundMessage(
            Channel: channel,
            ThreadKey: counterpart,
            ExternalMessageId: mid,
            IsEcho: isEcho,
            From: counterpart,
            FromDisplayName: null,
            Body: body,
            SentAt: Timestamp(element));
    }

    /// <summary>
    /// What an attachment-only message says in the list. Named by kind rather than left blank:
    /// "[prilog: image]" is a row somebody knows to open, and an empty one is a row they skip.
    /// </summary>
    private static string AttachmentPlaceholder(JsonElement message)
    {
        if (!message.TryGetProperty("attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Array)
        {
            return "[poruka bez teksta]";
        }

        var kinds = attachments
            .EnumerateArray()
            .Select(attachment => String(attachment, "type") ?? "prilog")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return kinds.Count == 0 ? "[poruka bez teksta]" : $"[prilog: {string.Join(", ", kinds)}]";
    }

    /// <summary>
    /// Meta's millisecond timestamp, or now.
    ///
    /// The fallback matters: a message with no time would otherwise land at the epoch and sort to
    /// the bottom of the thread for ever, which is a message nobody sees again.
    /// </summary>
    private static DateTime Timestamp(JsonElement element) =>
        element.TryGetProperty("timestamp", out var value) && value.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
            : DateTime.UtcNow;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
