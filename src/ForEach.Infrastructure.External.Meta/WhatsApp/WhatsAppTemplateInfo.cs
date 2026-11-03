namespace ForEach.Infrastructure.External.Meta.WhatsApp;

/// <summary>
/// One approved (or not yet approved) message template, as Meta describes it.
/// </summary>
/// <param name="Name">Meta's name for it — what a send names, together with the language.</param>
/// <param name="Language">
/// The code the template was approved under — `sr`, `en_US`. A send has to match it exactly, or
/// Meta reports that the template does not exist.
/// </param>
/// <param name="Status">Whether it may be sent at all.</param>
/// <param name="Category">
/// MARKETING, UTILITY or AUTHENTICATION in Meta's words; null when it says nothing. It decides what
/// a message costs and when it may be sent.
/// </param>
/// <param name="BodyText">
/// The BODY component's wording, placeholders included, for a screen that shows what will go out.
/// Null when the template has no body.
/// </param>
/// <param name="ParameterCount">
/// How many distinct {{n}} placeholders the body has. Counted from the wording because Meta does
/// not report it, and a send whose parameter count disagrees is refused outright — so this is what
/// lets a host say so before spending the call.
/// </param>
public sealed record WhatsAppTemplateInfo(
    string Name,
    string Language,
    WhatsAppTemplateStatus Status,
    string? Category,
    string? BodyText,
    int ParameterCount);

/// <summary>
/// Where a template stands with Meta.
///
/// Their vocabulary, and only the values that change what a caller does: approved may be sent,
/// pending may not yet, rejected may not at all and somebody has to be told. Anything else they
/// invent — PAUSED, DISABLED, FLAGGED — lands as Unknown, which a host should treat as "not
/// approved" rather than guess either way.
/// </summary>
public enum WhatsAppTemplateStatus
{
    /// <summary>Anything Meta says that is not one of the three below. Not approved, as far as a send is concerned.</summary>
    Unknown,

    /// <summary>Submitted and under review. Not sendable yet.</summary>
    Pending,

    /// <summary>May be sent.</summary>
    Approved,

    /// <summary>Refused by review and will not be sent; somebody has to edit and resubmit it.</summary>
    Rejected
}
