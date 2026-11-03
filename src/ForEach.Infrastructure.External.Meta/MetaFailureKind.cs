namespace ForEach.Infrastructure.External.Meta;

/// <summary>
/// What a caller should DO about a failure, which is the only distinction worth making in code.
///
/// Meta reports dozens of codes and the host does not want to know them; it wants to know whether
/// to try again, to tell somebody to reconnect an account, to show a refusal to the person who
/// pressed send, or to fail loudly because the call itself was wrong.
/// </summary>
public enum MetaFailureKind
{
    /// <summary>A token, permission or app setting. It repeats until a person changes something.</summary>
    Configuration,

    /// <summary>Meta-side, or rate limiting. The next attempt will probably work.</summary>
    Transient,

    /// <summary>The request was wrong — wrong parameter count, wrong id. A bug here, not there.</summary>
    CallerMistake,

    /// <summary>
    /// Allowed by the API and refused by policy: outside the 24-hour window, a recipient who
    /// cannot be reached, a re-engagement that needs a template. Never worth retrying, and the
    /// message is meant for a person rather than a log.
    /// </summary>
    PolicyRefused
}
