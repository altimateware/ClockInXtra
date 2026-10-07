using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Enforces one-time use of authenticator codes.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>core.usp_MfaCredential_TryConsumeTimeStep</c>. Verifying a TOTP
/// code and <em>consuming</em> it are separate steps, and both are required
/// before the second factor counts as satisfied (§13).
/// </para>
/// <para>
/// <b>Why consumption cannot live in the application.</b> A code stays valid for
/// its whole 30-second step, and with ±1 step tolerance a single code works for
/// about 90 seconds — long enough for someone who read it over a shoulder, or
/// who captured it on a compromised device. Remembering "this step was used"
/// in process memory would not stop the same code being presented to a second
/// IIS node a moment later. The database does it with one conditional UPDATE,
/// so the check and the write cannot be separated by a race.
/// </para>
/// </remarks>
public interface IMfaCredentialRepository
{
    /// <summary>
    /// Marks a time step as used, if it has not been used already.
    /// </summary>
    /// <param name="mobileUserId">The employee.</param>
    /// <param name="timeStep">
    /// The step that matched during verification, as reported by
    /// <see cref="ITotpVerifier"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="AttendanceResultCode.Success"/> when the step was consumed;
    /// <see cref="AttendanceResultCode.OtpReplayed"/> when it had already been
    /// used; <see cref="AttendanceResultCode.MfaNotEnrolled"/> when the employee
    /// has no active authenticator.
    /// </returns>
    /// <remarks>
    /// The two failure codes are distinguished for the security event trail, not
    /// for the response: the API reports both as INVALID_CREDENTIALS so that an
    /// internet-facing endpoint cannot confirm a correct password (CON-09).
    /// </remarks>
    Task<AttendanceResultCode> TryConsumeTimeStepAsync(
        int mobileUserId,
        long timeStep,
        CancellationToken cancellationToken);
}
