using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Features.Devices;

/// <summary>
/// Tells a device where its own registration stands (Claude.md §18).
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint is deliberately reachable by a device that is not active.</b>
/// Almost every mobile endpoint requires an approved device; this one cannot,
/// because a device awaiting approval has to be able to ask whether it has been
/// approved yet. What is relaxed is the status requirement, not the
/// authentication — the request is still signed with the registered private key,
/// so only the real device can ask about itself.
/// </para>
/// <para>
/// A revoked device is told plainly that it is revoked, with the reason. There is
/// nothing to gain by being vague: the holder already knows which device they
/// have, and an employee whose handset stopped working deserves to know why
/// rather than meeting a generic failure at the start of a shift. The reason text
/// never mentions another employee or another device.
/// </para>
/// </remarks>
public sealed class DeviceStatusHandler
{
    private readonly IDeviceRepository _devices;

    /// <summary>Creates the handler.</summary>
    public DeviceStatusHandler(IDeviceRepository devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <summary>Returns the device's registration state.</summary>
    public async Task<DeviceStatusResponse> HandleAsync(
        DeviceStatusCommand command,
        CancellationToken cancellationToken)
    {
        DeviceStatusRecord? record = await _devices
            .GetStatusAsync(command.DevicePublicId, cancellationToken)
            .ConfigureAwait(false);

        if (record is not { } status || status.ResultCode != AttendanceResultCode.Success)
        {
            return DeviceStatusResponse.Failed(record?.ResultCode ?? AttendanceResultCode.DeviceNotRegistered);
        }

        return new DeviceStatusResponse(
            AttendanceResultCode.Success,
            status.Status,
            status.EmployeeActive,
            status.RegisteredUtc,
            status.ApprovedUtc,
            status.RevokedUtc,
            status.RevokedReason,
            status.ServerTimeUtc);
    }
}

/// <summary>A device asking about itself.</summary>
/// <param name="DevicePublicId">The device, taken from the verified signature.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
public readonly record struct DeviceStatusCommand(
    Guid DevicePublicId,
    Guid CorrelationId);

/// <summary>What a device is told about its own registration.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="Status">Pending approval, active or revoked.</param>
/// <param name="EmployeeActive">Whether the bound employee is still active.</param>
/// <param name="RegisteredUtc">When it was registered.</param>
/// <param name="ApprovedUtc">When it was approved, if it was.</param>
/// <param name="RevokedUtc">When it was revoked, if it was.</param>
/// <param name="RevokedReason">Why, for the holder of the device.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
public readonly record struct DeviceStatusResponse(
    AttendanceResultCode ResultCode,
    DeviceStatus? Status,
    bool EmployeeActive,
    DateTimeOffset? RegisteredUtc,
    DateTimeOffset? ApprovedUtc,
    DateTimeOffset? RevokedUtc,
    string? RevokedReason,
    DateTimeOffset? ServerTimeUtc)
{
    /// <summary>A refusal carrying only its code.</summary>
    public static DeviceStatusResponse Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null, false, null, null, null, null, null);
}
