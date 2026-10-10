using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// Settings for the shared ASP.NET Core Data Protection key ring
/// (configuration section <c>DataProtection</c>).
/// </summary>
/// <remarks>
/// <para>
/// The key ring encrypts every TOTP secret. The portal encrypts a secret when an
/// administrator enrols an employee; the API decrypts it at every clock-in, on
/// whichever node the load balancer chose. All of them must therefore read one
/// key ring, under one application name (architecture §12).
/// </para>
/// </remarks>
public sealed class KeyRingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Directory holding the key ring, normally a UNC path to a share that only
    /// the API and portal service accounts can read and write.
    /// </summary>
    public string? KeyRingPath { get; set; }

    /// <summary>
    /// Thumbprint of the certificate, in <c>LocalMachine\My</c> on every node, whose
    /// key encrypts the key ring at rest. Its private key must be readable by the
    /// application pool identities.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Thumbprints of earlier key-encryption certificates, still needed to read
    /// keys written before a certificate rotation. Remove one only when no key it
    /// protected remains in the ring.
    /// </summary>
    public IList<string> PreviousCertificateThumbprints { get; } = [];

    /// <summary>
    /// Path to a PKCS#12 (.pfx) file holding the key-encryption certificate
    /// and its private key, as an alternative to
    /// <see cref="CertificateThumbprint"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> A thumbprint is resolved from
    /// <c>LocalMachine\\My</c>, which is a Windows certificate store. On Linux,
    /// .NET maps that location to a directory it will not write to, so a host
    /// deployed there could never find the certificate and — because one is
    /// required outside Development — would refuse to start. A file keeps the
    /// same control on a platform that has no certificate store.
    /// </para>
    /// <para>
    /// The file must be readable only by the service account: it holds the
    /// private key that protects every authenticator secret. It is used with
    /// <see cref="CertificatePassword"/>, and takes precedence over a
    /// thumbprint when both are given.
    /// </para>
    /// </remarks>
    public string? CertificatePath { get; set; }

    /// <summary>Password for <see cref="CertificatePath"/>, if it has one.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Paths of earlier key-encryption certificates, still needed to read the
    /// keys they protected. The file equivalent of
    /// <see cref="PreviousCertificateThumbprints"/>.
    /// </summary>
    public IList<string> PreviousCertificatePaths { get; } = [];
}

/// <summary>
/// Configures Data Protection from <see cref="KeyRingOptions"/>, and refuses to
/// start a non-development host without a shared, encrypted key ring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why refuse rather than warn.</b> Both failure modes are silent at startup
/// and severe later:
/// </para>
/// <list type="bullet">
/// <item>
/// With no path, each process uses a key ring in its own user profile. The
/// portal then enrols authenticators the API on another node cannot decrypt, and
/// nobody can clock in — discovered on the first morning, not at deployment.
/// </item>
/// <item>
/// With a path but no certificate, the framework writes the keys to the share as
/// <b>unencrypted XML</b>: anyone who can read the share can decrypt every TOTP
/// secret. DPAPI is not an alternative for a shared ring, since it binds keys to
/// one machine.
/// </item>
/// </list>
/// <para>
/// Development keeps the framework default so a developer needs neither a share
/// nor a certificate.
/// </para>
/// </remarks>
public static class KeyRingConfiguration
{
    /// <summary>The application name shared by the API and the portal.</summary>
    public const string ApplicationName = "ClockInXtra";

    /// <summary>Registers Data Protection for the host.</summary>
    /// <exception cref="InvalidOperationException">
    /// Outside Development, when the key ring is not shared and encrypted, or a
    /// configured certificate cannot be used.
    /// </exception>
    public static void AddSharedKeyRing(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        KeyRingOptions options = new();
        configuration.GetSection(KeyRingOptions.SectionName).Bind(options);

        IReadOnlyList<string> problems = Validate(options, environment.IsDevelopment());

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The Data Protection key ring is not configured for this environment: " + string.Join(" ", problems));
        }

        IDataProtectionBuilder builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        if (!string.IsNullOrWhiteSpace(options.KeyRingPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(options.KeyRingPath));
        }

        // A file wins over a store lookup when both are set, because a path is
        // unambiguous and a certificate store is not present on every platform.
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            X509Certificate2 current = LoadCertificateFile(options.CertificatePath, options.CertificatePassword);
            builder.ProtectKeysWithCertificate(current);

