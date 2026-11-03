using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.WhatsApp;

/// <summary>
/// The WhatsApp Cloud API over HTTP.
///
/// The same host as the Page client and almost nothing else in common: the token authorises a
/// business account rather than a Page, it travels as a bearer header rather than a query string,
/// and the reply is addressed to a phone number. Sharing a class with the Page client would mean
/// one object holding two tokens, each valid for exactly the calls the other cannot make.
///
/// Cheap to construct per call, holds no state, caches no credentials, and retries nothing — a
/// library that retried on its own would do it inside a request somebody is waiting on.
/// </summary>
public sealed class WhatsAppCloudClient(
    HttpClient httpClient,
    MetaAppCredentials app,
    WhatsAppCredentials whatsApp,
    TimeSpan? timeout = null)
{
    /// <summary>Ten seconds by default. A reply is typed by somebody waiting at a screen.</summary>
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    /// <summary>
    /// A free-text message, allowed only within 24 hours of the customer last writing. Outside
    /// that window Meta refuses it as 131047, and the answer is <see cref="SendTemplateAsync"/>.
    /// </summary>
    public Task<MetaResult<string>> SendTextAsync(
        string to, string text, CancellationToken ct = default) =>
        SendAsync(new
        {
            // Required by the API on every request, and the one field nothing else sets. Omitting
            // it answers "unsupported post request", which reads like a wrong URL.
            messaging_product = "whatsapp",
            to,
            type = "text",
            text = new { body = text }
        }, ct);

    /// <summary>
    /// An approved template, the only thing that may be sent to somebody who has not written in
    /// the last 24 hours. The parameters fill the body's {{n}} placeholders in order and must
    /// number exactly <see cref="WhatsAppTemplateInfo.ParameterCount"/>, or Meta refuses the send.
    /// </summary>
    public Task<MetaResult<string>> SendTemplateAsync(
        string to,
        string templateName,
        string language,
        IReadOnlyList<string> parameters,
        CancellationToken ct = default) =>
        SendAsync(new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = templateName,

                // The language of the approved template, not of the customer. Meta matches on it,
                // and a mismatch is "template name does not exist" — an error that sends whoever
                // reads it looking for a template that is sitting right there, approved.
                language = new { code = language },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = parameters
                            .Select(value => new { type = "text", text = value })
                            .ToArray()
                    }
                }
            }
        }, ct);

    /// <summary>
    /// Every template on the business account, approved or not.
    ///
    /// Pages of a hundred, followed through `paging.next` until Meta stops offering one and never
    /// past fifty pages — a guard against a cursor that does not end, not a limit anyone should meet.
    /// </summary>
    public async Task<MetaResult<IReadOnlyList<WhatsAppTemplateInfo>>> GetTemplatesAsync(
        CancellationToken ct = default)
    {
        var templates = new List<WhatsAppTemplateInfo>();

        string? path = $"{whatsApp.WabaId}/message_templates"
                       + "?fields=name,language,status,category,components&limit=100";

        for (var pages = 0; path is not null && pages < MetaJson.MaxPages; pages++)
        {
            var (body, error) = await SendAsync(HttpMethod.Get, path, null, ct);

            if (error is not null) return Fail<IReadOnlyList<WhatsAppTemplateInfo>>(error);

            templates.AddRange(WhatsAppTemplateReader.Read(body!));

            path = NextPage(body!);
        }

        return MetaResult<IReadOnlyList<WhatsAppTemplateInfo>>.Ok(templates);
    }

    /// <summary>
    /// The one place a message is actually posted, so the token, the timeout, the error reading
    /// and the id extraction are written once and cannot drift apart between the kinds of send.
    /// </summary>
    private async Task<MetaResult<string>> SendAsync(object payload, CancellationToken ct)
    {
        var (body, error) = await SendAsync(
            HttpMethod.Post, $"{whatsApp.PhoneNumberId}/messages", payload, ct);

        if (error is not null) return Fail<string>(error);

        return MessageId(body!) is { } id
            ? MetaResult<string>.Ok(id)

            // Accepted without a readable id is a message that cannot be recorded — and one that
            // may well have been sent, so it is an unknown outcome and never a retry.
            : MetaResult<string>.Unknown("WhatsApp accepted the message without a readable id.");
    }

    private async Task<(string? Body, MetaResult<object>? Error)> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        try
        {
            // Per call rather than on the HttpClient: its Timeout is fixed at construction, and a
            // host reading this one from settings must be able to change it without a restart.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

            timeout.CancelAfter(_timeout);

            using var request = new HttpRequestMessage(method, $"{app.GraphRoot}/{path}");

            if (payload is not null) request.Content = JsonContent.Create(payload);

            // Bearer, not a query parameter. The Cloud API accepts both, and query strings end up
            // in proxy logs — a token in a log is a token that has to be rotated.
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", whatsApp.AccessToken);

            var response = await httpClient.SendAsync(request, timeout.Token);

            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.IsSuccessStatusCode) return (body, null);

            // Meta's own error when there is one; the status alone when a gateway answered instead,
            // because inventing a code would put words in Meta's mouth.
            return (null, MetaError.TryRead(body) is { } error
                ? MetaResult<object>.Fail(error)
                : MetaResult<object>.Transport($"Meta answered {(int)response.StatusCode}."));
        }
        catch (HttpRequestException exception)
            when (method == HttpMethod.Post && !MetaJson.NeverConnected(exception))
        {
            // The send may have left; whether Meta acted on it is not known.
            return (null, MetaResult<object>.Unknown(exception.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own deadline; a caller's cancellation propagates. For a send the request left,
            // so the outcome is unknown rather than a failure to retry.
            var message = $"Meta did not answer within {_timeout.TotalSeconds:0} s.";

            return (null, method == HttpMethod.Post
                ? MetaResult<object>.Unknown(message + " The message may have been sent.")
                : MetaResult<object>.Transport(message));
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

    private string? NextPage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return MetaJson.NextPage(document.RootElement, app.GraphRoot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The id, which the Cloud API buries one array deep: `messages[0].id`. Nothing like the Page
    /// API's flat `message_id`, and reading the wrong one gives null on a successful send.
    /// </summary>
    private static string? MessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (MetaJson.Property(document.RootElement, "messages") is not
                { ValueKind: JsonValueKind.Array } messages)
            {
                return null;
            }

            foreach (var message in messages.EnumerateArray())
            {
                if (MetaJson.Property(message, "id") is { ValueKind: JsonValueKind.String } id
                    && id.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // A 200 that is not JSON. Treated as no id, which the caller reports as a failure.
        }

        return null;
    }
}
