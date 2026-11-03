using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>What a webhook batch is about, before anybody parses it.</summary>
public enum MetaWebhookKind
{
    /// <summary>Messages, echoes and delivery receipts — the conversation surfaces.</summary>
    Messaging,

    /// <summary>Somebody submitted an Instant Form. The lead itself is fetched afterwards.</summary>
    LeadGen,

    /// <summary>A subscription field nobody here reads. Stored, never parsed.</summary>
    Unknown
}

/// <summary>
/// What a batch is and which channel it came from, read from the envelope before any parser sees it.
/// </summary>
/// <param name="Kind">
/// Messaging and lead submissions arrive on the same URL under the same `object: "page"`, and only
/// the field name inside the entry tells them apart. A batch filed as the wrong kind is one no job
/// picks up — indistinguishable from a message that never arrived, with no error anywhere to lead
/// somebody to it.
/// </param>
/// <param name="Channel">
/// Null for a kind that has no channel — a lead is not a conversation.
/// </param>
/// <param name="EntryIds">
/// The `entry[].id` values: the Page or WhatsApp Business Account ids the batch is about. A relay
/// serving several installs routes on these, which is the one thing that has to be readable
/// without parsing the payload properly.
/// </param>
public sealed record MetaWebhookEnvelopeInfo(
    MetaWebhookKind Kind,
    MetaChannel? Channel,
    IReadOnlyList<string> EntryIds);

/// <summary>
/// Reads the envelope of a Meta webhook batch.
///
/// One URL carries three products and two kinds of event, and this decides which parser will ever
/// see the stored row. It is a decision with a wrong answer, which is why it is here and tested
/// rather than inlined in whatever endpoint receives the call.
/// </summary>
public static class MetaWebhookEnvelope
{
    /// <summary>
    /// What a batch is and which parser gets it, decided from the envelope alone. Never throws:
    /// a payload that is not JSON is filed under Messenger so the stored row survives to be read.
    /// </summary>
    public static MetaWebhookEnvelopeInfo Classify(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            var root = document.RootElement;

            var envelope = root.ValueKind == JsonValueKind.Object
                           && root.TryGetProperty("object", out var value)
                           && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            var entries = EntryIds(root);

            // Lead submissions come in under `object: "page"` like Messenger's do, and are told
            // apart only by the changed field. Checked first for that reason: the channel test
            // below would otherwise claim every one of them for Messenger, whose parser reads a
            // different tree and finds nothing — the silent drop this classification exists to end.
            if (HasField(root, "leadgen"))
                return new MetaWebhookEnvelopeInfo(MetaWebhookKind.LeadGen, null, entries);

            return envelope switch
            {
                "instagram" => new MetaWebhookEnvelopeInfo(
                    MetaWebhookKind.Messaging, MetaChannel.Instagram, entries),

                "whatsapp_business_account" => new MetaWebhookEnvelopeInfo(
                    MetaWebhookKind.Messaging, MetaChannel.WhatsApp, entries),

                // "page" and anything unrecognised. Messenger's parser reads an envelope it does
                // not understand as no messages, which is the harmless outcome — where guessing
                // WhatsApp for an unknown shape would send it to a parser that reads a different
                // tree entirely.
                _ => new MetaWebhookEnvelopeInfo(
                    MetaWebhookKind.Messaging, MetaChannel.Messenger, entries)
            };
        }
        catch (JsonException)
        {
            // Unparseable, and still classified under the commonest of the three rather than
            // refused. The stored row is evidence: a payload that is not JSON at all is worth
            // having when somebody asks why nothing arrived, and discarding it here would answer
            // that question with silence.
            return new MetaWebhookEnvelopeInfo(MetaWebhookKind.Messaging, MetaChannel.Messenger, []);
        }
    }

    /// <summary>Whether any entry announces this subscription field.</summary>
    private static bool HasField(JsonElement root, string field)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entry", out var entry)
            || entry.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in entry.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.ValueKind == JsonValueKind.Object
                    && change.TryGetProperty("field", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && string.Equals(name.GetString(), field, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> EntryIds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entry", out var entry)
            || entry.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();

        foreach (var item in entry.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } text)
            {
                ids.Add(text);
            }
        }

        return ids;
    }
}
