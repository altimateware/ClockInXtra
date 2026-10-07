namespace Attendance.Admin.Models;

/// <summary>
/// The latitude, longitude and radius inputs, shared by the create and edit
/// forms of the office location page.
/// </summary>
/// <param name="IdPrefix">Prefix that keeps element ids unique on a page with many offices.</param>
/// <param name="Latitude">Current latitude, invariant ("6.465422"), or null for a new office.</param>
/// <param name="Longitude">Current longitude, invariant, or null for a new office.</param>
/// <param name="RadiusMeters">Current radius in metres, invariant.</param>
public sealed record CoordinateInputs(
    string IdPrefix,
    string? Latitude,
    string? Longitude,
    string RadiusMeters);
