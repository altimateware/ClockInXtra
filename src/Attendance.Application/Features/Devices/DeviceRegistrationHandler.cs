using System.Security.Cryptography;
using Attendance.Application.Abstractions;
using Attendance.Application.Services;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Features.Devices;

/// <summary>
/// Device registration: the two-step exchange that binds a key to an employee
/// (Claude.md §18).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the most sensitive endpoint in the mobile API</b>, and the only one
/// that accepts a password from a device that is not yet registered — which makes
/// it the system's main brute-force surface (threat TH-14). Everything below is
/// shaped by that.
/// </para>
/// <para>
/// The exchange is two calls. First <see cref="IssueChallengeAsync"/> returns a
/// single-use random value; then <see cref="RegisterAsync"/> accepts the device's
/// key, an attestation bound to that challenge, and a signature proving the
/// device holds the matching private key. Binding the attestation to a
/// server-issued value is what makes a captured attestation useless (threat
/// TH-10).
/// </para>
/// <para>
/// <b>Why proof of possession is verified here rather than in middleware.</b> The
/// signature middleware authenticates a request by resolving its <c>keyid</c>
/// against <c>core.Device</c>. During registration that row does not exist yet,
/// so the only place that can check the signature against the key <em>being
/// presented</em> is this handler, which has it in hand.
/// </para>
/// <para>
/// <b>Order of checks.</b> Lockout, then both credentials, then attestation and
/// proof of possession, then the database. Authenticating before verifying the
/// attestation costs a chain validation on a request that turned out to be
/// authentic, but it means an attestation failure can be attributed to a known
/// employee in the security trail — an anonymous attestation failure is much
/// harder to act on. Neither order weakens the control, since both must pass.
/// </para>
/// </remarks>
public sealed class DeviceRegistrationHandler
{
    /// <summary>
    /// Challenge size. 32 bytes matches the column and exceeds by a wide margin
    /// what is needed to make guessing a live challenge infeasible.
    /// </summary>
    private const int ChallengeSizeBytes = 32;

    private readonly IAttendancePolicyProvider _policyProvider;
    private readonly EmployeeAuthenticator _authenticator;
    private readonly IDeviceAttestationVerifier _attestation;
    private readonly IRequestSignatureVerifier _signatures;
    private readonly IDeviceRepository _devices;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the handler.</summary>
    public DeviceRegistrationHandler(
        IAttendancePolicyProvider policyProvider,
        EmployeeAuthenticator authenticator,
        IDeviceAttestationVerifier attestation,
        IRequestSignatureVerifier signatures,
        IDeviceRepository devices,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _policyProvider = policyProvider;
        _authenticator = authenticator;
        _attestation = attestation;
        _signatures = signatures;
        _devices = devices;
        _securityEvents = securityEvents;
    }

    /// <summary>
    /// Issues a single-use registration challenge.
    /// </summary>
    /// <remarks>
    /// No employee is referenced, so this endpoint cannot be used to discover
    /// whether an identifier exists. The bytes are returned to the caller because
    /// the device has to embed them in its attestation; they are not a secret,
    /// only unpredictable and short-lived.
    /// </remarks>
    public async Task<IssueChallengeResponse> IssueChallengeAsync(
        IssueChallengeCommand command,
        CancellationToken cancellationToken)
    {
        AttendancePolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeSizeBytes);

        RegistrationChallenge issued = await _devices
            .IssueRegistrationChallengeAsync(
                challenge,
                policy.ChallengeLifetimeSeconds,
                command.SourceAddressHash,
                cancellationToken)
            .ConfigureAwait(false);

