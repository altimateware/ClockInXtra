using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Office location administration (§19).
/// </summary>
/// <remarks>
/// Until at least one active office exists, every location check refuses and
/// nobody can clock in anywhere — the domain reports
/// <c>NoActiveOfficeLocations</c> rather than accepting a position it cannot
/// measure against.
/// </remarks>
public interface IOfficeLocationAdministrationRepository
{
    /// <summary>Lists offices with the accuracy actually observed at each.</summary>
    Task<IReadOnlyList<OfficeLocationDetail>> GetAllAsync(
        int statisticsDays,
        CancellationToken cancellationToken);

    /// <summary>Creates an office location.</summary>
    Task<AttendanceResultCode> CreateAsync(
        OfficeLocationInput input,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Updates an office location.</summary>
    Task<AttendanceResultCode> UpdateAsync(
        int officeLocationId,
        OfficeLocationInput input,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Enables or disables an office location.</summary>
    Task<AttendanceResultCode> SetStatusAsync(
        int officeLocationId,
        bool isActive,
        byte[] rowVersion,
        string? reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>The editable fields of an office location.</summary>
/// <param name="Name">A name people recognise.</param>
/// <param name="Description">Optional detail.</param>
/// <param name="Latitude">Degrees, -90 to 90.</param>
/// <param name="Longitude">Degrees, -180 to 180.</param>
/// <param name="AllowedRadiusMeters">
/// The proximity threshold. The default is 5 metres as specified, but see the
/// observed accuracy before trusting it: consumer GPS is frequently worse than
/// that, especially indoors and among tall buildings (CON-01).
/// </param>
/// <param name="IsActive">Whether attendance may be recorded here.</param>
public readonly record struct OfficeLocationInput(
    string Name,
    string? Description,
    decimal Latitude,
    decimal Longitude,
    decimal AllowedRadiusMeters,
    bool IsActive);

/// <summary>
/// An office location, with the accuracy employees' devices actually report
/// there.
/// </summary>
/// <param name="OfficeLocationId">Internal identifier.</param>
/// <param name="Name">The office name.</param>
/// <param name="Description">Optional detail.</param>
/// <param name="Latitude">Configured latitude.</param>
/// <param name="Longitude">Configured longitude.</param>
/// <param name="AllowedRadiusMeters">Configured proximity threshold.</param>
/// <param name="IsActive">Whether attendance may be recorded here.</param>
/// <param name="EventCount">Attendance events observed in the window.</param>
/// <param name="MedianAccuracyMeters">
/// The median accuracy devices reported here. <b>This is the number that says
/// whether the configured radius is realistic</b> — if the median accuracy is
/// worse than the radius, half of all honest attempts are being asked to do
/// something their hardware cannot.
/// </param>
/// <param name="WorstAccuracyMeters">The worst accuracy observed.</param>
/// <param name="MaxDistanceMeters">The greatest distance accepted.</param>
/// <param name="RowVersion">Concurrency token.</param>
public readonly record struct OfficeLocationDetail(
    int OfficeLocationId,
    string Name,
    string? Description,
    decimal Latitude,
    decimal Longitude,
    decimal AllowedRadiusMeters,
    bool IsActive,
    int EventCount,
    decimal? MedianAccuracyMeters,
    decimal? WorstAccuracyMeters,
    decimal? MaxDistanceMeters,
    byte[] RowVersion);
