using Attendance.Domain.Services;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Supplies the configurable security and location policy the request path
/// needs.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>core.ApplicationSetting</c>. The <em>attendance</em> business rules —
/// the timezone, the clock-in closing time, the window actions — are deliberately
/// NOT here: those are read inside the stored procedure's own transaction
/// (decision DB-05), so that the rule applied and the record written cannot
/// disagree. What this provides is the policy the application layer applies
/// before it ever reaches the database.
/// </para>
/// <para>
/// Values are cached briefly. A setting change takes effect within the cache
/// lifetime rather than instantly, which is acceptable for thresholds and
/// policies but would not be for device revocation — which is why that is never
/// cached (§18).
/// </para>
/// </remarks>
public interface IAttendancePolicyProvider
{
    /// <summary>Returns the current policy.</summary>
    Task<AttendancePolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Configurable policy applied by the application layer.
/// </summary>
/// <param name="AccuracyPolicy">
/// How reported accuracy affects the location decision (OPEN-25).
/// </param>
/// <param name="MaxAcceptedAccuracyMeters">
/// Worst accuracy still accepted, or <see langword="null"/> to fall back to the
/// matched office's own radius — the strict interim rule.
/// </param>
/// <param name="RejectUntrustedLocationSource">
/// Whether to refuse positions the platform flagged as mocked or simulated
/// (OPEN-26).
/// </param>
/// <param name="MobileLockoutThreshold">Failures before an employee account locks.</param>
/// <param name="MobileLockoutMinutes">How long that lock lasts.</param>
/// <param name="SignatureSkewSeconds">
/// Accepted difference between a signature's <c>created</c> time and server
/// time. This is also the window in which a captured request could be replayed,
/// which is why the nonce store must outlive it (§37).
/// </param>
/// <param name="CollapseCredentialErrorCodes">
/// Whether wrong user, wrong password and wrong code all report as
/// INVALID_CREDENTIALS (CON-09, OPEN-38).
/// </param>
/// <param name="RequireHardwareAttestationAndroid">
/// Whether Android registration must present a verified hardware-backed key.
/// </param>
/// <param name="RequireHardwareAttestationIos">
/// Whether iOS registration must present Apple App Attest (DEC-05).
/// </param>
/// <param name="DeviceRegistrationRequiresApproval">
/// Whether a first device registration waits for an administrator (OPEN-32).
/// Turning this off does <b>not</b> allow a registration to displace an existing
/// active device — that refusal is enforced in the database regardless, because
/// otherwise a phished password and one code would be enough to move an
/// employee's attendance to another handset with nobody in the loop.
/// </param>
/// <param name="ChallengeLifetimeSeconds">
/// How long a registration challenge stays usable. It bounds the window in which
/// a captured attestation could be presented, so it is a security parameter.
/// </param>
/// <param name="MinimumAppVersion">
/// Lowest accepted application version, or <see langword="null"/> before the
/// first release sets one.
/// </param>
public readonly record struct AttendancePolicy(
    LocationAccuracyPolicy AccuracyPolicy,
    double? MaxAcceptedAccuracyMeters,
    bool RejectUntrustedLocationSource,
    int MobileLockoutThreshold,
    int MobileLockoutMinutes,
    int SignatureSkewSeconds,
    bool CollapseCredentialErrorCodes,
    bool RequireHardwareAttestationAndroid,
    bool RequireHardwareAttestationIos,
    bool DeviceRegistrationRequiresApproval,
    int ChallengeLifetimeSeconds,
    string? MinimumAppVersion,
    int TotpStepTolerance);
