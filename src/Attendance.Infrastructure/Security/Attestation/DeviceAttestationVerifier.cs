using Attendance.Application.Abstractions;
using Attendance.Domain.ValueObjects;

namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// Dispatches attestation verification to the platform that produced it.
/// </summary>
/// <remarks>
/// <para>
/// The application layer asks one question — "is this device's key genuinely
/// hardware-backed, and bound to my challenge?" — and the answer comes from two
/// entirely different mechanisms. Keeping the dispatch here means no handler has
/// to know which.
/// </para>
/// <para>
/// <b>An unknown platform is refused.</b> Not treated as "no attestation
/// required": a request claiming a platform this server does not recognise is
/// either a client far newer than the server, or someone probing for a path that
/// skips the check.
/// </para>
/// <para>
/// Both verifiers are synchronous — they do no I/O beyond reading a local root
/// certificate and, for Android, a locally provisioned revocation list. The
/// asynchronous signature exists because the port must not forbid an
/// implementation that does need I/O.
/// </para>
/// </remarks>
public sealed class DeviceAttestationVerifier : IDeviceAttestationVerifier
{
    private readonly AndroidKeyAttestationVerifier _android;
    private readonly AppleAppAttestVerifier _apple;

    /// <summary>Creates the verifier.</summary>
    public DeviceAttestationVerifier(
        AndroidKeyAttestationVerifier android,
        AppleAppAttestVerifier apple)
    {
        ArgumentNullException.ThrowIfNull(android);
        ArgumentNullException.ThrowIfNull(apple);

        _android = android;
        _apple = apple;
    }

    /// <inheritdoc />
    public Task<AttestationVerificationResult> VerifyAsync(
        AttestationVerificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AttestationVerificationResult result = request.Platform switch
        {
            DevicePlatform.Android => _android.Verify(request),
            DevicePlatform.Ios => _apple.Verify(request),
            _ => AttestationVerificationResult.Rejected("ATTESTATION_PLATFORM_UNSUPPORTED"),
        };

        return Task.FromResult(result);
    }
}
