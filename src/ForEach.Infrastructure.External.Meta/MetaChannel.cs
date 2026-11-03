namespace ForEach.Infrastructure.External.Meta;

/// <summary>
/// Which of Meta's messaging surfaces a message came from, or is going to.
///
/// The library's own enum rather than the host's: a solution consuming this has its own channel
/// list, usually longer (e-mail, Viber), and mapping three values is cheaper than either project
/// having to know the other's.
/// </summary>
public enum MetaChannel
{
    /// <summary>Facebook Messenger, on behalf of a Page.</summary>
    Messenger,

    /// <summary>Instagram direct messages, to the professional account linked to the Page.</summary>
    Instagram,

    /// <summary>WhatsApp Business, over the Cloud API.</summary>
    WhatsApp
}
