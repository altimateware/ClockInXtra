using Attendance.Domain.ValueObjects;

namespace Attendance.Domain.Services;

/// <summary>
/// Geodesic distance between two positions on the Earth's surface.
/// </summary>
/// <remarks>
/// <para>
/// This is the single implementation of the calculation behind the proximity
/// rule in Claude.md §9. It lives in the domain so that the boundary cases
/// required by §51 (0, 1, 3, 4.9, 5.0, 5.1 and 10 metres) can be tested without
/// a database, a network or a device.
/// </para>
/// <para>
/// <b>What this does not prove.</b> The arithmetic here is exact to well under a
/// millimetre at these distances, but that says nothing about whether a phone's
/// reported position is correct. Consumer GNSS accuracy is frequently worse than
/// 5 metres, particularly indoors and beside buildings, so a correct distance
/// calculation on an inaccurate fix is still an inaccurate answer. See conflict
/// CON-01 in the requirements register.
/// </para>
/// </remarks>
public static class GeoDistance
{
    /// <summary>
    /// IUGG mean Earth radius in metres.
    /// </summary>
    /// <remarks>
    /// Measured against SQL Server's ellipsoidal WGS 84
    /// <c>geography::STDistance</c> at the reference office latitude (6.465°N),
    /// this model runs about 0.55% short north–south and 0.12% long east–west.
    /// At the 5 metre radius the system works with, that is roughly 3 cm — two
    /// orders of magnitude below the accuracy a phone reports, which is metres at
    /// best. An ellipsoidal formula such as Vincenty would therefore add
    /// complexity without changing a single decision. The measurements are
    /// recorded in docs/architecture/06-implementation-status.md.
    /// </remarks>
    public const double MeanEarthRadiusMeters = 6_371_008.8d;

    /// <summary>
    /// Returns the great-circle distance between two positions, in metres.
    /// </summary>
    public static double MetersBetween(Coordinates from, Coordinates to)
    {
        // Haversine. The asin form is used rather than the more familiar
        // acos(sin·sin + cos·cos·cos) because that version loses precision
        // catastrophically at short distances: its argument approaches 1, where
        // acos is numerically ill-conditioned. At the scale this system cares
        // about — a few metres — the acos form can be wrong by metres.
        double lat1 = DegreesToRadians(from.Latitude);
        double lat2 = DegreesToRadians(to.Latitude);
        double deltaLat = DegreesToRadians(to.Latitude - from.Latitude);
        double deltaLon = DegreesToRadians(to.Longitude - from.Longitude);

        double sinHalfDeltaLat = Math.Sin(deltaLat / 2d);
        double sinHalfDeltaLon = Math.Sin(deltaLon / 2d);

        double h = (sinHalfDeltaLat * sinHalfDeltaLat)
                 + (Math.Cos(lat1) * Math.Cos(lat2) * sinHalfDeltaLon * sinHalfDeltaLon);

        // Guard against a value fractionally above 1 from floating-point error,
        // which would make Sqrt/Asin produce NaN for two identical positions.
        h = Math.Clamp(h, 0d, 1d);

        return 2d * MeanEarthRadiusMeters * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>
    /// Absorbs floating-point representation error at the radius boundary.
    /// </summary>
    /// <remarks>
    /// One micrometre. This is not a relaxation of the rule — it is smaller than
    /// any physically meaningful distance, and roughly a millionth of the
    /// 5 metre radius. It exists because "exactly on the boundary" is not a
    /// decidable question in binary floating point: a point constructed to be
    /// exactly 5 m away computes as 5 m plus or minus a few ulps, so a bare
    /// <c>&lt;=</c> would make the boundary case depend on rounding rather than on
    /// the rule. Without it, the 5.0 m case required by Claude.md §51 has no
    /// stable answer.
    /// </remarks>
    public const double BoundaryToleranceMeters = 1e-6d;

    /// <summary>
    /// Indicates whether <paramref name="reported"/> lies within
    /// <paramref name="allowedRadiusMeters"/> of <paramref name="office"/>.
    /// </summary>
    /// <remarks>
    /// The comparison is inclusive: a position exactly on the boundary is inside
    /// it, because "within 5 metres" in the requirement reads as including
    /// 5 metres. See <see cref="BoundaryToleranceMeters"/> for why that needs a
    /// tolerance to be well defined at all.
    /// </remarks>
    public static bool IsWithin(Coordinates reported, Coordinates office, double allowedRadiusMeters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allowedRadiusMeters);

        return MetersBetween(reported, office) <= allowedRadiusMeters + BoundaryToleranceMeters;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);
}
