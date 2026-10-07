using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;
using Xunit;

namespace Attendance.Domain.Tests;

/// <summary>
/// Tests for the location acceptance rules.
/// </summary>
/// <remarks>
/// These encode decisions that are still OPEN REQUIREMENTS (OPEN-25 accuracy
/// handling, OPEN-26 spoofing policy). They assert the *interim* behaviour
/// recorded in the requirements register, so that if the business decides
/// differently the tests fail loudly and the change is deliberate rather than
/// accidental.
/// </remarks>
public sealed class LocationPolicyTests
{
    private const double OfficeLatitude = 6.465422d;
    private const double OfficeLongitude = 3.406448d;
    private const double MetersPerDegreeLatitude = GeoDistance.MeanEarthRadiusMeters * Math.PI / 180d;
    private const int OfficeId = 42;

    private static Coordinates OfficePosition => Coordinates.Create(OfficeLatitude, OfficeLongitude);

    private static Coordinates MetersNorthOfOffice(double meters) =>
        Coordinates.Create(OfficeLatitude + (meters / MetersPerDegreeLatitude), OfficeLongitude);

    private static IReadOnlyList<OfficeLocationCandidate> SingleOffice(double radiusMeters = 5d) =>
        [new OfficeLocationCandidate(OfficeId, OfficePosition, radiusMeters)];

    private static ReportedPosition PositionAt(
        double metersFromOffice,
        double? accuracyMeters = 3d,
        bool isMocked = false,
        bool isSimulated = false) =>
        new(MetersNorthOfOffice(metersFromOffice),
            accuracyMeters,
            DevicePlatform.Android,
            isMocked,
            isSimulated);

