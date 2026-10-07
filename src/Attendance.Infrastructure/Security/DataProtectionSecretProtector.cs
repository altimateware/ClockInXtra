using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretProtector"/> backed by ASP.NET Core Data Protection
/// (decision TD-06).
/// </summary>
/// <remarks>
/// <para>
/// Data Protection is used rather than hand-rolled AES because Claude.md §23 is
/// explicit that custom cryptography is not to be invented. It provides
/// authenticated encryption, key rotation and a key ring that can be shared
/// between the API and the portal, which is required here: the portal encrypts a
/// TOTP secret at enrolment and the API decrypts it at clock-in.
/// </para>
/// <para>
/// <b>Operational rules that make this safe</b> (documented in the deployment
/// guide, not just here):
/// </para>
/// <list type="bullet">
///   <item>The key ring lives on an access-controlled share and is encrypted
///   with an organisation PKI certificate, so a database backup alone does not
///   yield the secrets.</item>
///   <item>API and portal set the same application name, or they will not
///   resolve the same keys.</item>
///   <item>Keys are never deleted. Microsoft documents that data protected by a
///   deleted key is "permanently undecipherable, and there's no emergency
///   override like there's with revoked keys" — here that would mean every
///   employee re-enrolling their authenticator.</item>
///   <item>The key ring and the certificate's private key are part of the backup
///   and restore drill. Restoring the database without them restores nothing
///   usable.</item>
/// </list>
/// </remarks>
public sealed partial class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtectionProvider _provider;
    private readonly ILogger<DataProtectionSecretProtector> _logger;

    /// <summary>Creates the protector.</summary>
    public DataProtectionSecretProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionSecretProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("The value to protect must not be empty.", nameof(plaintext));
        }

        IDataProtector protector = _provider.CreateProtector(purpose);
        return protector.Protect(plaintext.ToArray());
    }

    /// <inheritdoc />
    public bool TryUnprotect(string purpose, byte[] protectedPayload, out byte[] plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        plaintext = [];

        if (protectedPayload is null or { Length: 0 })
        {
            return false;
        }

        try
        {
            IDataProtector protector = _provider.CreateProtector(purpose);
            plaintext = protector.Unprotect(protectedPayload);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Deliberately caught and reported as a failure. This happens when
            // the key ring is missing or was replaced, when the payload was
            // protected for a different purpose, or when the row was tampered
            // with. All of those are operational conditions on the clock-in path
            // and must not surface as an unhandled exception.
            LogUnprotectFailed(_logger, ex, purpose);

            return false;
        }
    }

    /// <summary>
    /// Logs a failed decryption.
    /// </summary>
    /// <remarks>
    /// A source-generated logger message rather than the <c>LogError</c>
    /// extension: the generated delegate avoids boxing and formatting work when
    /// the level is disabled, which is what CA1848 asks for on a path that runs
    /// during clock-in.
    ///
    /// The purpose is logged; the payload never is. Writing ciphertext to a log
    /// would move protected material into files handled with far less care than
    /// the database (§33).
    /// </remarks>
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "Failed to unprotect a stored secret for purpose {Purpose}. The Data Protection key ring may be missing or replaced, the payload may have been protected for a different purpose, or the stored value may be corrupt.")]
    private static partial void LogUnprotectFailed(ILogger logger, Exception exception, string purpose);
}
