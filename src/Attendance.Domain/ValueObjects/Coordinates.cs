namespace Attendance.Domain.ValueObjects;

/// <summary>
/// A validated WGS 84 geographic position.
/// </summary>
/// <remarks>
/// Latitude and longitude are validated on construction, so a
/// <see cref="Coordinates"/> value can never hold an impossible position. The
/// database enforces the same ranges (CK_OfficeLocation_Latitude and
/// CK_OfficeLocation_Longitude), because validation at the boundary is a
/// convenience and the constraint is the guarantee.
/// </remarks>
public readonly record struct Coordinates
{
    /// <summary>Minimum valid latitude, in degrees.</summary>
    public const double MinLatitude = -90d;

    /// <summary>Maximum valid latitude, in degrees.</summary>
    public const double MaxLatitude = 90d;

    /// <summary>Minimum valid longitude, in degrees.</summary>
    public const double MinLongitude = -180d;

    /// <summary>Maximum valid longitude, in degrees.</summary>
    public const double MaxLongitude = 180d;

    private Coordinates(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Latitude in degrees, between -90 and +90 inclusive.</summary>
    public double Latitude { get; }

    /// <summary>Longitude in degrees, between -180 and +180 inclusive.</summary>
    public double Longitude { get; }

    /// <summary>
    /// Creates a validated position, throwing when the values are out of range
    /// or not finite.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The latitude or longitude is outside its valid range, or is NaN or
    /// infinity.
    /// </exception>
    public static Coordinates Create(double latitude, double longitude)
    {
        if (!TryCreate(latitude, longitude, out Coordinates coordinates))
        {
            // Which value failed matters to the caller, so report it precisely.
            if (!double.IsFinite(latitude) || latitude is < MinLatitude or > MaxLatitude)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(latitude),
                    latitude,
                    $"Latitude must be a finite value between {MinLatitude} and {MaxLatitude} degrees.");
            }

            throw new ArgumentOutOfRangeException(
                nameof(longitude),
                longitude,
                $"Longitude must be a finite value between {MinLongitude} and {MaxLongitude} degrees.");
        }

        return coordinates;
    }

    /// <summary>
    /// Attempts to create a validated position.
    /// </summary>
    /// <remarks>
    /// Used on the request path, where an out-of-range coordinate from a mobile
    /// client is an expected input to reject with INVALID_REQUEST rather than an
    /// exceptional condition to throw on.
    /// </remarks>
    public static bool TryCreate(double latitude, double longitude, out Coordinates coordinates)
    {
        // NaN comparisons are always false, so test for finiteness explicitly
        // rather than relying on the range checks to catch it.
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            coordinates = default;
            return false;
        }

        if (latitude is < MinLatitude or > MaxLatitude ||
            longitude is < MinLongitude or > MaxLongitude)
        {
            coordinates = default;
            return false;
        }

        coordinates = new Coordinates(latitude, longitude);
        return true;
    }

    /// <summary>
    /// Returns the position in a form safe for diagnostics.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the real coordinates. Claude.md §33 forbids writing exact
    /// geolocation to logs without operational justification, and the easiest way
    /// for coordinates to reach a log file is an innocent string interpolation of
    /// an object that overrode ToString. Use the properties explicitly when a
    /// value genuinely has to be persisted or displayed.
    /// </remarks>
    public override string ToString() => "Coordinates(redacted)";
}