    [Fact]
    public void Accepts_APositionInsideTheRadiusWithGoodAccuracy()
    {
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 3d, accuracyMeters: 4d),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.True(decision.IsAccepted);
        Assert.Equal(OfficeId, decision.OfficeLocationId);
        Assert.Equal(LocationRejectionReason.None, decision.Reason);
        Assert.NotNull(decision.DistanceMeters);
        Assert.Equal(3d, decision.DistanceMeters!.Value, 0.001d);
    }

    [Fact]
    public void RejectsAMockedPosition_EvenStandingExactlyAtTheOffice()
    {
        // The order matters: a reading the platform itself calls artificial is
        // refused before its distance is considered at all. Measuring the
        // distance of a fabricated position would be theatre.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 0d, accuracyMeters: 1d, isMocked: true),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Equal(LocationRejectionReason.SourceUntrusted, decision.Reason);
        Assert.Null(decision.OfficeLocationId);
    }

    [Fact]
    public void RejectsASoftwareSimulatedPosition()
    {
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 1d, accuracyMeters: 1d, isSimulated: true),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.Equal(LocationRejectionReason.SourceUntrusted, decision.Reason);
    }

    [Fact]
    public void HonoursTheSettingThatDisablesMockRejection()
    {
        // OPEN-26 is a business decision. If it is switched off, a mocked
        // position is evaluated on its distance like any other — the policy must
        // not quietly keep enforcing its own preference.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 1d, accuracyMeters: 2d, isMocked: true),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: false);

        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void ReportsWhenNoOfficeLocationsAreConfigured()
    {
        // Distinct from "you are in the wrong place": nobody can clock in
        // anywhere, which is an administrative problem, not the employee's.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(0d),
            [],
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Equal(LocationRejectionReason.NoActiveOfficeLocations, decision.Reason);
    }

    [Fact]
    public void RejectsAPositionOutsideEveryRadius()
    {
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 25d, accuracyMeters: 3d),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Equal(LocationRejectionReason.OutsideAllOfficeLocations, decision.Reason);

        // The distance is still reported, because it is written to the
        // attendance evidence even for a refusal. It is never returned to the
        // mobile client.
        Assert.NotNull(decision.DistanceMeters);
    }

    [Fact]
    public void ChoosesTheNearestOfficeAmongSeveral()
    {
        Coordinates farOffice = Coordinates.Create(OfficeLatitude + 0.5d, OfficeLongitude);

        IReadOnlyList<OfficeLocationCandidate> offices =
        [
            new OfficeLocationCandidate(99, farOffice, 5d),
            new OfficeLocationCandidate(OfficeId, OfficePosition, 5d),
        ];

        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 2d, accuracyMeters: 3d),
            offices,
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.True(decision.IsAccepted);
        Assert.Equal(OfficeId, decision.OfficeLocationId);
    }

    [Fact]
    public void RejectsWhenReportedAccuracyIsWorseThanTheRadius()
    {
        // Standing 2 m away, but the device says it could be 30 m out. The fix
        // cannot support a claim about a 5 m radius, so it is refused as
        // insufficient rather than accepted on a number that means little.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 2d, accuracyMeters: 30d),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Equal(LocationRejectionReason.AccuracyInsufficient, decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void TreatsMissingOrNonsensicalAccuracyAsInsufficient(double? accuracy)
    {
        // "Unknown" must not be read as "perfect". Apple documents a negative
        // horizontalAccuracy as meaning the coordinates are invalid, and no
        // honest device claims a zero-metre fix.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 1d, accuracyMeters: accuracy),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Equal(LocationRejectionReason.AccuracyInsufficient, decision.Reason);
    }

    [Fact]
    public void IgnoresAccuracyEntirelyUnderTheDistanceOnlyPolicy()
    {
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 2d, accuracyMeters: null),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceOnly,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void PrefersOutsideOverInsufficient_WhenEvenTheUncertaintyCannotReach()
    {
        // 200 m away with a 30 m uncertainty: no charitable reading of that fix
        // places the device at the office. Saying "improve your accuracy" would
        // be misleading, so the distance verdict wins.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 200d, accuracyMeters: 30d),
            SingleOffice(),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.Equal(LocationRejectionReason.OutsideAllOfficeLocations, decision.Reason);
    }

    [Fact]
    public void NeverWidensTheRadiusToCompensateForPoorAccuracy()
    {
        // Claude.md §26 is explicit: poor accuracy must not become a licence to
        // accept a position further away. 8 m from a 5 m office with an 8 m
        // uncertainty is refused, not waved through on the grounds that the
        // employee "might" be inside.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 8d, accuracyMeters: 8d),
            SingleOffice(radiusMeters: 5d),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.False(decision.IsAccepted);
        Assert.Null(decision.OfficeLocationId);
    }

    [Fact]
    public void UsesTheConfiguredAccuracyThresholdWhenOneIsSet()
    {
        // With an explicit 20 m threshold the same reading that fails under the
        // default (the office radius) is accepted. This is the knob OPEN-25
        // gives the business, and it is the only thing that changes behaviour.
        ReportedPosition position = PositionAt(metersFromOffice: 2d, accuracyMeters: 15d);

        LocationDecision strict = LocationPolicy.Evaluate(
            position, SingleOffice(), LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null, rejectUntrustedSource: true);

        LocationDecision relaxed = LocationPolicy.Evaluate(
            position, SingleOffice(), LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: 20d, rejectUntrustedSource: true);

        Assert.False(strict.IsAccepted);
        Assert.True(relaxed.IsAccepted);
    }

    [Fact]
    public void AcceptsExactlyAtTheBoundary()
    {
        // The 5.0 m case required by §51, at the policy level rather than the
        // arithmetic level.
        LocationDecision decision = LocationPolicy.Evaluate(
            PositionAt(metersFromOffice: 5d, accuracyMeters: 5d),
            SingleOffice(radiusMeters: 5d),
            LocationAccuracyPolicy.DistanceAndAccuracyThreshold,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true);

        Assert.True(decision.IsAccepted);
    }

    [Fact]
    public void RejectsANullOfficeCollection()
    {
        Assert.Throws<ArgumentNullException>(() => LocationPolicy.Evaluate(
            PositionAt(0d),
            null!,
            LocationAccuracyPolicy.DistanceOnly,
            maxAcceptedAccuracyMeters: null,
            rejectUntrustedSource: true));
    }
}
