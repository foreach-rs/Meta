namespace ForEach.Infrastructure.External.Meta.Credentials;

/// <summary>
/// The app itself: who we are to Meta, and which Graph version we speak.
///
/// Separate from the page and WhatsApp credentials because it has a different lifetime and a
/// different owner. A page token is reconnected by whoever runs the business; the app secret is
/// ours and changes when we rotate it, which is roughly never.
/// </summary>
public sealed class MetaAppCredentials
{
    /// <summary>
    /// The app's numeric id as the developer console shows it. Public, and sent in the clear —
    /// it is the secret below that is not.
    /// </summary>
    public string AppId { get; set; } = "";

    /// <summary>
    /// Used for two things and no others: signing `appsecret_proof` on calls, and verifying the
    /// HMAC on incoming webhooks. A host binding this from configuration should treat it as the
    /// secret it is.
    /// </summary>
    public string AppSecret { get; set; } = "";

    /// <summary>
    /// The string Meta echoes back when it verifies a webhook subscription. Ours to choose, and
    /// the only thing standing between a stranger and a subscription pointed at our endpoint.
    /// </summary>
    public string VerifyToken { get; set; } = "";

    /// <summary>
    /// Pinned, not "latest". Meta breaks fields between versions and a client that silently
    /// followed the newest one would change behaviour on their schedule rather than ours.
    /// </summary>
    public string GraphApiVersion { get; set; } = "v21.0";

    /// <summary>
    /// Where the Graph API lives. Overridable so a test can point a client at a stub, and for no
    /// other reason.
    /// </summary>
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>
    /// Enough to make a signed call. Nothing here checks whether the values are CORRECT — only
    /// Meta can say that, and it says it as code 190. What this answers is whether a call is worth
    /// making at all, so a host can run dark until somebody connects an account.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary>The versioned root every request hangs off, without a trailing slash.</summary>
    public Uri GraphRoot => new($"{GraphBaseUrl.TrimEnd('/')}/{GraphApiVersion}");
}
