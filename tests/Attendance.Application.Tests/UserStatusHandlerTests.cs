using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Domain.Enums;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="UserStatusHandler"/>.
/// </summary>
public sealed class UserStatusHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task ReturnsTheServerStateAndTheServerClock()
    {
        UserStatusResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.Equal(AttendanceState.NotClockedIn, response.State);

        // The app renders from the server's clock, never its own (§31).
        Assert.NotEqual(default, response.ServerTimeUtc);
    }

    [Fact]
    public async Task ReadsTheStatusBeforeTouchingTheDevice()
    {
        // Last-seen bookkeeping must never come between the employee and the
        // answer they asked for.
        await Handle();

        int statusIndex = _harness.Calls.IndexOf("devices.touchLastSeen");
        Assert.True(statusIndex >= 0);
        Assert.Equal(_harness.Calls.Count - 1, statusIndex);
    }

    [Fact]
    public async Task RefreshesTheDeviceMetadataItWasGiven()
    {
        await Handle();

        Assert.Equal(AttendanceTestHarness.DeviceId, _harness.Devices.TouchedDeviceId);
        Assert.Equal("1.2.3", _harness.Devices.TouchedAppVersion);
    }

    private Task<UserStatusResponse> Handle()
    {
        UserStatusHandler handler = new(_harness.Attendance, _harness.Devices);

        UserStatusCommand command = new(
            AttendanceTestHarness.MobileUserId,
            AttendanceTestHarness.DeviceId,
            "1.2.3",
            "Android 15",
            Guid.NewGuid());

        return handler.HandleAsync(command, TestContext.Current.CancellationToken);
    }
}
