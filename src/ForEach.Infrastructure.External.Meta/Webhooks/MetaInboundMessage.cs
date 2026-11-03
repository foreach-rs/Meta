namespace ForEach.Infrastructure.External.Meta.Webhooks;

/// <summary>
/// One message read out of a webhook batch, as Meta sent it.
/// </summary>
/// <param name="Channel">Which surface it arrived on, as the envelope was classified.</param>
/// <param name="ThreadKey">
/// What identifies the conversation on its own channel: the other person's id or number. One
/// thread per person per channel, which is all Meta gives us and all a chat means.
/// </param>
/// <param name="ExternalMessageId">
/// Meta's id for the message — `mid` on the Page, `id` on WhatsApp. What a host de-duplicates on,
/// because Meta re-delivers any batch it did not get a prompt 200 for.
/// </param>
/// <param name="IsEcho">
/// True when this is our own outbound message coming back — a colleague replied in Meta's own
/// inbox. The host decides what that means for its data; here it is only the fact.
/// </param>
/// <param name="From">
/// The counterpart's id or number. On an echo this is still the customer rather than the Page,
/// because the thread is the person in both directions.
/// </param>
/// <param name="FromDisplayName">
/// The profile name when the payload carried one. WhatsApp sends it beside the message; Messenger
/// does not, and fetching it is a Graph call the host makes if it wants to.
/// </param>
/// <param name="Body">
/// The text, or a placeholder naming what arrived instead. Unclipped: how long a message may be is
/// a property of somebody's database, not of Meta's payload, so the host cuts it to its own widths.
/// </param>
/// <param name="SentAt">
/// When Meta says it was sent, in UTC. Now when the payload carries no usable timestamp, so a
/// message never sorts to the epoch.
/// </param>
/// <param name="ReplyPayload">
/// The developer payload of a WhatsApp quick-reply or interactive button the customer tapped, when
/// there was one. Body carries the words they saw; this carries what the template author attached.
/// </param>
public sealed record MetaInboundMessage(
    MetaChannel Channel,
    string ThreadKey,
    string ExternalMessageId,
    bool IsEcho,
    string From,
    string? FromDisplayName,
    string Body,
    DateTime SentAt,
    string? ReplyPayload = null);

/// <summary>
/// What became of one message we sent, as WhatsApp reports it.
/// </summary>
/// <param name="ExternalMessageId">The id the send returned, which is how a receipt finds its message.</param>
/// <param name="Status">How far it got.</param>
/// <param name="Error">
/// Meta's reason, on a failure and never otherwise. Worth carrying because it is what somebody
/// reads when they ask why the customer never answered — and "re-engagement message" is a
/// different afternoon from "message undeliverable".
/// </param>
/// <param name="At">When that stage was reached, in UTC, from Meta's own timestamp.</param>
public sealed record WhatsAppDeliveryReceipt(
    string ExternalMessageId,
    WhatsAppDeliveryStatus Status,
    string? Error,
    DateTime At);

/// <summary>
/// How far a sent message got. Ordered, because the Cloud API reports them as a message moves
/// through them and a host must not let a late "sent" overwrite a "read".
/// </summary>
public enum WhatsAppDeliveryStatus
{
    /// <summary>Accepted by WhatsApp and on its way.</summary>
    Sent,

    /// <summary>On the recipient's phone.</summary>
    Delivered,

    /// <summary>Opened by the recipient — reported only when their read receipts are on.</summary>
    Read,

    /// <summary>Not delivered and will not be. <see cref="WhatsAppDeliveryReceipt.Error"/> says why.</summary>
    Failed
}
