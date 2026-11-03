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

            return MetaResult<MetaLead>.Ok(ReadLead(document.RootElement));
        }
        catch (JsonException)
        {
            return MetaResult<MetaLead>.Transport("Meta answered with something that is not JSON.");
        }
    }

    /// <summary>
    /// Every form on the Page, so a host can name them and choose which to import from.
    ///
    /// A hundred, for the same reason the template list stops there: a business with more forms
    /// than that has somebody managing them in Ads Manager, and a paging loop nobody exercises is a
    /// worse risk than a list that stops.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<MetaLeadFormInfo>>> GetFormsAsync(
        CancellationToken ct = default)
    {
        var (body, error) = await GetAsync(
            $"{page.PageId}/leadgen_forms?fields=id,name,status,questions&limit=100", ct);

        if (error is not null) return Fail<IReadOnlyList<MetaLeadFormInfo>>(error);

        var forms = new List<MetaLeadFormInfo>();

        try
        {
            using var document = JsonDocument.Parse(body!);

            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in data.EnumerateArray())
                {
                    if (Text(element, "id") is not { } id || Text(element, "name") is not { } name)
                        continue;

                    forms.Add(new MetaLeadFormInfo(
                        id,
                        name,
                        Text(element, "status"),
                        element.TryGetProperty("questions", out var questions)
                            ? questions.GetRawText()
                            : null));
                }
            }
        }
        catch (JsonException)
        {
            return MetaResult<IReadOnlyList<MetaLeadFormInfo>>.Transport(
                "Meta answered with something that is not JSON.");
        }

        return MetaResult<IReadOnlyList<MetaLeadFormInfo>>.Ok(forms);
    }

    /// <summary>
    /// The submissions of one form since a moment, newest first as Meta returns them.
    ///
    /// This is what makes a missed webhook recoverable — and what imports the leads that arrived
    /// before the integration was switched on at all. `since` is Unix seconds, Meta's own filter,
    /// so the window is applied on their side rather than by fetching everything and discarding.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<MetaLead>>> GetFormLeadsAsync(
        string formId, DateTime since, CancellationToken ct = default)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc))
            .ToUnixTimeSeconds();

        var (body, error) = await GetAsync(
            $"{formId}/leads?fields={LeadFields}&filtering="
            + $"[{{\"field\":\"time_created\",\"operator\":\"GREATER_THAN\",\"value\":{seconds}}}]"
            + "&limit=100",
            ct);

        if (error is not null) return Fail<IReadOnlyList<MetaLead>>(error);

        var leads = new List<MetaLead>();

        try
        {
            using var document = JsonDocument.Parse(body!);

            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                leads.AddRange(data.EnumerateArray().Select(ReadLead).Where(lead => lead.Id.Length > 0));
            }
        }
        catch (JsonException)
        {
            return MetaResult<IReadOnlyList<MetaLead>>.Transport(
                "Meta answered with something that is not JSON.");
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
        catch (Exception exception)
            when (exception is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return MetaResult<bool>.Transport(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return MetaResult<bool>.Transport(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s.");
        }
    }

    private static MetaLead ReadLead(JsonElement element) =>
        new(Text(element, "id") ?? "",
            Time(element),
            Text(element, "form_id"),
            Text(element, "ad_id"),
            Text(element, "ad_name"),
            Text(element, "adset_id"),
            Text(element, "adset_name"),
            Text(element, "campaign_id"),
            Text(element, "campaign_name"),
            element.TryGetProperty("is_organic", out var organic)
            && organic.ValueKind == JsonValueKind.True,
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

            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';

            using var response = await httpClient.GetAsync(
                $"{app.GraphRoot}/{path}{separator}access_token="
                + Uri.EscapeDataString(page.PageAccessToken),
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
}
