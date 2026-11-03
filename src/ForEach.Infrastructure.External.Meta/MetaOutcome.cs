namespace ForEach.Infrastructure.External.Meta;

/// <summary>
/// What a call can be known to have done.
///
/// Three states rather than two because a send has a third: the request left, and nothing came back
/// that says whether Meta acted on it. Treating that as a plain failure resends a message the
/// customer may already have received.
/// </summary>
public enum MetaOutcome
{
    /// <summary>Meta answered and did not report an error.</summary>
    Succeeded,

    /// <summary>The call failed in a way that is known: Meta refused it, or it never left.</summary>
    Failed,

    /// <summary>
    /// A send that may or may not have been acted on — the timeout elapsed after the request left,
    /// the connection broke mid-flight, or a success reply could not be read. Not worth retrying
    /// blindly: the host checks (or accepts the risk) before it sends again.
    /// </summary>
    Unknown
}
