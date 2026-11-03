namespace ForEach.Infrastructure.External.Meta.Credentials;

/// <summary>
/// One WhatsApp Business number, and the account it belongs to.
///
/// Distinct from the Page credentials even though both live under one Meta business: the Cloud API
/// is a different product with its own ids and its own token, and a company can have a Page
/// without WhatsApp or WhatsApp without a Page. Conflating them is how a host ends up sending a
/// message with a token that has no rights to the number.
/// </summary>
public sealed class WhatsAppCredentials
{
    /// <summary>The number's own id — what messages are sent from, not the number itself.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>
    /// The WhatsApp Business Account the number sits in. Needed only for account-level reads —
    /// the template list lives here rather than on the number.
    /// </summary>
    public string WabaId { get; set; } = "";

    /// <summary>
    /// A system-user token with the WhatsApp permissions. Sent as a bearer header and never in a
    /// URL, so it does not end up in a proxy log.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Enough to send. Reading templates additionally wants <see cref="WabaId"/>.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>Whether the template list can be fetched: a send does not need the account id, that read does.</summary>
    public bool CanReadTemplates => IsConfigured && !string.IsNullOrWhiteSpace(WabaId);
}
