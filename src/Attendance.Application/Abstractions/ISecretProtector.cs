namespace Attendance.Application.Abstractions;

/// <summary>
/// Encrypts and decrypts secrets held at rest in the database.
/// </summary>
/// <remarks>
/// <para>
/// Used for TOTP shared secrets, which Claude.md §13 requires to be encrypted at
/// rest and never stored in plaintext, and for clock-in coordinates if OPEN-35
/// decides they must be retained.
/// </para>
/// <para>
/// This is <b>reversible encryption, and that is the point</b> — the server must
/// recover a TOTP secret to verify a code. It is the opposite of how passwords
/// are treated, which are hashed and never recovered (§16). Confusing the two is
/// a common and serious mistake, so the two capabilities are separate interfaces
/// with names that cannot be mixed up.
/// </para>
/// <para>
/// The implementation is ASP.NET Core Data Protection with a key ring shared by
/// the API and the portal (decision TD-06): the portal encrypts a secret at
/// enrolment and the API decrypts it at clock-in, so both must resolve the same
/// keys. The keys are never deleted — Microsoft documents that deleting a key
/// makes everything it protected permanently undecipherable, with no override —
/// which for this system would mean re-enrolling every employee's authenticator.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Encrypts a secret for storage.
    /// </summary>
    /// <param name="purpose">
    /// Isolates one kind of secret from another. A payload protected for one
    /// purpose cannot be decrypted under a different one, so a bug that fed a
    /// stored coordinate blob into the authenticator path would fail loudly
    /// instead of producing nonsense.
    /// </param>
    /// <param name="plaintext">The secret.</param>
    byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Decrypts a stored secret.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the plaintext when the payload is intact and
    /// was protected for this purpose; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Returns a flag rather than throwing, because a payload that cannot be
    /// decrypted is an operational condition the caller has to handle — a
    /// revoked key, a restored database missing its key ring, or a tampered row
    /// — and it should surface as a clear failure rather than an unhandled
    /// exception on the clock-in path.
    /// </remarks>
    bool TryUnprotect(string purpose, byte[] protectedPayload, out byte[] plaintext);
}

/// <summary>
/// Purpose strings for <see cref="ISecretProtector"/>.
/// </summary>
/// <remarks>
/// Constants rather than literals: a typo in a purpose string would make every
/// previously stored secret undecryptable, and the failure would not appear
/// until someone tried to clock in.
/// </remarks>
public static class SecretPurposes
{
    /// <summary>TOTP shared secrets for employees.</summary>
    public const string MobileUserTotpSecret = "ClockInXtra.MobileUser.TotpSecret.v1";

    /// <summary>TOTP shared secrets for administrators.</summary>
    public const string AdministratorTotpSecret = "ClockInXtra.Administrator.TotpSecret.v1";

    /// <summary>Clock-in and clock-out coordinates, if OPEN-35 requires them.</summary>
    public const string AttendanceCoordinates = "ClockInXtra.Attendance.Coordinates.v1";
}
