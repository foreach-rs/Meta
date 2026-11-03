using System.Text.Json;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.Leads;

/// <summary>
/// Reading submitted Instant Forms.
///
/// The webhook announces an id and nothing else, so this is where the lead actually arrives. It
/// wants `leads_retrieval` and Leads Access on the Page, and the absence of either is the one
/// failure worth recognising by sight: Meta reports it as a plain code 100 whose message is the
/// only thing distinguishing it from a wrong parameter, which is why the host is expected to show
/// that message rather than translate the code.
/// </summary>
public sealed class LeadAdsClient(
    HttpClient httpClient,
    MetaAppCredentials app,
    MetaPageCredentials page,
    TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    private const string LeadFields =
        "id,created_time,ad_id,ad_name,adset_id,adset_name,campaign_id,campaign_name,"
        + "form_id,is_organic,platform,field_data";

    /// <summary>One submission, by the id the webhook carried.</summary>
    public async Task<MetaResult<MetaLead>> GetLeadAsync(string leadId, CancellationToken ct = default)
    {
        var (body, error) = await GetAsync($"{leadId}?fields={LeadFields}", ct);

        if (error is not null) return Fail<MetaLead>(error);

        try
        {
            using var document = JsonDocument.Parse(body!);

            return ReadLead(document.RootElement) is { } lead
                ? MetaResult<MetaLead>.Ok(lead)
                : MetaResult<MetaLead>.Transport("Meta answered with a lead that has no id.");
        }
        catch (JsonException)
        {
            return MetaResult<MetaLead>.Transport("Meta answered with something that is not JSON.");
        }
    }

    /// <summary>
    /// Every form on the Page, so a host can name them and choose which to import from.
    ///
    /// Pages of a hundred, followed through `paging.next` until Meta stops offering one, and never
    /// past fifty pages — a guard against a cursor that does not end, not a limit anyone should meet.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<MetaLeadFormInfo>>> GetFormsAsync(
        CancellationToken ct = default)
    {
        var forms = new List<MetaLeadFormInfo>();

        string? next = $"{page.PageId}/leadgen_forms?fields=id,name,status,questions&limit=100";

        for (var pages = 0; next is not null && pages < MetaJson.MaxPages; pages++)
        {
            var (body, error) = await GetAsync(next, ct);

            if (error is not null) return Fail<IReadOnlyList<MetaLeadFormInfo>>(error);

            try
            {
                using var document = JsonDocument.Parse(body!);

                if (MetaJson.Property(document.RootElement, "data") is { ValueKind: JsonValueKind.Array } data)
                {
                    foreach (var element in data.EnumerateArray())
                    {
                        if (Text(element, "id") is not { } id || Text(element, "name") is not { } name)
                            continue;

                        forms.Add(new MetaLeadFormInfo(
                            id,
                            name,
                            Text(element, "status"),
                            MetaJson.Property(element, "questions") is { } questions
                                ? questions.GetRawText()
                                : null));
                    }
                }

                next = MetaJson.NextPage(document.RootElement, app.GraphRoot);
            }
            catch (JsonException)
            {
                return MetaResult<IReadOnlyList<MetaLeadFormInfo>>.Transport(
                    "Meta answered with something that is not JSON.");
            }
        }

        return MetaResult<IReadOnlyList<MetaLeadFormInfo>>.Ok(forms);
    }

    /// <summary>
    /// The submissions of one form since a moment, newest first as Meta returns them.
    ///
    /// This is what makes a missed webhook recoverable — and what imports the leads that arrived
    /// before the integration was switched on at all. `since` is Unix seconds, Meta's own filter,
    /// so the window is applied on their side rather than by fetching everything and discarding.
    /// Pages of a hundred are followed through `paging.next`, up to fifty pages.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<MetaLead>>> GetFormLeadsAsync(
        string formId, DateTime since, CancellationToken ct = default)
    {
        // Local becomes UTC properly; Unspecified is taken as UTC, which is what a host storing
        // UTC without a Kind means.
        var utc = since.Kind == DateTimeKind.Local
            ? since.ToUniversalTime()
            : DateTime.SpecifyKind(since, DateTimeKind.Utc);

        var seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();

        var leads = new List<MetaLead>();

        string? next =
            $"{formId}/leads?fields={LeadFields}&filtering="
            + $"[{{\"field\":\"time_created\",\"operator\":\"GREATER_THAN\",\"value\":{seconds}}}]"
            + "&limit=100";

        for (var pages = 0; next is not null && pages < MetaJson.MaxPages; pages++)
        {
            var (body, error) = await GetAsync(next, ct);

            if (error is not null) return Fail<IReadOnlyList<MetaLead>>(error);

            try
            {
                using var document = JsonDocument.Parse(body!);

                if (MetaJson.Property(document.RootElement, "data") is { ValueKind: JsonValueKind.Array } data)
                {
                    leads.AddRange(data.EnumerateArray().Select(ReadLead).OfType<MetaLead>());
                }

                next = MetaJson.NextPage(document.RootElement, app.GraphRoot);
            }
            catch (JsonException)
            {
                return MetaResult<IReadOnlyList<MetaLead>>.Transport(
                    "Meta answered with something that is not JSON.");
            }
        }

        return MetaResult<IReadOnlyList<MetaLead>>.Ok(leads);
    }

    /// <summary>
    /// Subscribes the app to the Page's fields, `leadgen` among them.
    ///
    /// Without this the webhook never fires and everything downstream is correct and idle — which
    /// looks exactly like a form nobody has filled in, and is the commonest reason a lead
    /// integration appears not to work.
    /// </summary>
    public async Task<MetaResult<bool>> SubscribeFieldsAsync(
        IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(_timeout);

        try
        {
            using var response = await httpClient.PostAsync(
                $"{app.GraphRoot}/{page.PageId}/subscribed_apps"
                + $"?subscribed_fields={Uri.EscapeDataString(string.Join(",", fields))}"
                + $"&access_token={Uri.EscapeDataString(page.PageAccessToken)}",
                null,
                deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (response.IsSuccessStatusCode) return MetaResult<bool>.Ok(true);

            return MetaError.TryRead(body) is { } error
                ? MetaResult<bool>.Fail(error)
                : MetaResult<bool>.Transport($"Meta answered {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MetaResult<bool>.Transport(exception.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MetaResult<bool>.Transport(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>The lead, or null when the element is not an object or carries no id.</summary>
    private static MetaLead? ReadLead(JsonElement element) =>
        element.ValueKind != JsonValueKind.Object || Text(element, "id") is not { } id
            ? null
            : ReadLead(element, id);

    private static MetaLead ReadLead(JsonElement element, string id) =>
        new(id,
            Time(element),
            Text(element, "form_id"),
            Text(element, "ad_id"),
            Text(element, "ad_name"),
            Text(element, "adset_id"),
            Text(element, "adset_name"),
            Text(element, "campaign_id"),
            Text(element, "campaign_name"),
            MetaJson.Property(element, "is_organic") is { ValueKind: JsonValueKind.True },
            Text(element, "platform"),
            ReadFields(element));

    private static IReadOnlyList<MetaLeadField> ReadFields(JsonElement element)
    {
        var fields = new List<MetaLeadField>();

        if (!element.TryGetProperty("field_data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return fields;
        }

        foreach (var field in data.EnumerateArray())
        {
            if (Text(field, "name") is not { } name) continue;

            var values = new List<string>();

            if (field.TryGetProperty("values", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(array.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!));
            }

            fields.Add(new MetaLeadField(name, values));
        }

        return fields;
    }

    /// <summary>
    /// `created_time`, which on this edge is an ISO string rather than the seconds the webhook
    /// sends. Reading one as the other is how a lead lands in 1970 and is never seen again.
    /// </summary>
    private static DateTime Time(JsonElement element) =>
        Text(element, "created_time") is { } value
        && DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.UtcDateTime
            : DateTime.UtcNow;

    private async Task<(string? Body, MetaResult<object>? Error)> GetAsync(
        string path, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(_timeout);

            // A `paging.next` URL is absolute and already carries its token.
            var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
              && Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                ? absolute.AbsoluteUri
                : $"{app.GraphRoot}/{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}"
                  + $"access_token={Uri.EscapeDataString(page.PageAccessToken)}";

            using var response = await httpClient.GetAsync(url, deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (response.IsSuccessStatusCode) return (body, null);

            return (null, MetaError.TryRead(body) is { } error
                ? MetaResult<object>.Fail(error)
                : MetaResult<object>.Transport($"Meta answered {(int)response.StatusCode}."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, MetaResult<object>.Transport(exception.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
}
