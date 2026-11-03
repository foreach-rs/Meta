using System.Net.Http.Json;
using System.Text.Json;
using ForEach.Infrastructure.External.Meta.Credentials;

namespace ForEach.Infrastructure.External.Meta.Messenger;

/// <summary>
/// The Page's side of Messenger and Instagram over the Graph API.
///
/// Two calls and nothing else. How a message is threaded, whether it may be sent at all, and what
/// a failure means for the person who pressed the button all live above this — what is here is the
/// URL, the token and the reading of Meta's error envelope.
///
/// Instagram needs no second client: the same Page token addresses both, and which surface a
/// message lands on is decided by the recipient id rather than by the endpoint.
/// </summary>
public sealed class MessengerClient(
    HttpClient httpClient,
    MetaAppCredentials app,
    MetaPageCredentials page,
    TimeSpan? timeout = null)
{
    /// <summary>Ten seconds by default. A reply is typed by somebody waiting at a screen.</summary>
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    /// <summary>
    /// One text reply inside the window the customer opened, answered with Meta's message id.
    /// Whether it lands on Messenger or Instagram follows from the recipient id.
    /// </summary>
    public async Task<MetaResult<string>> SendTextAsync(
        string recipientId, string text, CancellationToken ct = default)
    {
        try
        {
            // Per call rather than on the HttpClient, whose Timeout is fixed at construction: a
            // host reading this from settings must be able to change it without a restart.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(_timeout);

            var response = await httpClient.PostAsJsonAsync(
                $"{app.GraphRoot}/{page.PageId}/messages"
                + $"?access_token={Uri.EscapeDataString(page.PageAccessToken)}",
                new
                {
                    recipient = new { id = recipientId },

                    // RESPONSE, because that is what this is: an answer inside the window the
                    // customer opened. Claiming another type would be claiming a permission the
                    // Page does not have, and Meta polices exactly that.
                    messaging_type = "RESPONSE",
                    message = new { text }
                },
                deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                return MetaError.TryRead(body) is { } error
                    ? MetaResult<string>.Fail(error)
                    : MetaResult<string>.Transport($"Meta answered {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);

            // No message id in a 200 would mean Meta accepted something the caller cannot record.
            // Reported as an unknown outcome rather than invented, and never as retryable: the
            // message may well have gone out, and a row under a made-up id is a row the
            // customer's reply will never thread to.
            return MetaJson.Property(document.RootElement, "message_id") is
                   { ValueKind: JsonValueKind.String } id
                   && id.GetString() is { Length: > 0 } messageId
                ? MetaResult<string>.Ok(messageId)
                : MetaResult<string>.Unknown("Meta accepted the message without a readable id.");
        }
        catch (JsonException)
        {
            return MetaResult<string>.Unknown(
                "Meta accepted the message but the answer could not be read.");
        }
        catch (HttpRequestException exception) when (MetaJson.NeverConnected(exception))
        {
            // Name resolution, refused connection, TLS: nothing reached Meta, so a repeat is safe.
            return MetaResult<string>.Transport(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            // The request may have left; whether Meta acted on it is not known.
            return MetaResult<string>.Unknown(exception.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own deadline rather than a caller cancelling, said plainly: "the request was
            // cancelled" reads like somebody clicked away. The request left, so the outcome is
            // unknown rather than a failure to retry. A caller's own cancellation propagates.
            return MetaResult<string>.Unknown(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s; the message may have been sent.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MetaResult<string>.Unknown(exception.Message);
        }
    }

    /// <summary>
    /// The person's name, if Meta will tell us. Null when it will not, for any reason.
    ///
    /// Only the name is asked for. Meta will hand over more about somebody who has messaged the
    /// Page, and this deliberately does not ask: what an inbox needs is a label on a thread, and
    /// anything further is personal data with no reason to be held.
    ///
    /// Null rather than a result type, because every failure here has the same answer. A name is
    /// decoration on a message; refusing the message because it could not be labelled would be
    /// losing the thing that matters to protect the thing that does not.
    /// </summary>
    public async Task<string?> GetDisplayNameAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(_timeout);

            var response = await httpClient.GetAsync(
                $"{app.GraphRoot}/{Uri.EscapeDataString(userId)}?fields=name,first_name,last_name"
                + $"&access_token={Uri.EscapeDataString(page.PageAccessToken)}",
                deadline.Token);

            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(deadline.Token));

            if (Name(document.RootElement, "name") is { Length: > 0 } full) return full;

            var first = Name(document.RootElement, "first_name");

            var last = Name(document.RootElement, "last_name");

            var joined = $"{first} {last}".Trim();

            return joined.Length == 0 ? null : joined;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own deadline. A caller's cancellation propagates.
            return null;
        }
    }

    private static string? Name(JsonElement root, string property) =>
        MetaJson.Property(root, property) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
}
