using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>
/// One form submission announced by a webhook.
/// </summary>
/// <param name="LeadId">
/// Meta's `leadgen_id`, and the only thing here that matters: the answers are not in the webhook
/// and have to be fetched with the Page token. That is deliberate on their side and convenient on
/// ours — the payload carries no personal data at all, so a forged event could at most make us
/// fetch a lead we are entitled to read anyway.
/// </param>
/// <param name="CreatedAt">
/// When the person pressed send. Carried because the fetch may happen minutes or — after a token
/// problem — days later, and "arrived at" is not the same question as "submitted at".
/// </param>
/// <param name="FormId">Meta's `form_id`, when the payload names one. What a host matches against its cached forms.</param>
/// <param name="PageId">
/// The Page the form belongs to: `page_id` from the change, or the entry's own id when the change
/// omits it, because the two are the same Page and one of them is always there.
/// </param>
/// <param name="AdId">The advertisement the submission came through, or null for a form filled in on the Page itself.</param>
public sealed record MetaLeadGenEvent(
    string LeadId,
    string? FormId,
    string? PageId,
    string? AdId,
    DateTime CreatedAt);

/// <summary>
/// The lead-gen half of the Page webhook.
///
/// Separate from the Messenger parser because it walks a different tree — `entry[].changes[]`
/// rather than `entry[].messaging[]` — and because the two answer different questions. Before this
/// existed every submission was handed to the message parser, which found nothing and said so
/// quietly: no lead, no error, and no way to tell that from a form nobody had filled in.
/// </summary>
public static class LeadGenWebhookParser
{
    /// <summary>
    /// Every submission announced in one batch, in payload order. Empty, never an exception, for
    /// a payload that is not JSON or carries no `leadgen` change.
    /// </summary>
    public static IReadOnlyList<MetaLeadGenEvent> Parse(string payload)
    {
        var events = new List<MetaLeadGenEvent>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // Not JSON at all. The host keeps the raw row and stamps the error; there is nothing
            // here to salvage and no reason to take the queue down over it.
            return events;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("entry", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return events;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("changes", out var changes)
                    || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    // Subscribing to one field subscribes to its neighbours' noise: `feed` and
                    // `mention` arrive on the same webhook and are not leads.
                    if (change.ValueKind != JsonValueKind.Object
                        || Text(change, "field") != "leadgen"
                        || !change.TryGetProperty("value", out var value)
                        || value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // No id, nothing to fetch. Skipped rather than stored as an empty intake,
                    // which would be a row nobody could ever complete.
                    if (Text(value, "leadgen_id") is not { } leadId) continue;

                    events.Add(new MetaLeadGenEvent(
                        leadId,
                        Text(value, "form_id"),
                        Text(value, "page_id") ?? Text(entry, "id"),
                        Text(value, "ad_id"),
                        Time(value)));
                }
            }
        }

        return events;
    }

    /// <summary>
    /// Meta's seconds, or now.
    ///
    /// The fallback matters for the same reason it does on a message: a submission dated at the
    /// epoch sorts to the bottom of every list for ever, and a lead nobody sees is a lead nobody
    /// telephones.
    /// </summary>
    private static DateTime Time(JsonElement value) =>
        value.TryGetProperty("created_time", out var created) && created.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        // Meta sends ids as strings, and occasionally as numbers. Reading only strings would drop
        // a form id on the one payload shaped the other way.
        return value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}
