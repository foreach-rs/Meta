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
            // Reported as a failure rather than invented: a row under a made-up id is a row the
            // customer's reply will never thread to.
            return document.RootElement.TryGetProperty("message_id", out var id)
                   && id.GetString() is { Length: > 0 } messageId
                ? MetaResult<string>.Ok(messageId)
                : MetaResult<string>.Transport("Meta accepted the message without an id.");
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return MetaResult<string>.Transport(exception.Message);
        }
        catch (OperationCanceledException)
        {
            // Our own deadline rather than a caller cancelling, said plainly: "the request was
            // cancelled" reads like somebody clicked away.
            return MetaResult<string>.Transport(
                $"Meta did not answer within {_timeout.TotalSeconds:0} s.");
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
                $"{app.GraphRoot}/{userId}?fields=name,first_name,last_name"
                + $"&access_token={Uri.EscapeDataString(page.PageAccessToken)}",
                deadline.Token);

            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(deadline.Token));

            if (document.RootElement.TryGetProperty("name", out var name)
                && name.GetString() is { Length: > 0 } full)
            {
                return full;
            }

            var first = document.RootElement.TryGetProperty("first_name", out var firstName)
                ? firstName.GetString()
                : null;

            var last = document.RootElement.TryGetProperty("last_name", out var lastName)
                ? lastName.GetString()
                : null;

            var joined = $"{first} {last}".Trim();

            return joined.Length == 0 ? null : joined;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
