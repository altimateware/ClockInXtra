using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Domain.Tests;

/// <summary>
/// Validation tests for <see cref="Coordinates"/>.
/// </summary>
/// <remarks>
/// Coordinates arrive from an untrusted mobile client, so every one of these
/// inputs is something a real request could carry — including the values that
/// come from a device with no fix at all.
/// </remarks>
public sealed class CoordinatesTests
{
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(6.465422d, 3.406448d)]
    [InlineData(90d, 180d)]       // the corners are valid
    [InlineData(-90d, -180d)]
    public void TryCreate_AcceptsPositionsInRange(double latitude, double longitude)
    {
        Assert.True(Coordinates.TryCreate(latitude, longitude, out Coordinates coordinates));
        Assert.Equal(latitude, coordinates.Latitude);
        Assert.Equal(longitude, coordinates.Longitude);
    }

    [Theory]
    [InlineData(90.000001d, 0d)]
    [InlineData(-90.000001d, 0d)]
    [InlineData(0d, 180.000001d)]
    [InlineData(0d, -180.000001d)]
    public void TryCreate_RejectsPositionsOutOfRange(double latitude, double longitude)
    {
        Assert.False(Coordinates.TryCreate(latitude, longitude, out _));
    }

    [Theory]
    [InlineData(double.NaN, 0d)]
    [InlineData(0d, double.NaN)]
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(0d, double.NegativeInfinity)]
    public void TryCreate_RejectsValuesThatAreNotFinite(double latitude, double longitude)
    {
        // NaN is the realistic case: a device with no position fix can serialise
        // one, and every comparison against NaN is false, so a range check alone
        // would let it through and it would poison the distance calculation.
        Assert.False(Coordinates.TryCreate(latitude, longitude, out _));
    }

    [Fact]
    public void Create_NamesTheOffendingParameter()
    {
        ArgumentOutOfRangeException latitudeError =
            Assert.Throws<ArgumentOutOfRangeException>(() => Coordinates.Create(91d, 0d));
        Assert.Equal("latitude", latitudeError.ParamName);

        ArgumentOutOfRangeException longitudeError =
            Assert.Throws<ArgumentOutOfRangeException>(() => Coordinates.Create(0d, 181d));
        Assert.Equal("longitude", longitudeError.ParamName);
    }

    [Fact]
    public void ToString_DoesNotRevealThePosition()
    {
        // An employee's position is sensitive data (Claude.md §26, §33), and the
        // easiest way for it to reach a log file is interpolating the object into
        // a message. ToString must not make that mistake possible.
        Coordinates coordinates = Coordinates.Create(6.465422d, 3.406448d);

        string text = coordinates.ToString();

        Assert.DoesNotContain("6.465422", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3.406448", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualPositionsAreEqualValues()
    {
        Coordinates first = Coordinates.Create(6.465422d, 3.406448d);
        Coordinates second = Coordinates.Create(6.465422d, 3.406448d);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
