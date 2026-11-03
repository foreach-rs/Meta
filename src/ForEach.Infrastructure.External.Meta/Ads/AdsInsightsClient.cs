using System.Globalization;
using System.Text.Json;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.Ads;

/// <summary>
/// What an advertising campaign cost and what it produced, a day at a time.
///
/// A separate token from everything else in this library: insights hang off an ad account rather
/// than a Page, and the permission is `ads_read` on a user or system user. A host with a Page
/// connection and no ads permission can do everything else here and none of this — which is why
/// the token is a parameter rather than read from the page credentials.
///
/// Day by day on purpose. Meta revises figures for up to 28 days as attribution windows close, so
/// the only shape that can be corrected is one row per campaign per day, re-read and overwritten.
/// </summary>
public sealed class AdsInsightsClient(
    HttpClient httpClient,
    MetaAppCredentials app,
    string adAccountId,
    string accessToken,
    TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    private const string Fields =
        "campaign_id,campaign_name,spend,impressions,clicks,actions,date_start";

    /// <summary>
    /// Every campaign's daily figures between two dates, inclusive.
    ///
    /// One page. A month of a handful of campaigns fits comfortably, and a paging loop nobody
    /// exercises is a worse risk than a window that has to be asked for twice.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<MetaAdInsight>>> GetDailyAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var range = JsonSerializer.Serialize(new
        {
            since = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            until = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        var (body, error) = await GetAsync(
            $"{Account}/insights?level=campaign&time_increment=1&limit=500"
            + $"&fields={Fields}&time_range={Uri.EscapeDataString(range)}",
            ct);

        if (error is not null) return Fail<IReadOnlyList<MetaAdInsight>>(error);

        try
        {
            using var document = JsonDocument.Parse(body!);

            var rows = new List<MetaAdInsight>();

            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in data.EnumerateArray())
                {
                    if (Read(element) is { } insight) rows.Add(insight);
                }
            }

            return MetaResult<IReadOnlyList<MetaAdInsight>>.Ok(rows);
        }
        catch (JsonException)
        {
            return MetaResult<IReadOnlyList<MetaAdInsight>>.Transport(
                "Meta answered with something that is not JSON.");
        }
    }

    /// <summary>The account's currency, so a host does not assume the figures are in its own.</summary>
    public async Task<MetaResult<string>> GetCurrencyAsync(CancellationToken ct = default)
    {
        var (body, error) = await GetAsync($"{Account}?fields=currency", ct);

        if (error is not null) return Fail<string>(error);

        try
        {
            using var document = JsonDocument.Parse(body!);

            return MetaResult<string>.Ok(Text(document.RootElement, "currency") ?? "");
        }
        catch (JsonException)
        {
            return MetaResult<string>.Transport("Meta answered with something that is not JSON.");
        }
    }

    /// <summary>Meta wants the account id prefixed, and hosts store it both ways.</summary>
    private string Account =>
        adAccountId.StartsWith("act_", StringComparison.Ordinal) ? adAccountId : "act_" + adAccountId;

    private static MetaAdInsight? Read(JsonElement element)
    {
        var campaignId = Text(element, "campaign_id");

        if (campaignId is null) return null;

        if (Text(element, "date_start") is not { } day
            || !DateOnly.TryParse(day, CultureInfo.InvariantCulture, out var date))
        {
            // Without a date the row cannot be stored idempotently, which is the whole point of
            // asking day by day. Dropped rather than dated to today and double-counted tomorrow.
            return null;
        }

        return new MetaAdInsight(
            campaignId,
            Text(element, "campaign_name") ?? campaignId,
            date,
            Money(element, "spend"),
            Count(element, "impressions"),
            Count(element, "clicks"),
            Results(element));
    }

    /// <summary>
    /// The result count, out of the actions array.
    ///
    /// Three spellings, because Meta reports a lead from an Instant Form as `leadgen.other`, one
    /// from an on-site form as `lead`, and the grouped variant on newer objectives — and a host
    /// reading only one of them shows nought results for a campaign that is working.
    /// </summary>
    private static long Results(JsonElement element)
    {
        if (!element.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0L;

        foreach (var action in actions.EnumerateArray())
        {
            if (Text(action, "action_type")
                is not ("lead" or "leadgen.other" or "onsite_conversion.lead_grouped"))
            {
                continue;
            }

            total += Count(action, "value");
        }

        return total;
    }

    private async Task<(string? Body, MetaResult<object>? Error)> GetAsync(
        string path, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(_timeout);

            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';

            using var response = await httpClient.GetAsync(
                $"{app.GraphRoot}/{path}{separator}access_token="
                + Uri.EscapeDataString(accessToken),
                deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (response.IsSuccessStatusCode) return (body, null);

            return (null, MetaError.TryRead(body) is { } error
                ? MetaResult<object>.Fail(error)
                : MetaResult<object>.Transport($"Meta answered {(int)response.StatusCode}."));
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return (null, MetaResult<object>.Transport(exception.Message));
        }
        catch (OperationCanceledException)
        {
            return (null, MetaResult<object>.Transport(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s."));
        }
    }

    private static MetaResult<T> Fail<T>(MetaResult<object> error) =>
        error.Error is { } reported
            ? MetaResult<T>.Fail(reported)
            : MetaResult<T>.Transport(error.TransportFailure ?? "");

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    /// <summary>
    /// Spend arrives as a decimal string with a point, whatever the account's locale.
    ///
    /// Parsed invariantly for exactly that reason: a server running under a Serbian culture would
    /// otherwise read "12.50" as twelve and a half thousand, and the report would say a campaign
    /// cost a thousand times what it did.
    /// </summary>
    private static decimal Money(JsonElement element, string name) =>
        Text(element, name) is { } value
        && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private static long Count(JsonElement element, string name) =>
        Text(element, name) is { } value
        && long.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L;
}