        return new IssueChallengeResponse(
            issued.ResultCode, issued.ChallengeId, challenge, issued.ExpiresUtc);
    }

    /// <summary>Registers a device against a previously issued challenge.</summary>
    public async Task<DeviceRegistrationResponse> RegisterAsync(
        DeviceRegistrationCommand command,
        CancellationToken cancellationToken)
    {
        AttendancePolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        // 1. The key must be a well-formed uncompressed P-256 point before it is
        //    worth doing anything else with it. Rejected here rather than at the
        //    database's check constraint, so the answer is a validation failure
        //    rather than an unexpected SQL error.
        if (command.PublicKey is not { Length: 65 } publicKey || publicKey[0] != 0x04)
        {
            await RecordAsync(command, "Device.RegistrationRefused", SecurityEventSeverity.Warning, "PUBLIC_KEY_MALFORMED", cancellationToken)
                .ConfigureAwait(false);

            return DeviceRegistrationResponse.Failed(AttendanceResultCode.InvalidRequest);
        }

        // 2. Lockout, before any credential work is spent.
        LockoutState lockout = await _authenticator
            .CheckLockoutAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        if (lockout.IsLockedOut)
        {
            await RecordAsync(command, "Auth.Locked", SecurityEventSeverity.Warning, "ACCOUNT_LOCKED", cancellationToken)
                .ConfigureAwait(false);

            return DeviceRegistrationResponse.Failed(AttendanceResultCode.AccountLocked);
        }

        // 3. Password and authenticator code, with the time step consumed.
        EmployeeAuthenticationResult authentication = await _authenticator
            .AuthenticateAsync(
                new EmployeeAuthenticationRequest(
                    command.UserId,
                    command.Password,
                    command.AuthenticatorCode,
                    DevicePublicId: null,   // there is no device yet
                    command.SourceAddressHash,
                    command.CorrelationId),
                policy,
                cancellationToken)
            .ConfigureAwait(false);

        if (!authentication.IsAuthenticated)
        {
            return DeviceRegistrationResponse.Failed(authentication.ResultCode);
        }

        // 4. Proof of possession: the request must be signed by the private key
        //    matching the public key being registered. Without this, an attacker
        //    could register somebody else's public key.
        if (!_signatures.Verify(command.SignatureBase.Span, command.Signature.Span, publicKey))
        {
            await RecordAsync(command, "Device.RegistrationRefused", SecurityEventSeverity.Critical, "PROOF_OF_POSSESSION_FAILED", cancellationToken)
                .ConfigureAwait(false);

            return DeviceRegistrationResponse.Failed(AttendanceResultCode.Unauthorized);
        }

        // 5. Platform attestation, where policy requires it.
        AttestationLevel level = AttestationLevel.None;
        bool attestationRequired = command.Platform switch
        {
            DevicePlatform.Android => policy.RequireHardwareAttestationAndroid,
            DevicePlatform.Ios => policy.RequireHardwareAttestationIos,
            _ => true,
        };

        if (attestationRequired)
        {
            AttestationVerificationResult verified = await _attestation
                .VerifyAsync(
                    new AttestationVerificationRequest(
                        command.Platform,
                        command.Attestation,
                        command.Challenge,
                        publicKey,
                        command.AttestationKeyId),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!verified.IsAccepted || verified.Level != AttestationLevel.Hardware)
            {
                // The precise reason goes to the trail only. Telling the client
                // which part of the attestation failed would tell an attacker
                // which part of a forgery to fix.
                await RecordAsync(
                        command,
                        "Device.AttestationRejected",
                        SecurityEventSeverity.Critical,
                        verified.ReasonCode ?? "ATTESTATION_LEVEL_INSUFFICIENT",
                        cancellationToken)
                    .ConfigureAwait(false);

                return DeviceRegistrationResponse.Failed(AttendanceResultCode.AttestationRejected);
            }

            level = verified.Level;
        }

        // 6. The database owns consuming the challenge exactly once, refusing a
        //    key already bound elsewhere, and applying DEC-04.
        DeviceRegistrationOutcome outcome = await _devices
            .RegisterAsync(
                new DeviceRegistrationRequest(
                    command.ChallengeId,
                    command.Challenge,
                    authentication.MobileUserId!.Value,
                    publicKey,
                    SHA256.HashData(publicKey),
                    command.Platform,
                    level,
                    command.DeviceModel,
                    command.OsVersion,
                    command.AppVersion,
                    policy.DeviceRegistrationRequiresApproval,
                    command.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.ResultCode is not (AttendanceResultCode.Success or AttendanceResultCode.RegistrationPendingApproval))
        {
            await RecordAsync(
                    command,
                    "Device.RegistrationRefused",
                    SecurityEventSeverity.Warning,
                    outcome.ResultCode.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            return DeviceRegistrationResponse.Failed(outcome.ResultCode);
        }

        return new DeviceRegistrationResponse(
            outcome.ResultCode,
            outcome.DevicePublicId,
            outcome.Status,
            RequiresApproval: outcome.Status == DeviceStatus.PendingApproval);
    }

    private Task RecordAsync(
        DeviceRegistrationCommand command,
        string eventType,
        SecurityEventSeverity severity,
        string reasonCode,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            new SecurityEvent(
                eventType,
                severity,
                SecurityEventSubject.MobileUser,
                command.UserId,
                reasonCode,
                SourceApplication: "Attendance.Api",
                DevicePublicId: null,
                SourceAddressHash: command.SourceAddressHash,
                CorrelationId: command.CorrelationId),
            cancellationToken);
}

/// <summary>A request for a registration challenge.</summary>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
/// <param name="CorrelationId">Ties the exchange together in the logs.</param>
public readonly record struct IssueChallengeCommand(
    byte[]? SourceAddressHash,
    Guid CorrelationId);

/// <summary>An issued challenge.</summary>
/// <param name="ResultCode">The outcome.</param>
/// <param name="ChallengeId">Returned with the registration request.</param>
/// <param name="Challenge">The bytes the device embeds in its attestation.</param>
/// <param name="ExpiresUtc">When it stops being usable.</param>
public readonly record struct IssueChallengeResponse(
    AttendanceResultCode ResultCode,
    Guid ChallengeId,
    byte[] Challenge,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// A request to register a device.
/// </summary>
/// <param name="UserId">The identifier the employee typed.</param>
/// <param name="Password">Verified and discarded; never stored or logged (§24).</param>
/// <param name="AuthenticatorCode">The six-digit code.</param>
/// <param name="ChallengeId">The challenge being consumed.</param>
/// <param name="Challenge">Its bytes, as returned by the challenge endpoint.</param>
/// <param name="PublicKey">Uncompressed P-256 point: 0x04 ‖ X(32) ‖ Y(32).</param>
/// <param name="Platform">Android or iOS.</param>
/// <param name="Attestation">The platform attestation blob.</param>
/// <param name="AttestationKeyId">Apple's key identifier; unused on Android.</param>
/// <param name="SignatureBase">The RFC 9421 signature base for this request.</param>
/// <param name="Signature">Its signature, proving possession of the private key.</param>
/// <param name="DeviceModel">Untrusted client metadata.</param>
/// <param name="OsVersion">Untrusted client metadata.</param>
/// <param name="AppVersion">Untrusted client metadata.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct DeviceRegistrationCommand(
    string UserId,
    string Password,
    string AuthenticatorCode,
    Guid ChallengeId,
    ReadOnlyMemory<byte> Challenge,
    byte[] PublicKey,
    DevicePlatform Platform,
    ReadOnlyMemory<byte> Attestation,
    ReadOnlyMemory<byte> AttestationKeyId,
    ReadOnlyMemory<byte> SignatureBase,
    ReadOnlyMemory<byte> Signature,
    string? DeviceModel,
    string? OsVersion,
    string? AppVersion,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>The outcome of a registration.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="DevicePublicId">The new device's identifier, when one was created.</param>
/// <param name="Status">The status it was created with.</param>
/// <param name="RequiresApproval">
/// Whether an administrator must still approve it. The app shows "waiting for
/// approval" rather than a clock-in button that cannot work.
/// </param>
public readonly record struct DeviceRegistrationResponse(
    AttendanceResultCode ResultCode,
    Guid? DevicePublicId,
    DeviceStatus? Status,
    bool RequiresApproval)
{
    /// <summary>A refusal carrying only its code.</summary>
    public static DeviceRegistrationResponse Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null, null, false);
}
