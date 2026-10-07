using System.Security.Cryptography;
using Attendance.Application.Abstractions;
using Attendance.Application.Features.Devices;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="DeviceRegistrationHandler"/>.
/// </summary>
/// <remarks>
/// Registration is the only mobile endpoint that accepts a password from a device
/// that is not yet registered, which the database's own procedure calls the
/// system's main brute-force surface. These tests hold the order of its checks in
/// place, and pin the two refusals that exist to stop somebody registering a key
/// they do not hold.
/// </remarks>
public sealed class DeviceRegistrationHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task RegistersAndWaitsForApproval_WhenPolicyRequiresIt()
    {
        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.RegistrationPendingApproval, response.ResultCode);
        Assert.True(response.RequiresApproval);
        Assert.Equal(DeviceStatus.PendingApproval, response.Status);
        Assert.NotNull(response.DevicePublicId);
    }

    [Fact]
    public async Task RefusesAMalformedPublicKeyBeforeSpendingAnyCredentialWork()
    {
        // A 64-byte value cannot be an uncompressed P-256 point. That is a
        // validation failure, answered before the server spends hundreds of
        // milliseconds of PBKDF2 work on it.
        DeviceRegistrationResponse response = await Register(publicKey: new byte[64]);

        Assert.Equal(AttendanceResultCode.InvalidRequest, response.ResultCode);
        Assert.DoesNotContain("credentials.validate", _harness.Calls);
        Assert.DoesNotContain("devices.register", _harness.Calls);
    }

    [Fact]
    public async Task RefusesAKeyWithoutTheUncompressedPointPrefix()
    {
        byte[] wrongPrefix = AttendanceTestHarness.WellFormedPublicKey();
        wrongPrefix[0] = 0x02;   // compressed-point prefix

        DeviceRegistrationResponse response = await Register(publicKey: wrongPrefix);

        Assert.Equal(AttendanceResultCode.InvalidRequest, response.ResultCode);
    }

    [Fact]
    public async Task RefusesALockedAccountBeforeVerifyingThePassword()
    {
        _harness.Lockout.IsLockedOut = true;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.AccountLocked, response.ResultCode);
        Assert.DoesNotContain("credentials.validate", _harness.Calls);
    }

    [Fact]
    public async Task VerifiesCredentialsBeforeTheAttestation()
    {
        // So that an attestation failure can be attributed to a known employee
        // in the security trail rather than being anonymous.
        await Register();

        int credentialIndex = _harness.Calls.IndexOf("credentials.validate");
        int attestationIndex = _harness.Calls.IndexOf("attestation.verify");

        Assert.True(credentialIndex >= 0 && attestationIndex >= 0);
        Assert.True(credentialIndex < attestationIndex);
    }

    [Fact]
    public async Task DoesNotRegisterWhenTheCredentialsAreWrong()
    {
        _harness.Credentials.Outcome = CredentialValidationOutcome.InvalidPassword;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.InvalidCredentials, response.ResultCode);
        Assert.DoesNotContain("attestation.verify", _harness.Calls);
        Assert.DoesNotContain("devices.register", _harness.Calls);
    }

    [Fact]
    public async Task RefusesWhenTheRequestIsNotSignedByTheKeyBeingRegistered()
    {
        // Without this check, an attacker who knew a password and one code could
        // register somebody else's public key.
        _harness.Signatures.IsValid = false;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.Unauthorized, response.ResultCode);
        Assert.DoesNotContain("attestation.verify", _harness.Calls);
        Assert.DoesNotContain("devices.register", _harness.Calls);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "PROOF_OF_POSSESSION_FAILED");
    }

    [Fact]
    public async Task RefusesARejectedAttestationWithoutRevealingWhy()
    {
        _harness.Attestation.IsAccepted = false;
        _harness.Attestation.ReasonCode = "ATTESTATION_CHALLENGE_MISMATCH";

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.AttestationRejected, response.ResultCode);
        Assert.DoesNotContain("devices.register", _harness.Calls);

        // The detail exists, but only in the trail: telling the client which part
        // failed would tell an attacker which part of a forgery to fix.
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "ATTESTATION_CHALLENGE_MISMATCH");
    }

    [Fact]
    public async Task RefusesASoftwareOnlyKeyWhenHardwareAttestationIsRequired()
    {
        _harness.Attestation.Level = AttestationLevel.Software;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.AttestationRejected, response.ResultCode);
    }

    [Fact]
    public async Task SkipsAttestationWhenPolicyDoesNotRequireIt()
    {
        _harness.Policy.Policy = _harness.Policy.Policy with { RequireHardwareAttestationAndroid = false };

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.RegistrationPendingApproval, response.ResultCode);
        Assert.DoesNotContain("attestation.verify", _harness.Calls);

        // What was not verified is recorded as not verified, rather than being
        // optimistically stored as hardware-backed.
        Assert.Equal(AttestationLevel.None, _harness.Devices.LastRegistration!.Value.AttestationLevel);
    }

    [Fact]
    public async Task StoresTheThumbprintAsSha256OfThePublicKey()
    {
        byte[] key = AttendanceTestHarness.WellFormedPublicKey();

        await Register(publicKey: key);

        DeviceRegistrationRequest stored = _harness.Devices.LastRegistration!.Value;
        Assert.Equal(SHA256.HashData(key), stored.PublicKeyThumbprint);
        Assert.Equal(key, stored.PublicKey);
    }

    [Fact]
    public async Task PassesTheApprovalPolicyThroughToTheDatabase()
    {
        await Register();
        Assert.True(_harness.Devices.LastRegistration!.Value.RequiresApproval);

        AttendanceTestHarness noApproval = new();
        noApproval.Policy.Policy = noApproval.Policy.Policy with { DeviceRegistrationRequiresApproval = false };
        noApproval.Devices.RegisterResult = AttendanceResultCode.Success;
        noApproval.Devices.RegisteredStatus = DeviceStatus.Active;

        DeviceRegistrationResponse response = await Register(noApproval);

        Assert.False(noApproval.Devices.LastRegistration!.Value.RequiresApproval);
        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.False(response.RequiresApproval);
    }

    [Fact]
    public async Task ReportsTheDatabaseRefusalWhenTheEmployeeAlreadyHasAnActiveDevice()
    {
        // DEC-04. Replacing a device is always an administrative act, so this
        // refusal is passed through rather than worked around.
        _harness.Devices.RegisterResult = AttendanceResultCode.ActiveDeviceAlreadyExists;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.ActiveDeviceAlreadyExists, response.ResultCode);
        Assert.Null(response.DevicePublicId);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "ActiveDeviceAlreadyExists");
    }

    [Fact]
    public async Task ReportsAnExpiredChallenge()
    {
        _harness.Devices.RegisterResult = AttendanceResultCode.ChallengeExpired;

        DeviceRegistrationResponse response = await Register();

        Assert.Equal(AttendanceResultCode.ChallengeExpired, response.ResultCode);
    }

    [Fact]
    public async Task IssuesAChallengeOfTheConfiguredLifetimeAndFullWidth()
    {
        IssueChallengeResponse response = await IssueChallenge();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.Equal(32, response.Challenge.Length);
        Assert.Equal(300, _harness.Devices.LastChallengeLifetimeSeconds);
        Assert.True(response.ExpiresUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task IssuesADifferentChallengeEveryTime()
    {
        // A predictable challenge would let an attacker prepare an attestation in
        // advance, which is the whole point of issuing one.
        await IssueChallenge();
        await IssueChallenge();
        await IssueChallenge();

        Assert.Equal(3, _harness.Devices.IssuedChallenges.Count);
        Assert.Equal(3, _harness.Devices.IssuedChallenges.Select(Convert.ToBase64String).Distinct().Count());
    }

    private Task<IssueChallengeResponse> IssueChallenge() =>
        Handler(_harness).IssueChallengeAsync(
            new IssueChallengeCommand(SourceAddressHash: null, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

    private Task<DeviceRegistrationResponse> Register(byte[]? publicKey = null) => Register(_harness, publicKey);

    private static Task<DeviceRegistrationResponse> Register(AttendanceTestHarness harness, byte[]? publicKey = null)
    {
        DeviceRegistrationCommand command = new(
            AttendanceTestHarness.UserId,
            AttendanceTestHarness.Password,
            AttendanceTestHarness.Code,
            Guid.NewGuid(),
            new byte[32],
            publicKey ?? AttendanceTestHarness.WellFormedPublicKey(),
            DevicePlatform.Android,
            new byte[128],
            new byte[32],
            new byte[64],
            new byte[64],
            "Pixel 8",
            "Android 15",
            "1.0.0",
            Guid.NewGuid(),
            SourceAddressHash: null);

        return Handler(harness).RegisterAsync(command, TestContext.Current.CancellationToken);
    }

    private static DeviceRegistrationHandler Handler(AttendanceTestHarness harness) =>
        new(harness.Policy,
            harness.Authenticator,
            harness.Attestation,
            harness.Signatures,
            harness.Devices,
            harness.SecurityEvents);
}
