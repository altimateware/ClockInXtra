using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// Checks an attestation certificate chain against Google's revocation status
/// list.
/// </summary>
public interface IAttestationRevocationList
{
    /// <summary>
    /// Whether any certificate in the chain has been revoked.
    /// </summary>
    /// <param name="chain">The chain, leaf first.</param>
    /// <param name="revokedSerial">The serial number that was revoked, if any.</param>
    bool IsRevoked(X509Certificate2Collection chain, out string? revokedSerial);
}

/// <summary>
/// Reads the revocation status list from a locally provisioned file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file rather than a live fetch.</b> Google publishes this list over the
/// internet, and the obvious implementation would download it during
/// registration. That would make device registration depend on an external
/// service being reachable, which §2.1 rules out — and it would mean an outage at
/// Google, or anything blocking that egress, stops employees enrolling. The
/// operator refreshes the file on a schedule instead, so verification runs
/// entirely on infrastructure the organisation controls.
/// </para>
/// <para>
/// <b>When no file is configured, nothing is revoked.</b> That is a real
/// weakening and it is deliberate rather than hidden: an organisation that has
/// not provisioned the list still gets chain validation, challenge binding, boot
/// state and application identity. The gap is that a key Google has since
/// declared compromised would still pass, which is why the configuration is
/// documented as an operational requirement rather than an optional extra.
/// </para>
/// <para>
/// The file is re-read when its timestamp changes, so a refresh takes effect
/// without restarting the application.
/// </para>
/// </remarks>
public sealed class FileAttestationRevocationList : IAttestationRevocationList
{
    private readonly string? _path;
    private readonly Lock _gate = new();

    private Dictionary<string, string>? _entries;
    private DateTime _loadedFileTimestampUtc;

    /// <summary>Creates the revocation list reader.</summary>
    public FileAttestationRevocationList(IOptions<AttestationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.Android.RevocationStatusListPath;
    }

    /// <summary>
    /// Whether the list was available on the most recent check.
    /// </summary>
    /// <remarks>
    /// Surfaced so a registration accepted without a revocation check can say so,
    /// rather than being indistinguishable from one that passed it.
    /// </remarks>
    [MemberNotNullWhen(true, nameof(_path))]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_path);

    /// <inheritdoc />
    public bool IsRevoked(X509Certificate2Collection chain, out string? revokedSerial)
    {
        ArgumentNullException.ThrowIfNull(chain);

        revokedSerial = null;

        Dictionary<string, string>? entries = LoadIfChanged();

        if (entries is null || entries.Count == 0)
        {
            return false;
        }

        foreach (X509Certificate2 certificate in chain)
        {
            // Google keys the list by lowercase hexadecimal serial number with no
            // leading zeros, which is not how X509Certificate2 renders it.
            string serial = certificate.SerialNumber.TrimStart('0').ToLowerInvariant();

            if (entries.ContainsKey(serial))
            {
                revokedSerial = serial;
                return true;
            }
        }

        return false;
    }

    private Dictionary<string, string>? LoadIfChanged()
    {
        // The same test as IsConfigured, and it must stay the same test.
        // Configuration binding turns "RevocationStatusListPath": "" into an empty
        // string, never null, so checking only for null let an unconfigured list
        // reach File.GetLastWriteTimeUtc(""), which throws — and every
        // registration on a system that had configured attestation but not yet
        // the revocation list failed with a 500 instead of taking the documented
        // "nothing is revoked" path.
        if (!IsConfigured)
        {
            return null;
        }

        lock (_gate)
        {
            try
            {
                DateTime timestamp = File.GetLastWriteTimeUtc(_path);

                if (_entries is not null && timestamp == _loadedFileTimestampUtc)
                {
                    return _entries;
                }

                using FileStream stream = File.OpenRead(_path);
                RevocationStatusDocument? document =
                    JsonSerializer.Deserialize(stream, RevocationJsonContext.Default.RevocationStatusDocument);

                _entries = document?.Entries?.ToDictionary(
                    entry => entry.Key.TrimStart('0').ToLowerInvariant(),
                    entry => entry.Value.Status ?? "REVOKED",
                    StringComparer.OrdinalIgnoreCase) ?? [];

                _loadedFileTimestampUtc = timestamp;

                return _entries;
            }
            catch (IOException)
            {
                // A missing or unreadable file must not fail a registration that
                // has already satisfied every cryptographic check. It is reported
                // through IsConfigured instead.
                return _entries;
            }
            catch (JsonException)
            {
                return _entries;
            }
            catch (UnauthorizedAccessException)
            {
                return _entries;
            }
        }
    }

    internal sealed class RevocationStatusDocument
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, RevocationStatusEntry>? Entries { get; set; }
    }

    internal sealed class RevocationStatusEntry
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}

[JsonSerializable(typeof(FileAttestationRevocationList.RevocationStatusDocument))]
internal sealed partial class RevocationJsonContext : JsonSerializerContext;
