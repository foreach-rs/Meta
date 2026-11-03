namespace ForEach.Infrastructure.External.Meta.Credentials;

/// <summary>
/// One Facebook Page, and the token that acts on its behalf.
///
/// A page token belongs to the business rather than to us: it is issued when somebody connects
/// their Page, it can be revoked from their side at any moment, and its loss is a configuration
/// failure rather than an outage. The Instagram account is here rather than in its own class
/// because Meta models it as a property of the Page — messages to an Instagram profile are sent
/// with the Page's token.
/// </summary>
public sealed class MetaPageCredentials
{
    /// <summary>
    /// The Page's numeric id: the sender of every message, and what a webhook entry names so a
    /// relay can tell whose batch it is.
    /// </summary>
    public string PageId { get; set; } = "";

    /// <summary>
    /// A long-lived Page token, issued when the Page was connected. Travels as a query parameter on
    /// every Page call, because that is the only place the Page API reads it from.
    /// </summary>
    public string PageAccessToken { get; set; } = "";

    /// <summary>The Instagram professional account linked to this Page, when there is one.</summary>
    public string? InstagramAccountId { get; set; }

    /// <summary>
    /// Enough to make a Page call. Says nothing about whether the token still works — only Meta
    /// knows, and it says so as code 190.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PageId) && !string.IsNullOrWhiteSpace(PageAccessToken);

    /// <summary>Whether Instagram messages can be sent at all — the Page alone is not enough.</summary>
    public bool IsInstagramConfigured =>
        IsConfigured && !string.IsNullOrWhiteSpace(InstagramAccountId);
}
