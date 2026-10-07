namespace Attendance.Application.Abstractions;

/// <summary>
/// Counts failed authentication attempts and enforces lockout.
/// </summary>
/// <remarks>
/// <para>
/// Maps to the <c>core.usp_AuthenticationAttempt_*</c> procedures. The counters
/// live in the database rather than in process memory for a specific reason: the
/// ASP.NET Core rate limiter keeps its state per process, so with two or more
/// IIS nodes an attacker simply spreads attempts across them and the limit never
/// fires (decision TD-10, Claude.md §45). Lockout is a security control, so it is
/// counted where every node sees the same number.
/// </para>
/// <para>
/// The in-process rate limiter is still used, as first-line throttling that
/// keeps obvious floods away from the database. It is not the control.
/// </para>
/// </remarks>
public interface IAuthenticationAttemptRepository
{
    /// <summary>
    /// Reports whether an account is currently locked, before any credential is
    /// checked.
    /// </summary>
    Task<LockoutState> CheckAsync(
        AuthenticationSubject subject,
        string subjectKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt and applies the threshold.
    /// </summary>
    /// <remarks>
    /// Called after <b>any</b> credential failure — wrong password, wrong code,
    /// replayed code, or an identifier that does not exist. Unknown accounts are
    /// counted too: if they were not, the difference in behaviour would itself
    /// reveal which identifiers are real.
    /// </remarks>
    Task<LockoutState> RegisterFailureAsync(
        AuthenticationSubject subject,
        string subjectKey,
        int threshold,
        int lockoutMinutes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears the counter after a fully successful authentication.
    /// </summary>
    /// <remarks>
    /// Only after <b>every</b> factor has passed. Clearing it once the password
    /// is correct but before the authenticator code would let someone who knows
    /// the password hold the counter at zero while guessing codes indefinitely.
    /// </remarks>
    Task ResetAsync(
        AuthenticationSubject subject,
        string subjectKey,
        CancellationToken cancellationToken);
}

/// <summary>Which account type a lockout counter belongs to.</summary>
public enum AuthenticationSubject
{
    /// <summary>An employee using the mobile application.</summary>
    MobileUser = 1,

    /// <summary>An administrator using the portal.</summary>
    Administrator = 2,
}

/// <summary>
/// The lockout position of an account.
/// </summary>
/// <param name="IsLockedOut">Whether the account is currently locked.</param>
/// <param name="LockedUntilUtc">
/// When the lock expires, when one is in force.
/// </param>
/// <param name="FailedCount">Consecutive failures recorded.</param>
public readonly record struct LockoutState(
    bool IsLockedOut,
    DateTimeOffset? LockedUntilUtc,
    int FailedCount);
