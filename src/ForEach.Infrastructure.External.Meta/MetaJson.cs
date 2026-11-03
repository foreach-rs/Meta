using System.Globalization;
using System.Text.Json;

namespace ForEach.Infrastructure.External.Meta;

/// <summary>Small readers shared by the parsers and clients, none of which throws on a wrong shape.</summary>
internal static class MetaJson
{
    /// <summary>The latest instant DateTimeOffset accepts, in Unix seconds (year 9999).</summary>
    private const long MaxSeconds = 253402300799;

    /// <summary>The property when the parent is an object and has it; otherwise null.</summary>
    public static JsonElement? Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : null;

    /// <summary>
    /// The most pages a list is followed through. A guard against a cursor that never ends, not a
    /// limit anybody should meet: fifty pages of a hundred is five thousand rows.
    /// </summary>
    public const int MaxPages = 50;

    /// <summary>
    /// The `paging.next` URL of a Graph list, or null when there is none — or when it points
    /// anywhere but the Graph host the client was configured with, since a cursor is followed
    /// with our token attached and must not lead the request somewhere else.
    /// </summary>
    public static string? NextPage(JsonElement root, Uri graphRoot)
    {
        if (Property(root, "paging") is not { } paging
            || Property(paging, "next") is not { ValueKind: JsonValueKind.String } next
            || !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Authority, graphRoot.Authority, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Scheme, graphRoot.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <summary>True for a request that provably never reached Meta, so repeating it is safe.</summary>
    public static bool NeverConnected(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

    /// <summary>A Unix time given as a number or a numeric string, in seconds or milliseconds; null if absent or out of range.</summary>
    public static DateTime? UnixTime(JsonElement parent, string name, bool milliseconds)
    {
        if (Property(parent, name) is not { } value) return null;

        long raw;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                raw = number;
                break;
            case JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                raw = parsed;
                break;
            default:
                return null;
        }

        var seconds = milliseconds ? raw / 1000 : raw;

        return raw < 0 || seconds > MaxSeconds
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }
}
