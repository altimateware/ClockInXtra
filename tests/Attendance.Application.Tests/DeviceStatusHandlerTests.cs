using Attendance.Application.Abstractions;
using Attendance.Application.Features.Devices;
using Attendance.Domain.Enums;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="DeviceStatusHandler"/>.
/// </summary>
public sealed class DeviceStatusHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task ReportsAnApprovedDeviceAsActive()
    {
        DeviceStatusResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.Equal(DeviceStatus.Active, response.Status);
        Assert.True(response.EmployeeActive);
        Assert.NotNull(response.ApprovedUtc);
    }

    [Fact]
    public async Task ReportsADeviceStillAwaitingApproval()
    {
        // The app shows "waiting for approval" instead of failing every
        // attendance attempt with something the employee cannot act on.
        _harness.Devices.StatusRecord = AttendanceTestHarness.FakeDeviceRepository.ActiveStatus() with
        {
            Status = DeviceStatus.PendingApproval,
            ApprovedUtc = null,
        };

        DeviceStatusResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.Equal(DeviceStatus.PendingApproval, response.Status);
        Assert.Null(response.ApprovedUtc);
    }

    [Fact]
    public async Task TellsARevokedDeviceThatItWasRevokedAndWhy()
    {
        // The holder already knows which device they have; being vague would only
        // leave an employee guessing why their app stopped working.
        _harness.Devices.StatusRecord =
            AttendanceTestHarness.FakeDeviceRepository.RevokedStatus("Replaced by a newly approved device");

        DeviceStatusResponse response = await Handle();

        Assert.Equal(DeviceStatus.Revoked, response.Status);
        Assert.NotNull(response.RevokedUtc);
        Assert.Equal("Replaced by a newly approved device", response.RevokedReason);
    }

    [Fact]
    public async Task ReportsNotRegisteredWhenNoDeviceMatches()
    {
        _harness.Devices.StatusRecord = null;

        DeviceStatusResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.DeviceNotRegistered, response.ResultCode);
        Assert.Null(response.Status);
    }

    private Task<DeviceStatusResponse> Handle()
    {
        DeviceStatusHandler handler = new(_harness.Devices);

        return handler.HandleAsync(
            new DeviceStatusCommand(Guid.NewGuid(), Guid.NewGuid()),
            TestContext.Current.CancellationToken);
    }
}
