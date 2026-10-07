using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="ClockInHandler"/>.
/// </summary>
/// <remarks>
/// The ordering of checks inside this handler is the security design, so these
/// tests assert the order itself, not only the returned code. A refactor that
/// quietly moved password verification ahead of the lockout check, or reported
/// the real reason for a credential failure, would pass a naive test and fail
/// these.
/// </remarks>
public sealed class ClockInHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task Succeeds_WhenEveryCheckPasses()
    {
        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.NotNull(response.AttendancePublicId);
        Assert.Contains("attendance.clockIn", _harness.Calls);
    }

    [Fact]
    public async Task RefusesALockedAccountBeforeSpendingAnyPasswordWork()
    {
        // The lockout check exists to stop an attacker making the server do
        // hundreds of milliseconds of PBKDF2 work per attempt.
        _harness.Lockout.IsLockedOut = true;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.AccountLocked, response.ResultCode);
        Assert.DoesNotContain("credentials.validate", _harness.Calls);
        Assert.DoesNotContain("attendance.clockIn", _harness.Calls);
    }

    [Fact]
    public async Task EvaluatesLocationBeforeVerifyingThePassword()
    {
        // Cheap before expensive: location is arithmetic on data already in
        // hand, password verification is deliberate work.
        await Handle();

        int locationIndex = _harness.Calls.IndexOf("offices.getActive");
        int passwordIndex = _harness.Calls.IndexOf("credentials.validate");

        Assert.True(locationIndex >= 0 && passwordIndex >= 0);
        Assert.True(locationIndex < passwordIndex, "location must be evaluated before password hashing");
    }

    [Fact]
    public async Task RefusesADistantPositionWithoutTouchingCredentials()
    {
        ClockInResponse response = await Handle(position: AttendanceTestHarness.DistantPosition);

        Assert.Equal(AttendanceResultCode.LocationNotAllowed, response.ResultCode);
        Assert.DoesNotContain("credentials.validate", _harness.Calls);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "LOCATION_NOT_ALLOWED");
    }

    [Fact]
    public async Task RefusesAMockedPosition()
    {
        ReportedPosition mocked = new(
            AttendanceTestHarness.AcceptablePosition.Coordinates,
            accuracyMeters: 3d,
            DevicePlatform.Android,
            isMocked: true);

        ClockInResponse response = await Handle(position: mocked);

        Assert.Equal(AttendanceResultCode.LocationSourceUntrusted, response.ResultCode);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "LOCATION_SOURCE_UNTRUSTED");
    }

    [Fact]
    public async Task ReportsTheSameCodeForAWrongPasswordAndAWrongAuthenticatorCode()
    {
        // This is the oracle CON-09 closes. If these two differed, an attacker
        // would know when they had found the right password.
        _harness.Credentials.Outcome = CredentialValidationOutcome.InvalidPassword;
        ClockInResponse wrongPassword = await Handle();

        AttendanceTestHarness second = new();
        second.Totp.IsValid = false;
        ClockInResponse wrongCode = await Handle(second);

        Assert.Equal(AttendanceResultCode.InvalidCredentials, wrongPassword.ResultCode);
        Assert.Equal(AttendanceResultCode.InvalidCredentials, wrongCode.ResultCode);
    }

    [Fact]
    public async Task KeepsTheRealReasonInTheSecurityTrail()
    {
        // Collapsed for the client, precise for the investigator.
        _harness.Totp.IsValid = false;

        await Handle();

        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "INVALID_OTP");
    }

    [Fact]
    public async Task CountsAFailedAuthenticatorCodeTowardsLockout()
    {
        _harness.Totp.IsValid = false;

        await Handle();

        Assert.Equal(1, _harness.Lockout.FailuresRegistered);
    }

    [Fact]
    public async Task ReportsLockoutWhenTheFailureTripsTheThreshold()
    {
        _harness.Credentials.Outcome = CredentialValidationOutcome.InvalidPassword;
        _harness.Lockout.LocksOnNextFailure = true;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.AccountLocked, response.ResultCode);
    }

    [Fact]
    public async Task ConsumesTheTimeStepBeforeWritingAttendance()
    {
        // The code is spent whether or not the attendance write then succeeds:
        // otherwise a refused clock-in would hand the code back for reuse.
        await Handle();

        int consumeIndex = _harness.Calls.IndexOf("mfa.consumeTimeStep");
        int writeIndex = _harness.Calls.IndexOf("attendance.clockIn");

        Assert.True(consumeIndex >= 0 && writeIndex >= 0);
        Assert.True(consumeIndex < writeIndex, "the authenticator step must be consumed before the transaction");
    }

    [Fact]
    public async Task RefusesAReplayedAuthenticatorCode()
    {
        _harness.Mfa.ConsumeResult = AttendanceResultCode.OtpReplayed;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.InvalidCredentials, response.ResultCode);
        Assert.DoesNotContain("attendance.clockIn", _harness.Calls);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "OtpReplayed");
    }

    [Fact]
    public async Task RefusesWhenNoAuthenticatorIsEnrolled()
    {
        _harness.Users.HasMfaEnrolment = false;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.MfaNotEnrolled, response.ResultCode);
    }

    [Fact]
    public async Task ReportsAnInternalErrorWhenTheSecretCannotBeDecrypted()
    {
        // A missing key ring is an operational failure. Telling the employee
        // their credentials are invalid would send them chasing a problem they
        // cannot fix.
        _harness.Secrets.CanUnprotect = false;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.InternalError, response.ResultCode);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "SECRET_UNREADABLE");
    }

    [Fact]
    public async Task ClearsTheFailureCounterOnlyAfterEveryFactorPasses()
    {
        await Handle();

        Assert.Equal(1, _harness.Lockout.ResetCount);

        AttendanceTestHarness failed = new();
        failed.Totp.IsValid = false;
        await Handle(failed);

        Assert.Equal(0, failed.Lockout.ResetCount);
    }

    [Fact]
    public async Task ReturnsTheStoredResultForARetryOfTheSameRequest()
    {
        _harness.Idempotency.Disposition = IdempotencyDisposition.ReplayStoredResult;
        _harness.Idempotency.StoredResultCode = (int)AttendanceResultCode.Success;

        ClockInResponse response = await Handle();

        Assert.True(response.WasReplayed);
        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.DoesNotContain("attendance.clockIn", _harness.Calls);
    }

    [Fact]
    public async Task RefusesWhileTheOriginalRequestIsStillRunning()
    {
        _harness.Idempotency.Disposition = IdempotencyDisposition.InProgress;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.IdempotentInProgress, response.ResultCode);
        Assert.DoesNotContain("attendance.clockIn", _harness.Calls);
    }

    [Fact]
    public async Task RefusesAnIdempotencyKeyReusedForADifferentRequest()
    {
        _harness.Idempotency.Disposition = IdempotencyDisposition.KeyReused;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.IdempotencyKeyReuse, response.ResultCode);
    }

    [Fact]
    public async Task RecordsTheBusinessOutcomeAgainstTheIdempotencyKey()
    {
        // A refusal is recorded too, so the retry is told the same thing rather
        // than being processed afresh.
        _harness.Attendance.ClockInResult = AttendanceResultCode.AlreadyClockedIn;

        ClockInResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.AlreadyClockedIn, response.ResultCode);
        Assert.Equal(1, _harness.Idempotency.CompletedCount);
        Assert.Equal((int)AttendanceResultCode.AlreadyClockedIn, _harness.Idempotency.CompletedWithResultCode);
    }

    [Fact]
    public async Task PassesTheMatchedOfficeAndMeasuredDistanceAsEvidence()
    {
        await Handle();

        Assert.NotNull(_harness.Attendance.LastEvidence);
        LocationEvidence evidence = _harness.Attendance.LastEvidence!.Value;

        Assert.Equal(AttendanceTestHarness.OfficeLocationId, evidence.OfficeLocationId);
        Assert.InRange(evidence.DistanceMeters, 0m, 5m);
        Assert.False(evidence.WasMockedLocation);

        // Raw coordinates are not persisted by default (assumption ASM-06).
        Assert.Null(evidence.CoordinatesProtected);
    }

    private Task<ClockInResponse> Handle(ReportedPosition? position = null) => Handle(_harness, position);

    private static Task<ClockInResponse> Handle(AttendanceTestHarness harness, ReportedPosition? position = null)
    {
        ClockInHandler handler = new(
            harness.Policy,
            harness.Authenticator,
            harness.Offices,
            harness.Idempotency,
            harness.Attendance,
            harness.SecurityEvents);

        ClockInCommand command = new(
            AttendanceTestHarness.UserId,
            AttendanceTestHarness.Password,
            AttendanceTestHarness.Code,
            AttendanceTestHarness.DeviceId,
            Guid.NewGuid(),
            position ?? AttendanceTestHarness.AcceptablePosition,
            Guid.NewGuid(),
            new byte[32],
            Guid.NewGuid(),
            SourceAddressHash: null);

        return handler.HandleAsync(command, TestContext.Current.CancellationToken);
    }
}
