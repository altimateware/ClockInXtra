namespace Attendance.Application.Abstractions;

/// <summary>
/// Records security-relevant events (Claude.md §32, §33).
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>core.usp_SecurityEvent_Create</c>, which writes to an append-only
/// ledger table. Rows cannot be altered or removed afterwards, including by a
/// database administrator — which is the property that makes the trail worth
/// having (threat TH-40).
/// </para>
/// <para>
/// <b>This is where the truth lives.</b> The mobile API deliberately collapses
/// wrong-user, wrong-password and wrong-code into one INVALID_CREDENTIALS
/// response, so that an internet-facing endpoint cannot confirm a correct
/// password (CON-09). An investigator still needs to know which it was, and this
/// is the only place that distinction is kept.
/// </para>
/// <para>
/// <b>What must never be passed to it:</b> passwords, authenticator codes, TOTP
/// secrets, signatures, private keys, or exact coordinates. The reason code and
/// the subject are enough to investigate with; the rest would turn the audit
/// trail into a second copy of the credentials it exists to protect.
/// </para>
/// </remarks>
public interface ISecurityEventRecorder
{
    /// <summary>Records one security event.</summary>
    Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken);
}

/// <summary>How serious an event is.</summary>
public enum SecurityEventSeverity
{
    /// <summary>Normal activity worth keeping in the timeline, such as a successful sign-in.</summary>
    Information = 1,

    /// <summary>A refused attempt: wrong credentials, a rejected location, a stale signature.</summary>
    Warning = 2,

    /// <summary>Something that should be looked at: lockouts, replays, rejected attestation.</summary>
    Critical = 3,
}

/// <summary>Who or what an event concerns.</summary>
public enum SecurityEventSubject
{
    /// <summary>Not attributable to a known account.</summary>
    Unknown = 0,

    /// <summary>An employee.</summary>
    MobileUser = 1,

    /// <summary>An administrator.</summary>
    Administrator = 2,

    /// <summary>A registered device.</summary>
    Device = 3,
}

/// <summary>
/// A security event to record.
/// </summary>
/// <param name="EventType">
/// Stable category, for example <c>Auth.Failed</c> or <c>Signature.Replay</c>.
/// </param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Subject">What kind of account or device it concerns.</param>
/// <param name="SubjectKey">
/// The account identifier or device public id. Never a credential.
/// </param>
/// <param name="ReasonCode">
/// The precise internal reason, including the ones the API does not return.
/// </param>
/// <param name="SourceApplication">Which application observed it.</param>
/// <param name="DevicePublicId">The device involved, where there is one.</param>
/// <param name="SourceAddressHash">
/// A salted hash of the client address, never the address itself: correlating
/// abuse does not require identifying a person (§63).
/// </param>
/// <param name="CorrelationId">Ties the event to the request that caused it.</param>
/// <param name="Details">
/// Small JSON object with context. Reviewed at every call site for secrets.
/// </param>
public readonly record struct SecurityEvent(
    string EventType,
    SecurityEventSeverity Severity,
    SecurityEventSubject Subject,
    string? SubjectKey,
    string ReasonCode,
    string SourceApplication,
    Guid? DevicePublicId = null,
    byte[]? SourceAddressHash = null,
    Guid? CorrelationId = null,
    string? Details = null);
