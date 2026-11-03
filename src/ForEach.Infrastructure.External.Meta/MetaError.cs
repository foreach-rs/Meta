using System.Globalization;
using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta;

/// <summary>
/// What Meta says when a call fails: the `error` envelope every Graph endpoint answers with.
/// </summary>
/// <param name="Code">
/// The numeric code, kept as a number rather than only as an enum because Meta adds codes and a
/// caller that switches on a parsed enum must still be able to report one it does not know.
/// </param>
/// <param name="Subcode">
/// `error_subcode`. It is what separates two failures that share a code and want opposite
/// responses — 10 is a permission problem in general, and 10/2018278 is "the messaging window has
/// shut", which is not a problem at all but an answer.
/// </param>
/// <param name="Type">
/// Meta's `type` — `OAuthException`, `GraphMethodException` and the like. The family a code
/// belongs to, carried for whoever reads the message rather than switched on.
/// </param>
/// <param name="Message">
/// Meta's own wording. Shown verbatim, because on code 100 it is the only thing that says whether
/// a parameter or a permission was wrong.
/// </param>
/// <param name="TraceId">
/// `fbtrace_id`. Useless to us and the first thing Meta support asks for, which is the whole
/// reason it is carried rather than dropped.
/// </param>
public sealed record MetaError(int Code, int? Subcode, string Type, string Message, string? TraceId)
{
    /// <summary>
    /// The failure table, one row at a time.
    ///
    /// Written as a switch rather than a dictionary so an unmatched code falls through to the
    /// deliberate default below, and so each group can carry the reason it is grouped.
    /// </summary>
    public MetaFailureKind Kind => (Code, Subcode) switch
    {
        // A token that is invalid, expired or missing. Retrying spends quota to be told the same
        // thing; somebody has to reconnect the account.
        (190 or 102 or 104, _) => MetaFailureKind.Configuration,

        // The messaging window has shut, or this person cannot be written to. Both are permanent
        // until the customer writes first, and neither is anybody's mistake.
        (10, 2018278) or (10, 2018065) or (551, _) => MetaFailureKind.PolicyRefused,

        // Permission missing or not granted: code 10 without one of the subcodes above, and the
        // whole 200 range Meta uses for individual permissions. Somebody has to grant access.
        (10, _) or ( >= 200 and <= 299, _) => MetaFailureKind.Configuration,

        // WhatsApp: payment method missing / business not registered for the API. Account setup.
        (131042 or 131031, _) => MetaFailureKind.Configuration,

        // WhatsApp: bad message type, media or parameter. The request is wrong and stays wrong.
        (131008 or 131009 or 131021, _) => MetaFailureKind.CallerMistake,

        // WhatsApp's own refusals: re-engagement needs a template, recipient unavailable,
        // unsupported message type, number not in the test allow-list.
        (131047 or 131026 or 131051 or 131030, _) => MetaFailureKind.PolicyRefused,

        // Template parameter count or format. The call is wrong and will stay wrong.
        ( >= 132000 and <= 132016, _) => MetaFailureKind.CallerMistake,

        // Rate limited or throttled, in its several spellings.
        (4 or 17 or 32 or 613 or 80007 or 130429, _) => MetaFailureKind.Transient,

        // Invalid parameter *or* a missing permission — a Leads Access denial arrives as a plain
        // 100. Only the message text distinguishes them, which is why the host is expected to show
        // it verbatim rather than translate a code it cannot read precisely.
        (100, _) => MetaFailureKind.CallerMistake,

        (1 or 2 or 368, _) => MetaFailureKind.Transient,

        // An unknown code is transient on purpose, the same reasoning as NbsError: a wasted retry
        // costs one scheduled run, and giving up on something that would have worked costs the
        // message until somebody notices.
        _ => MetaFailureKind.Transient
    };

    /// <summary>
    /// One line for a log or a screen: code, subcode when there is one, type, and Meta's message.
    /// </summary>
    public override string ToString() =>
        Subcode is { } subcode
            ? $"Meta {Code}/{subcode} {Type}: {Message}"
            : $"Meta {Code} {Type}: {Message}";

    /// <summary>
    /// Reads the error out of a response body, or null when the body carries none.
    ///
    /// Null rather than a manufactured error for a body that is not JSON at all: a gateway between
    /// us and Meta answering HTML is a transport failure, and calling it "Meta 0" would put words
    /// in Meta's mouth on a screen an operator reads.
    /// </summary>
    public static MetaError? TryRead(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new MetaError(
                Number(error, "code") ?? 0,
                Number(error, "error_subcode"),
                Text(error, "type") ?? "UnknownError",
                Text(error, "message") ?? "",
                Text(error, "fbtrace_id"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt32(out var whole):
                return whole;
            case JsonValueKind.Number when value.TryGetDecimal(out var fraction)
                                          && fraction >= int.MinValue && fraction <= int.MaxValue:
                return (int)fraction;
            case JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text):
                return text;
            default:
                return null;
        }
    }
}
