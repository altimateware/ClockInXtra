using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="ClockOutHandler"/>.
/// </summary>
public sealed class ClockOutHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task Succeeds_AndReturnsTheDurationTheDatabaseComputed()
    {
        ClockOutResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.NotNull(response.ClockOutUtc);
        Assert.NotNull(response.DurationMinutes);
    }

    [Fact]
    public async Task RequiresNoPasswordOrAuthenticatorCode()
    {
        // By the stated requirement (§14), clock-out needs neither. The
        // consequence — anyone holding the unlocked phone can close the record —
        // is recorded as residual risk RR-06 rather than quietly mitigated here.
        await Handle();

        Assert.DoesNotContain("credentials.validate", _harness.Calls);
        Assert.DoesNotContain("totp.verify", _harness.Calls);
        Assert.DoesNotContain("lockout.check", _harness.Calls);
    }

    [Fact]
    public async Task RefusesADistantPosition()
    {
        ClockOutResponse response = await Handle(AttendanceTestHarness.DistantPosition);

        Assert.Equal(AttendanceResultCode.LocationNotAllowed, response.ResultCode);
        Assert.DoesNotContain("attendance.clockOut", _harness.Calls);
    }

    [Fact]
    public async Task RefusesAPositionWithInsufficientAccuracy()
    {
        ReportedPosition imprecise = new(
            AttendanceTestHarness.AcceptablePosition.Coordinates,
            accuracyMeters: 40d,
            DevicePlatform.Ios);

        ClockOutResponse response = await Handle(imprecise);

        Assert.Equal(AttendanceResultCode.LocationAccuracyInsufficient, response.ResultCode);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "LOCATION_ACCURACY_INSUFFICIENT");
    }

    [Fact]
    public async Task ReportsNotClockedInWhenThereIsNoOpenRecord()
    {
        _harness.Attendance.ClockOutResult = AttendanceResultCode.NotClockedIn;

        ClockOutResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.NotClockedIn, response.ResultCode);
        Assert.Null(response.ClockOutUtc);
    }

    [Fact]
    public async Task ReturnsTheStoredResultForARetry()
    {
        _harness.Idempotency.Disposition = IdempotencyDisposition.ReplayStoredResult;
        _harness.Idempotency.StoredResultCode = (int)AttendanceResultCode.Success;

        ClockOutResponse response = await Handle();

        Assert.True(response.WasReplayed);
        Assert.DoesNotContain("attendance.clockOut", _harness.Calls);
    }

    [Fact]
    public async Task RefusesWhileTheOriginalRequestIsStillRunning()
    {
        _harness.Idempotency.Disposition = IdempotencyDisposition.InProgress;

        ClockOutResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.IdempotentInProgress, response.ResultCode);
    }

    [Fact]
    public async Task RecordsTheOutcomeAgainstTheIdempotencyKey()
    {
        _harness.Attendance.ClockOutResult = AttendanceResultCode.AlreadyClockedOut;

        await Handle();

        Assert.Equal(1, _harness.Idempotency.CompletedCount);
        Assert.Equal((int)AttendanceResultCode.AlreadyClockedOut, _harness.Idempotency.CompletedWithResultCode);
    }

    private Task<ClockOutResponse> Handle(ReportedPosition? position = null)
    {
        ClockOutHandler handler = new(
            _harness.Policy,
            _harness.Offices,
            _harness.Idempotency,
            _harness.Attendance,
            _harness.SecurityEvents);

        ClockOutCommand command = new(
            AttendanceTestHarness.MobileUserId,
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
