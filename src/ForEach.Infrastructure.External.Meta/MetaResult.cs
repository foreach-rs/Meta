namespace ForEach.Infrastructure.External.Meta;

/// <summary>
/// The outcome of one Meta call: a value, or a reason there is none.
///
/// Its own type rather than FluentResults, which the host solution happens to use. The library
/// carries no third-party dependency at all, so a solution on a different result type — or on
/// exceptions — adapts it in a dozen lines instead of inheriting a package choice made elsewhere.
///
/// A failure is either a <see cref="MetaError"/> Meta itself reported or a transport problem that
/// never reached it, and the two are kept apart: a caller deciding whether to retry needs to know
/// that a timeout is not an expired token.
/// </summary>
public sealed class MetaResult<T>
{
    private MetaResult(T? value, MetaError? error, string? transportFailure, bool outcomeUnknown = false)
    {
        IsOutcomeUnknown = outcomeUnknown;
        Value = value;
        Error = error;
        TransportFailure = transportFailure;
    }

    /// <summary>The value when the call succeeded; default otherwise. <see cref="IsSuccess"/> says which.</summary>
    public T? Value { get; }

    /// <summary>Set when Meta answered with an error envelope. Null otherwise.</summary>
    public MetaError? Error { get; }

    /// <summary>Set when the call never got an answer — timeout, DNS, TLS, a gateway's HTML.</summary>
    public string? TransportFailure { get; }

    /// <summary>Meta answered, and did not answer with an error.</summary>
    public bool IsSuccess => Error is null && TransportFailure is null;

    /// <summary>
    /// True when a send may have been acted on although no usable answer came back. Also true of
    /// <see cref="TransportFailure"/>, which then carries the reason, but never retryable.
    /// </summary>
    public bool IsOutcomeUnknown { get; }

    /// <summary>Succeeded, Failed, or Unknown — the last for a send that may already have gone out.</summary>
    public MetaOutcome Outcome =>
        IsOutcomeUnknown ? MetaOutcome.Unknown : IsSuccess ? MetaOutcome.Succeeded : MetaOutcome.Failed;

    /// <summary>
    /// One sentence naming what went wrong, in Meta's own words where there are any.
    ///
    /// The host writes this into a job-run record or shows it to whoever pressed send, so it is
    /// worth being the server's message rather than a paraphrase: "more than 24 hours have passed
    /// since the last message" tells somebody what to do, and "sending failed" does not.
    /// </summary>
    public string FailureMessage => Error?.ToString() ?? TransportFailure ?? "";

    /// <summary>
    /// Retrying is worth it only for a transport problem or a Meta-side one — and never for an
    /// unknown outcome, because repeating a send that may have gone out sends it twice.
    /// </summary>
    public bool IsWorthRetrying =>
        !IsOutcomeUnknown && (TransportFailure is not null || Error?.Kind == MetaFailureKind.Transient);

    /// <summary>A call that answered.</summary>
    public static MetaResult<T> Ok(T value) => new(value, null, null);

    /// <summary>A call Meta refused, in its own words.</summary>
    public static MetaResult<T> Fail(MetaError error) => new(default, error, null);

    /// <summary>A call that never got an answer. The message is what the host logs.</summary>
    public static MetaResult<T> Transport(string message) => new(default, null, message);

    /// <summary>
    /// A send whose fate is not known. <see cref="IsSuccess"/> is false and
    /// <see cref="IsWorthRetrying"/> is false; the message says why.
    /// </summary>
    public static MetaResult<T> Unknown(string message) => new(default, null, message, true);
}
