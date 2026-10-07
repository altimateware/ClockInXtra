using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Domain.Tests;

/// <summary>
/// Boundary tests for the proximity calculation behind the 5 metre rule.
/// </summary>
/// <remarks>
/// <para>
/// Claude.md §51 requires the boundary to be tested at 0, 1, 3, 4.9, 5.0, 5.1
/// and 10 metres. Test points are produced by projecting a known distance from a
/// reference position, rather than by hard-coding coordinates, so the tests state
/// the intent ("a point 4.9 m north") instead of a magic number nobody can check.
/// </para>
/// <para>
/// <b>These tests verify arithmetic, not GPS.</b> They say nothing about whether a
/// phone can report its position to 5 metres — frequently it cannot, which is
/// conflict CON-01 and an operational limitation the business must accept, not a
/// defect these tests could catch.
/// </para>
/// </remarks>
public sealed class GeoDistanceTests
{
    // A position in Lagos, matching the office used in the database smoke tests.
    private const double ReferenceLatitude = 6.465422d;
    private const double ReferenceLongitude = 3.406448d;

    // Metres per degree of latitude on the sphere the calculation models.
    private const double MetersPerDegreeLatitude = GeoDistance.MeanEarthRadiusMeters * Math.PI / 180d;

    // One millimetre. The calculation should be far better than this; the
    // tolerance exists so the test does not depend on the last bits of a double.
    private const double ToleranceMeters = 0.001d;

    private static Coordinates Reference =>
        Coordinates.Create(ReferenceLatitude, ReferenceLongitude);

    private static Coordinates NorthOfReference(double meters) =>
        Coordinates.Create(ReferenceLatitude + (meters / MetersPerDegreeLatitude), ReferenceLongitude);

    private static Coordinates EastOfReference(double meters)
    {
        // A degree of longitude shortens by cos(latitude); ignoring that is the
        // classic bug this case exists to catch.
        double metersPerDegreeLongitude =
            MetersPerDegreeLatitude * Math.Cos(ReferenceLatitude * Math.PI / 180d);

        return Coordinates.Create(ReferenceLatitude, ReferenceLongitude + (meters / metersPerDegreeLongitude));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(3d)]
    [InlineData(4.9d)]
    [InlineData(5.0d)]
    [InlineData(5.1d)]
    [InlineData(10d)]
    public void MetersBetween_MeasuresProjectedNorthwardDistance(double meters)
    {
        double actual = GeoDistance.MetersBetween(Reference, NorthOfReference(meters));

        Assert.Equal(meters, actual, ToleranceMeters);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(3d)]
    [InlineData(4.9d)]
    [InlineData(5.0d)]
    [InlineData(5.1d)]
    [InlineData(10d)]
    public void MetersBetween_MeasuresProjectedEastwardDistance(double meters)
    {
        double actual = GeoDistance.MetersBetween(Reference, EastOfReference(meters));

        Assert.Equal(meters, actual, ToleranceMeters);
    }

    [Fact]
    public void MetersBetween_IsZeroForTheSamePosition()
    {
        // Guards the clamp in the implementation: without it, floating-point
        // error can push the haversine term fractionally above 1 and produce NaN
        // for two identical positions — which would make an employee standing
        // exactly on the office pin fail every comparison.
        double actual = GeoDistance.MetersBetween(Reference, Reference);

        Assert.Equal(0d, actual);
        Assert.False(double.IsNaN(actual));
    }

    [Fact]
    public void MetersBetween_IsSymmetric()
    {
        Coordinates other = NorthOfReference(5d);

        Assert.Equal(
            GeoDistance.MetersBetween(Reference, other),
            GeoDistance.MetersBetween(other, Reference),
            ToleranceMeters);
    }

    [Theory]
    [InlineData(0d, true)]
    [InlineData(1d, true)]
    [InlineData(3d, true)]
    [InlineData(4.9d, true)]
    [InlineData(5.0d, true)]   // inclusive: "within 5 metres" includes 5 metres
    [InlineData(5.1d, false)]
    [InlineData(10d, false)]
    public void IsWithin_AppliesTheFiveMetreBoundaryInclusively(double meters, bool expected)
    {
        bool actual = GeoDistance.IsWithin(NorthOfReference(meters), Reference, allowedRadiusMeters: 5d);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsWithin_RejectsANonPositiveRadius()
    {
        // A zero or negative radius is a configuration error, not a location that
        // nobody can reach. The database enforces the same rule
        // (CK_OfficeLocation_Radius), so this can never arrive from stored data.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GeoDistance.IsWithin(Reference, Reference, allowedRadiusMeters: 0d));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => GeoDistance.IsWithin(Reference, Reference, allowedRadiusMeters: -1d));
    }

    [Fact]
    public void MetersBetween_AgreesWithAKnownLongDistance()
    {
        // A long-distance case catches a formula that happens to work only for
        // small offsets — for example one treating degrees as a flat plane, which
        // is accurate enough over metres and badly wrong over hundreds of
        // kilometres.
        //
        // Expected value derived independently of the implementation: these two
        // points differ by 2.6068° of latitude (≈ 290 km) and 4.0849° of
        // longitude, which at the mean latitude of 7.77° is ≈ 450 km of easting.
        // The hypotenuse is ≈ 536 km. A one percent band confirms the model
        // without encoding any particular implementation's rounding.
        Coordinates lagos = Coordinates.Create(6.465422d, 3.406448d);
        Coordinates abuja = Coordinates.Create(9.072264d, 7.491302d);

        double actual = GeoDistance.MetersBetween(lagos, abuja);

        Assert.InRange(actual, 535_000d * 0.99d, 535_000d * 1.01d);
    }

    [Fact]
    public void MetersBetween_HandlesTheAntimeridian()
    {
        // Two points a degree apart either side of 180°. A naive implementation
        // that subtracts longitudes without care computes a 359 degree gap and
        // reports most of the way round the planet.
        Coordinates west = Coordinates.Create(0d, 179.5d);
        Coordinates east = Coordinates.Create(0d, -179.5d);

        double actual = GeoDistance.MetersBetween(west, east);

        // One degree of longitude at the equator is about 111.2 km.
        Assert.InRange(actual, 111_000d, 111_400d);
    }
}
