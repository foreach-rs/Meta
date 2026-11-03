using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForEach.Infrastructure.External.Meta.WhatsApp;

/// <summary>
/// Meta's template list, reduced to what a send and a screen need.
///
/// Their shape is a template with an array of components — header, body, footer, buttons — of
/// which only the body carries the placeholders a send has to fill. Reading the wrong component,
/// or counting placeholders across all of them, produces a parameter count that disagrees with
/// what the API will accept, and the send is then refused with an error about parameters that
/// names no component.
/// </summary>
public static partial class WhatsAppTemplateReader
{
    /// <summary>
    /// The templates in one `message_templates` response. Empty, never an exception, for anything
    /// that is not JSON or has no `data` array; a template without a name or language is skipped.
    /// </summary>
    public static IReadOnlyList<WhatsAppTemplateInfo> Read(string payload)
    {
        var templates = new List<WhatsAppTemplateInfo>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return templates;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return templates;
            }

            foreach (var element in data.EnumerateArray())
            {
                var name = String(element, "name");
                var language = String(element, "language");

                // A template with no name is not one anything could send. Skipped rather than
                // stored under an empty key, which would collide with the next one.
                if (name is null || language is null) continue;

                var body = Body(element);

                templates.Add(new WhatsAppTemplateInfo(
                    Name: name,
                    Language: language,
                    Status: Status(String(element, "status")),
                    Category: String(element, "category"),
                    BodyText: body,
                    ParameterCount: Placeholders(body)));
            }
        }

        return templates;
    }

    /// <summary>
    /// The BODY component's text. Not the header's and not the footer's: only the body takes
    /// parameters in the shape this application sends, and a header's own {{1}} would inflate the
    /// count into a send Meta refuses.
    /// </summary>
    private static string? Body(JsonElement element)
    {
        if (!element.TryGetProperty("components", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var component in components.EnumerateArray())
        {
            if (String(component, "type") == "BODY") return String(component, "text");
        }

        return null;
    }

    /// <summary>
    /// How many distinct placeholders the wording has. Distinct, because a template that says
    /// {{1}} twice takes one parameter — counting occurrences would send two and be refused.
    /// </summary>
    private static int Placeholders(string? body) =>
        body is null
            ? 0
            : PlaceholderPattern().Matches(body)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Count();

    /// <summary>
    /// Their vocabulary, and only the values that change what we do. Anything else Meta invents —
    /// PAUSED, DISABLED, FLAGGED — lands as Unknown, which is treated as "not approved" rather
    /// than guessed either way.
    /// </summary>
    private static WhatsAppTemplateStatus Status(string? status) => status?.ToUpperInvariant() switch
    {
        "APPROVED" => WhatsAppTemplateStatus.Approved,
        "PENDING" => WhatsAppTemplateStatus.Pending,
        "REJECTED" => WhatsAppTemplateStatus.Rejected,
        _ => WhatsAppTemplateStatus.Unknown
    };

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    [GeneratedRegex(@"\{\{(\d+)\}\}")]
    private static partial Regex PlaceholderPattern();
}
