namespace Attendance.Application.Abstractions;

/// <summary>
/// Makes clock-in and clock-out safe to retry (Claude.md §53).
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>mobile.usp_Idempotency_TryBegin</c> and
/// <c>usp_Idempotency_Complete</c>. The problem it solves is concrete: the
/// employee taps Clock In, the network drops while the server is committing, and
/// the app retries. Without this, the retry either creates a second record or —
/// because the unique index prevents that — tells the employee they are already
/// clocked in, which reads as a failure when it was a success.
/// </para>
/// <para>
/// The claim is made in the database, not in memory, because the retry may well
/// arrive at a different application server than the original (§45).
/// </para>
/// <para>
/// The request hash is what distinguishes an honest retry from a key being
/// reused for a different operation. Answering a different request with a stored
/// result would be worse than refusing it.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims an idempotency key before performing the operation.
    /// </summary>
    Task<IdempotencyClaim> TryBeginAsync(
        int deviceId,
        Guid idempotencyKey,
        IdempotentEndpoint endpoint,
        ReadOnlyMemory<byte> requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the outcome so a later retry receives the same answer.
    /// </summary>
    /// <remarks>
    /// Called for business failures too. If the first attempt was refused with
    /// ALREADY_CLOCKED_IN, the retry must be told the same thing rather than
    /// being processed afresh.
    /// </remarks>
    Task CompleteAsync(
        int deviceId,
        Guid idempotencyKey,
        int resultCode,
        string? responsePayload,
        CancellationToken cancellationToken);
}

/// <summary>Operations that accept an idempotency key.</summary>
public enum IdempotentEndpoint
{
    /// <summary>Clock-in.</summary>
    ClockIn = 1,

    /// <summary>Clock-out.</summary>
    ClockOut = 2,
}

/// <summary>What the caller should do with an idempotency key.</summary>
public enum IdempotencyDisposition
{
    /// <summary>First attempt: perform the operation.</summary>
    Proceed = 0,

    /// <summary>
    /// The original attempt finished; return its stored outcome verbatim
    /// instead of doing the work again.
    /// </summary>
    ReplayStoredResult = 1,

    /// <summary>
    /// The original attempt is still running. The client should retry shortly or
    /// reconcile with <c>user/status</c>; it must not start a second attendance
    /// transaction.
    /// </summary>
    InProgress = 2,

    /// <summary>
    /// The key was already used for a materially different request. Refused
    /// rather than answered with an unrelated stored result.
    /// </summary>
    KeyReused = 3,
}

/// <summary>
/// The result of claiming an idempotency key.
/// </summary>
/// <param name="Disposition">What the caller should do.</param>
/// <param name="StoredResultCode">
/// The original attempt's result code, when replaying.
/// </param>
/// <param name="StoredResponsePayload">
/// The original response, when replaying. Attendance results only — no
/// credential ever passes through here.
/// </param>
public readonly record struct IdempotencyClaim(
    IdempotencyDisposition Disposition,
    int? StoredResultCode,
    string? StoredResponsePayload);
