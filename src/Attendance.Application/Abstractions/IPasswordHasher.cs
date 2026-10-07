namespace Attendance.Application.Abstractions;

/// <summary>
/// Hashes and verifies passwords.
/// </summary>
/// <remarks>
/// <para>
/// Passwords are hashed, never encrypted (Claude.md §16: encryption is not a
/// substitute for hashing). The parameters travel with the hash so that the work
/// factor can be raised later without invalidating existing credentials — each
/// stored hash records the format and iteration count it was produced with.
/// </para>
/// <para>
/// Implementations must not throw when the stored parameters are unrecognised.
/// A malformed credential row is a failed verification, not an exception:
/// throwing would turn a data problem into a way of probing the service.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a password with the current parameters.</summary>
    /// <exception cref="ArgumentException">The password is null or empty.</exception>
    PasswordHash Hash(string password);

    /// <summary>
    /// Verifies a password against stored parameters, in constant time with
    /// respect to the hash contents.
    /// </summary>
    bool Verify(string password, PasswordHash stored);

    /// <summary>
    /// Indicates whether a stored credential was produced with weaker
    /// parameters than the current ones and should be re-hashed on next
    /// successful sign-in.
    /// </summary>
    bool NeedsRehash(PasswordHash stored);

    /// <summary>
    /// Performs a hash of equivalent cost and discards it.
    /// </summary>
    /// <remarks>
    /// Called when no credential exists for the presented identifier. Without
    /// it, an unknown user is refused in microseconds while a known one costs
    /// the full PBKDF2 work — a timing difference that lets an attacker
    /// enumerate valid user identifiers on an internet-facing API (threat
    /// TH-14). The uniform error code alone is not enough; the timing has to be
    /// uniform too.
    /// </remarks>
    void PerformDummyVerification();
}

/// <summary>
/// A stored password hash together with the parameters that produced it.
/// </summary>
/// <param name="HashFormat">
/// Algorithm identifier, matching <c>core.EmployeeCredential.HashFormat</c>
/// (for example <c>pbkdf2-sha512</c>).
/// </param>
/// <param name="Iterations">Iteration count used.</param>
/// <param name="Salt">Per-credential random salt.</param>
/// <param name="Hash">The derived key.</param>
public readonly record struct PasswordHash(
    string HashFormat,
    int Iterations,
    byte[] Salt,
    byte[] Hash);
