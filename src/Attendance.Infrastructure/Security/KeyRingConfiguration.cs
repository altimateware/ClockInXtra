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

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
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

        if (string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            problems.Add(
                "DataProtection:CertificateThumbprint is required: without it the key ring is written " +
                "to disk unencrypted, and anyone who can read it can decrypt every authenticator secret.");
        }

        return problems;
    }

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        string normalised = thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

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