            X509Certificate2[] readable =
            [
                current,
                .. options.PreviousCertificatePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => LoadCertificateFile(path, options.CertificatePassword)),
            ];

            builder.UnprotectKeysWithAnyCertificate(readable);
        }
        else if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            X509Certificate2 current = LoadCertificate(options.CertificateThumbprint);
            builder.ProtectKeysWithCertificate(current);

            X509Certificate2[] readable =
            [
                current,
                .. options.PreviousCertificateThumbprints
                    .Where(thumbprint => !string.IsNullOrWhiteSpace(thumbprint))
                    .Select(LoadCertificate),
            ];

            builder.UnprotectKeysWithAnyCertificate(readable);
        }
    }

    /// <summary>
    /// Returns what is wrong with the settings for this environment; empty when
    /// they are usable.
    /// </summary>
    public static IReadOnlyList<string> Validate(KeyRingOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> problems = [];

        if (isDevelopment)
        {
            return problems;
        }

        if (string.IsNullOrWhiteSpace(options.KeyRingPath))
        {
            problems.Add(
                "DataProtection:KeyRingPath is required: the API and the portal must share one key ring, " +
                "or authenticator secrets enrolled in the portal cannot be read by the API.");
        }

        if (string.IsNullOrWhiteSpace(options.CertificateThumbprint)
            && string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            problems.Add(
                "DataProtection:CertificateThumbprint or DataProtection:CertificatePath is required: " +
                "without one of them the key ring is written to disk unencrypted, and anyone who can " +
                "read it can decrypt every authenticator secret. Use CertificatePath on Linux, where " +
                "LocalMachine\\My is not a usable certificate store.");
        }

        return problems;
    }

    /// <summary>Loads the key-encryption certificate from a PKCS#12 file.</summary>
    /// <remarks>
    /// The same leniency as the store lookup: an expired certificate must still
    /// decrypt the keys it protected, so validity is not checked. A private key
    /// is not optional — without it the certificate protects nothing, and the
    /// failure would otherwise appear as unreadable authenticator secrets long
    /// after startup rather than as a refusal to start.
    /// </remarks>
    private static X509Certificate2 LoadCertificateFile(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Data Protection certificate file '{path}' does not exist, or the account this process " +
                "runs as cannot see the directory holding it.");
        }

        X509Certificate2 certificate;

        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                string.IsNullOrEmpty(password) ? null : password,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception exception) when (IsPermissionFailure(exception))
        {
            // Separated from a password failure because the two have nothing to
            // do with one another, and reporting a permission error as "check
            // the password" sent a real deployment looking in the wrong place.
            // The certificate is normally readable by the service account only,
            // so the usual cause is a different account running the process.
            throw new InvalidOperationException(
                $"Data Protection certificate file '{path}' cannot be read by the account this process " +
                "runs as. It is normally readable only by the service account, so check which account "
                + "this is running as rather than widening the file's permissions: the private key in it "
                + "protects every authenticator secret.", exception);
        }
        catch (CryptographicException exception)
        {
            // Deliberately echoes neither the password nor the file contents.
            throw new InvalidOperationException(
                $"Data Protection certificate file '{path}' could not be read. Check that it is PKCS#12 "
                + "and that DataProtection:CertificatePassword matches it. To test the password without "
                + "putting it in shell history: PFXPASS=... openssl pkcs12 -in "
                + $"'{path}' -nokeys -noout -passin env:PFXPASS", exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();

            throw new InvalidOperationException(
                $"Data Protection certificate file '{path}' contains no private key. Export it with the " +
                "key, for example: openssl pkcs12 -export -inkey key.pem -in cert.pem -out keyring.pfx");
        }

        return certificate;
    }

    /// <summary>
    /// Whether a failure to load the certificate was the file system refusing
    /// access rather than the contents being wrong. The access error arrives
    /// wrapped in a <see cref="CryptographicException"/>, so the chain has to be
    /// walked.
    /// </summary>
    private static bool IsPermissionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        string normalised = thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);

        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            // On Linux, .NET limits LocalMachine to the Root and
            // CertificateAuthority stores, so this throws rather than returning
            // nothing. Reported as a configuration problem naming the way out,
            // because a raw cryptography error here reads like a broken
            // certificate rather than a setting that cannot work on this
            // platform.
            throw new InvalidOperationException(
                $"The certificate store LocalMachine\\My cannot be opened on this platform, so "
                + $"DataProtection:CertificateThumbprint ({normalised}) cannot be resolved. On Linux, set "
                + "DataProtection:CertificatePath to a PKCS#12 file instead.", exception);
        }

        // validOnly: false — a key-encryption certificate does not need to chain
        // to a trusted root or be within its validity period to protect data
        // (an expired one must still decrypt old keys). It must hold a private key.
        X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, normalised, validOnly: false);

        X509Certificate2? certificate = found.Count > 0 ? found[0] : null;

        if (certificate is null)
        {
            throw new InvalidOperationException(
                $"Data Protection certificate {normalised} was not found in LocalMachine\\My on this server.");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"Data Protection certificate {normalised} has no private key on this server, or the application " +
                "pool identity cannot read it. Grant the identity read access to the private key.");
        }

        return certificate;
    }
}
