using Attendance.Application.Abstractions;
using Attendance.Application.Features.Locations;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="ValidateLocationHandler"/>.
/// </summary>
/// <remarks>
/// This is the one mobile endpoint an unauthenticated caller can reach, so the
/// tests that matter most are about what it declines to say.
/// </remarks>
public sealed class ValidateLocationHandlerTests
{
    private readonly AttendanceTestHarness _harness = new();

    [Fact]
    public async Task AcceptsAPositionInsideTheRadius()
    {
        ValidateLocationResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.Success, response.ResultCode);
        Assert.True(response.IsValid);
    }

    [Fact]
    public async Task WithholdsTheOfficeIdentifierFromAnUnauthenticatedCaller()
    {
        // Otherwise the endpoint is a search oracle: submit coordinates, watch the
        // answer, and walk the boundary until every office is located to within a
        // few metres.
        ValidateLocationResponse response = await Handle(isAuthenticated: false);

        Assert.True(response.IsValid);
        Assert.Null(response.OfficeLocationId);
    }

    [Fact]
    public async Task ReturnsTheOfficeIdentifierToARegisteredDevice()
    {
        ValidateLocationResponse response = await Handle(isAuthenticated: true);

        Assert.True(response.IsValid);
        Assert.Equal(AttendanceTestHarness.OfficeLocationId, response.OfficeLocationId);
    }

    [Fact]
    public async Task RefusesAPositionOutsideEveryOffice()
    {
        ValidateLocationResponse response = await Handle(position: AttendanceTestHarness.DistantPosition);

        Assert.Equal(AttendanceResultCode.LocationNotAllowed, response.ResultCode);
        Assert.False(response.IsValid);
        Assert.Null(response.OfficeLocationId);
    }

    [Fact]
    public async Task TreatsAMockedPositionAsMoreSeriousThanBeingInTheWrongPlace()
    {
        // Being outside the radius is ordinary. A mocked position is somebody
        // trying, and the trail should say so.
        ReportedPosition mocked = new(
            AttendanceTestHarness.AcceptablePosition.Coordinates,
            accuracyMeters: 3d,
            DevicePlatform.Android,
            isMocked: true);

        ValidateLocationResponse response = await Handle(position: mocked);

        Assert.Equal(AttendanceResultCode.LocationSourceUntrusted, response.ResultCode);

        SecurityEvent recorded = Assert.Single(_harness.SecurityEvents.Events);
        Assert.Equal(SecurityEventSeverity.Critical, recorded.Severity);
        Assert.Equal("LOCATION_SOURCE_UNTRUSTED", recorded.ReasonCode);
    }

    [Fact]
    public async Task RecordsAnUnattributedSubjectWhenTheCallerHasNoDeviceYet()
    {
        await Handle(position: AttendanceTestHarness.DistantPosition, devicePublicId: null);

        SecurityEvent recorded = Assert.Single(_harness.SecurityEvents.Events);
        Assert.Equal(SecurityEventSubject.Unknown, recorded.Subject);
        Assert.Null(recorded.SubjectKey);
    }

    [Fact]
    public async Task AttributesTheEventToTheDeviceWhenThereIsOne()
    {
        Guid device = Guid.NewGuid();

        await Handle(position: AttendanceTestHarness.DistantPosition, devicePublicId: device);

        SecurityEvent recorded = Assert.Single(_harness.SecurityEvents.Events);
        Assert.Equal(SecurityEventSubject.Device, recorded.Subject);
        Assert.Equal(device, recorded.DevicePublicId);
    }

    [Fact]
    public async Task RefusesWhenNoOfficeLocationsAreConfigured()
    {
        _harness.Offices.Offices = [];

        ValidateLocationResponse response = await Handle();

        Assert.Equal(AttendanceResultCode.LocationNotAllowed, response.ResultCode);
        Assert.Contains(_harness.SecurityEvents.Events, e => e.ReasonCode == "NO_ACTIVE_OFFICE_LOCATIONS");
    }

    private Task<ValidateLocationResponse> Handle(
        ReportedPosition? position = null,
        bool isAuthenticated = true,
        Guid? devicePublicId = null)
    {
        ValidateLocationHandler handler = new(_harness.Policy, _harness.Offices, _harness.SecurityEvents);

        ValidateLocationCommand command = new(
            position ?? AttendanceTestHarness.AcceptablePosition,
            devicePublicId,
            isAuthenticated,
            "1.0.0",
            Guid.NewGuid(),
            SourceAddressHash: null);

        return handler.HandleAsync(command, TestContext.Current.CancellationToken);
    }
}
