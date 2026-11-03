using System.Security.Cryptography;
using System.Text;

namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>
/// Proving that a webhook call came from Meta and not from somebody who found the URL.
///
/// The endpoint has to be anonymous — Meta cannot log in — so this signature is the *entire*
/// access control on it. Everything downstream treats what arrives as a customer's words, so a
/// forged payload is somebody writing into a customer's conversation history; and because the
/// route accepts a batch, one accepted forgery is as many messages as they care to send.
/// </summary>
public static class MetaWebhookSignature
{
    /// <summary>
    /// Whether the header matches an HMAC of exactly these bytes under the app secret.
    /// </summary>
    /// <param name="header">
    /// The X-Hub-Signature-256 value, which Meta sends as "sha256=" followed by lowercase hex.
    /// </param>
    /// <param name="rawBody">
    /// The body exactly as received. Re-serialising a parsed object changes whitespace and key
    /// order, and the signature is over the bytes — so a verifier that reads the model instead of
    /// the stream rejects every genuine call, which is the failure that gets a signature check
    /// deleted rather than fixed.
    /// </param>
    /// <param name="appSecret">
    /// The app secret, <see cref="Credentials.MetaAppCredentials.AppSecret"/>. Empty is never
    /// valid: an empty key would make every forged signature as good as a real one.
    /// </param>
    public static bool IsValid(string? header, byte[] rawBody, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(header) || appSecret.Length == 0) return false;

        const string prefix = "sha256=";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var provided = header[prefix.Length..].Trim();

        // Hex, and of the right length, before anything is computed. FromHexString throws on
        // anything else, and an exception here would be a 500 for a request that is simply
        // rubbish — which is a way of telling an attacker their guess was interesting.
        if (provided.Length != 64 || !IsHex(provided)) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), rawBody);

        // Constant time. A comparison that returns early leaks, one byte at a time, how much of a
        // guess was right — and a signature is exactly the value worth guessing at.
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(provided));
    }

    /// <summary>
    /// Whether the verify token Meta echoed back is ours.
    ///
    /// Constant time for the same reason, even though this one only guards the subscription
    /// handshake: a token that can be guessed lets somebody point our webhook at their own app.
    /// </summary>
    public static bool IsVerifyTokenValid(string? provided, string expected) =>
        !string.IsNullOrEmpty(provided)
        && expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            var hex = character is >= '0' and <= '9'
                      || character is >= 'a' and <= 'f'
                      || character is >= 'A' and <= 'F';

            if (!hex) return false;
        }

        return true;
    }
}
