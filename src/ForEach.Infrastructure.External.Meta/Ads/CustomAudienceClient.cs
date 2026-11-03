using System.Globalization;
using System.Text.Json;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.Ads;

/// <summary>
/// Pushing a list of people to a Meta Custom Audience.
///
/// The one call in this library that sends customer data *out*, and it is worth saying why that is
/// acceptable: nothing leaves as an address or a number. Every value is hashed by
/// <see cref="AudienceHash"/> first, and the host is expected to have filtered to people who gave
/// marketing consent — Meta's own terms put the burden of that permission on the advertiser, and
/// "they are already a customer" does not satisfy it for uploading personal data to a third party.
///
/// Wants `ads_management`, which is a heavier permission than `ads_read`. A host without it should
/// simply not call this rather than discover the refusal on somebody's screen.
/// </summary>
public sealed class CustomAudienceClient(
    HttpClient httpClient,
    MetaAppCredentials app,
    string adAccountId,
    string accessToken,
    TimeSpan? timeout = null)
{
    /// <summary>
    /// How many people go in one call. Meta's own documented ceiling is ten thousand; this is well
    /// under it, because a request that times out halfway has already uploaded some of the list
    /// and the host cannot tell how much.
    /// </summary>
    public const int BatchSize = 1000;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates an audience and returns its id.
    ///
    /// `USER_PROVIDED_ONLY` is the honest answer to Meta's source question and the one that
    /// matches what actually happened: these people gave us their details directly.
    /// </summary>
    public async Task<MetaResult<string>> CreateAsync(
        string name, string? description, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync(
            $"{Account}/customaudiences",
            new Dictionary<string, string>
            {
                ["name"] = name,
                ["description"] = description ?? "",
                ["subtype"] = "CUSTOM",
                ["customer_file_source"] = "USER_PROVIDED_ONLY"
            },
            ct);

        if (error is not null) return Fail<string>(error);

        try
        {
            using var document = JsonDocument.Parse(body!);

            // A 2xx that does not name the audience is an unknown outcome: it may well exist, and
            // creating it again makes a second one.
            return MetaJson.Property(document.RootElement, "id") is
                   { ValueKind: JsonValueKind.String or JsonValueKind.Number } id
                ? MetaResult<string>.Ok(id.ToString())
                : MetaResult<string>.Unknown("Meta created the audience without naming it.");
        }
        catch (JsonException)
        {
            return MetaResult<string>.Unknown("Meta answered with something that is not JSON.");
        }
    }

    /// <summary>
    /// Adds one batch of already-hashed people.
    ///
    /// The pair is positional: Meta reads the schema and then every row in the same order, so a
    /// row with the values the other way round is uploaded as a telephone number that is an
    /// address and matches nobody. Missing halves are sent as empty strings rather than dropped,
    /// because a short row would shift every value after it.
    /// </summary>
    /// <param name="audienceId">The audience to add to, as <see cref="CreateAsync"/> returned it.</param>
    /// <param name="people">Hashed e-mail and hashed telephone, either of which may be null.</param>
    /// <param name="ct">
    /// The caller's cancellation. The client's own timeout runs beside it, so the call ends on
    /// whichever comes first.
    /// </param>
    public async Task<MetaResult<int>> AddAsync(
        string audienceId,
        IReadOnlyList<(string? Email, string? Phone)> people,
        CancellationToken ct = default)
    {
        if (people.Count == 0) return MetaResult<int>.Ok(0);

        var payload = JsonSerializer.Serialize(new
        {
            schema = new[] { "EMAIL", "PHONE" },
            data = people.Select(p => new[] { p.Email ?? "", p.Phone ?? "" }).ToArray()
        });

        var (body, error) = await PostAsync(
            $"{audienceId}/users",
            new Dictionary<string, string> { ["payload"] = payload },
            ct);

        if (error is not null) return Fail<int>(error);

        try
        {
            using var document = JsonDocument.Parse(body!);

            // Meta answers with how many it actually received. Returned rather than assumed: it is
            // routinely lower than what was sent, because rows it cannot use are dropped silently
            // and a host that reported the sent count would overstate every audience it builds.
            return MetaJson.Property(document.RootElement, "num_received") is
                   { ValueKind: JsonValueKind.Number } received
                   && received.TryGetInt32(out var count)
                ? MetaResult<int>.Ok(count)
                : MetaResult<int>.Ok(people.Count);
        }
        catch (JsonException)
        {
            return MetaResult<int>.Unknown("Meta answered with something that is not JSON.");
        }
    }

    private string Account =>
        adAccountId.StartsWith("act_", StringComparison.Ordinal) ? adAccountId : "act_" + adAccountId;

    private async Task<(string? Body, MetaResult<object>? Error)> PostAsync(
        string path, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(_timeout);

            form["access_token"] = accessToken;

            using var content = new FormUrlEncodedContent(form);

            using var response = await httpClient.PostAsync(
                new Uri($"{app.GraphRoot}/{path}"), content, deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (response.IsSuccessStatusCode) return (body, null);

            return (null, MetaError.TryRead(body) is { } error
                ? MetaResult<object>.Fail(error)
                : MetaResult<object>.Transport(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Meta answered {(int)response.StatusCode}.")));
        }
        catch (HttpRequestException exception) when (!MetaJson.NeverConnected(exception))
        {
            // The request may have left; whether Meta acted on it is not known.
            return (null, MetaResult<object>.Unknown(exception.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own deadline; a caller's cancellation propagates.
            return (null, MetaResult<object>.Unknown(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s; the request may have been applied."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, MetaResult<object>.Transport(exception.Message));
        }
    }

    private static MetaResult<T> Fail<T>(MetaResult<object> error) =>
        error.Error is { } reported
            ? MetaResult<T>.Fail(reported)
            : error.IsOutcomeUnknown
                ? MetaResult<T>.Unknown(error.TransportFailure ?? "")
                : MetaResult<T>.Transport(error.TransportFailure ?? "");
}
