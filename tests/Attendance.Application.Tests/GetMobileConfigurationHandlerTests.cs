using Attendance.Application.Abstractions;
using Attendance.Application.Features.Configuration;
using Attendance.Domain.Enums;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="GetMobileConfigurationHandler"/>.
/// </summary>
public sealed class GetMobileConfigurationHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task ReturnsTheAllowListedSettingsUnaltered()
    {
        // The database allow-list is the single decision about what an untrusted
        // device may know. This handler must not add a second one.
        MobileConfigurationResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.Equal(_harness.Configuration.Settings.Count, response.Settings.Count);
        Assert.Contains(response.Settings, s => s.Key == "Location.RejectMockedLocations");
    }

    [Fact]
    public async Task CarriesThroughASettingTheBusinessHasNotDecidedYet()
    {
        // An unset value is reported as unset rather than defaulted, which is
        // what stops the app inventing a business rule (§15, §68).
        MobileConfigurationResponse response = await Handle();

        MobileSetting unset = Assert.Single(response.Settings, s => s.Key == "Attendance.ClockInCloseTime");
        Assert.Null(unset.Value);
    }

    [Fact]
    public async Task TellsTheAppWhenAttendanceCannotOperate()
    {
        _harness.Configuration.ClockInConfigured = false;

        MobileConfigurationResponse response = await Handle();

        Assert.False(response.ClockInConfigured);
        Assert.True(response.ClockOutConfigured);
    }

    private Task<MobileConfigurationResponse> Handle() =>
        new GetMobileConfigurationHandler(_harness.Configuration)
            .HandleAsync(TestContext.Current.CancellationToken);
}
