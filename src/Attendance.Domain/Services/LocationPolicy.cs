using Attendance.Domain.ValueObjects;

namespace Attendance.Domain.Services;

/// <summary>
/// Decides whether a reported position is acceptable for attendance.
/// </summary>
/// <remarks>
/// <para>
/// This implements the proximity control in Claude.md §9 together with the
/// accuracy handling in §26. It is deliberately a pure function of its inputs:
/// no clock, no database, no configuration lookup, so every rule it applies can
/// be tested exhaustively.
/// </para>
/// <para>
/// <b>The configured radius is never widened.</b> §26 is explicit that poor
/// accuracy must not become a licence to accept a position further away. A bad
/// reading is refused, not accommodated.
/// </para>
/// </remarks>
public static class LocationPolicy
{
    /// <summary>
    /// Evaluates a reported position against the approved office locations.
    /// </summary>
    /// <param name="position">The position reported by the device.</param>
    /// <param name="offices">Active office locations. May be empty.</param>
    /// <param name="accuracyPolicy">
    /// How the reported accuracy affects the decision (setting
    /// <c>Location.AccuracyPolicy</c>, OPEN-25).
    /// </param>
    /// <param name="maxAcceptedAccuracyMeters">
    /// The worst accuracy still accepted (setting
    /// <c>Location.MaxAcceptedAccuracyMeters</c>). When <c>null</c>, the matched
    /// office's own radius is used, which is the strict interim rule.
    /// </param>
    /// <param name="rejectUntrustedSource">
    /// Whether to refuse positions the platform flagged as mocked or simulated
    /// (setting <c>Location.RejectMockedLocations</c>, OPEN-26).
    /// </param>
    public static LocationDecision Evaluate(
        ReportedPosition position,
        IReadOnlyList<OfficeLocationCandidate> offices,
        LocationAccuracyPolicy accuracyPolicy,
        double? maxAcceptedAccuracyMeters,
        bool rejectUntrustedSource)
    {
        ArgumentNullException.ThrowIfNull(offices);

        // 1. A position the platform itself says is artificial is refused first.
        //    Measuring the distance of a fake reading would be theatre.
        if (rejectUntrustedSource && position.IsSourceUntrusted)
        {
            return LocationDecision.Rejected(LocationRejectionReason.SourceUntrusted);
        }

        if (offices.Count == 0)
        {
            // Not the employee's fault, and a different operational problem from
            // being in the wrong place: nobody can clock in anywhere.
            return LocationDecision.Rejected(LocationRejectionReason.NoActiveOfficeLocations);
        }

        // 2. Find the closest office. The nearest one is the only candidate that
        //    matters: if the position is outside the nearest office's radius it
        //    is outside every other one too.
        OfficeLocationCandidate nearest = offices[0];
        double nearestDistance = GeoDistance.MetersBetween(position.Coordinates, offices[0].Coordinates);

        for (int i = 1; i < offices.Count; i++)
        {
            double distance = GeoDistance.MetersBetween(position.Coordinates, offices[i].Coordinates);
            if (distance < nearestDistance)
            {
                nearest = offices[i];
                nearestDistance = distance;
            }
        }

        bool withinRadius = nearestDistance <= nearest.AllowedRadiusMeters + GeoDistance.BoundaryToleranceMeters;

        if (accuracyPolicy == LocationAccuracyPolicy.DistanceOnly)
        {
            return withinRadius
                ? LocationDecision.Accepted(nearest.OfficeLocationId, nearestDistance)
                : LocationDecision.Rejected(LocationRejectionReason.OutsideAllOfficeLocations, nearestDistance);
        }

        // 3. Accuracy threshold. Defaulting to the office's own radius is the
        //    strict interim reading of OPEN-25: a reading whose uncertainty
        //    exceeds the whole radius cannot support a claim about that radius.
        double threshold = maxAcceptedAccuracyMeters ?? nearest.AllowedRadiusMeters;

        if (!position.HasUsableAccuracy || position.AccuracyMeters!.Value > threshold)
        {
            // Ordering matters here, and it is a judgement worth stating.
            //
            // When the reading is too imprecise to trust, the honest answer is
            // usually "we cannot tell" rather than "you are not there" — so
            // accuracy is reported in preference to distance. But if the device
            // is further away than the radius plus its own uncertainty, then
            // even the most charitable reading of that fix puts it outside the
            // office, and saying "improve your accuracy" would be misleading.
            double uncertainty = position.HasUsableAccuracy ? position.AccuracyMeters!.Value : 0d;
            bool outsideEvenAllowingForError =
                nearestDistance > nearest.AllowedRadiusMeters + uncertainty;

            return outsideEvenAllowingForError
                ? LocationDecision.Rejected(LocationRejectionReason.OutsideAllOfficeLocations, nearestDistance)
                : LocationDecision.Rejected(LocationRejectionReason.AccuracyInsufficient, nearestDistance);
        }

        return withinRadius
            ? LocationDecision.Accepted(nearest.OfficeLocationId, nearestDistance)
            : LocationDecision.Rejected(LocationRejectionReason.OutsideAllOfficeLocations, nearestDistance);
    }
}

/// <summary>
/// How the device's reported accuracy affects the location decision.
/// </summary>
public enum LocationAccuracyPolicy
{
    /// <summary>
    /// Consider only the distance. The reported accuracy is recorded but does
    /// not affect the outcome.
    /// </summary>
    DistanceOnly = 0,

    /// <summary>
    /// Require the reported accuracy to be no worse than the configured
    /// threshold, in addition to the distance test. This is the interim default.
    /// </summary>
    DistanceAndAccuracyThreshold = 1,
}

/// <summary>
/// Why a position was refused.
/// </summary>
public enum LocationRejectionReason
{
    /// <summary>The position was accepted.</summary>
    None = 0,

    /// <summary>No approved office lies within its configured radius.</summary>
    OutsideAllOfficeLocations = 1,

    /// <summary>The reported accuracy is worse than policy allows.</summary>
    AccuracyInsufficient = 2,

    /// <summary>The platform flagged the position as mocked or simulated.</summary>
    SourceUntrusted = 3,

    /// <summary>No office locations are configured or active.</summary>
    NoActiveOfficeLocations = 4,
}

/// <summary>
/// An approved office location considered during evaluation.
/// </summary>
/// <param name="OfficeLocationId">Database identifier of the office.</param>
/// <param name="Coordinates">The office position.</param>
/// <param name="AllowedRadiusMeters">The office's configured radius.</param>
public readonly record struct OfficeLocationCandidate(
    int OfficeLocationId,
    Coordinates Coordinates,
    double AllowedRadiusMeters);

/// <summary>
/// The outcome of evaluating a reported position.
/// </summary>
/// <remarks>
/// <see cref="DistanceMeters"/> is for the server's own records — it is written
/// to the attendance evidence and used by the office statistics report. It is
/// never returned to the mobile client: §9 requires the response to carry a
/// decision and a reason code, not the internal geometry.
/// </remarks>
public readonly record struct LocationDecision
{
    private LocationDecision(
        bool isAccepted,
        int? officeLocationId,
        double? distanceMeters,
        LocationRejectionReason reason)
    {
        IsAccepted = isAccepted;
        OfficeLocationId = officeLocationId;
        DistanceMeters = distanceMeters;
        Reason = reason;
    }

    /// <summary>Whether the position is acceptable for attendance.</summary>
    public bool IsAccepted { get; }

    /// <summary>The matched office, when the position was accepted.</summary>
    public int? OfficeLocationId { get; }

    /// <summary>
    /// Distance to the nearest office in metres, where one could be computed.
    /// </summary>
    public double? DistanceMeters { get; }

    /// <summary>Why the position was refused.</summary>
    public LocationRejectionReason Reason { get; }

    internal static LocationDecision Accepted(int officeLocationId, double distanceMeters) =>
        new(true, officeLocationId, distanceMeters, LocationRejectionReason.None);

    internal static LocationDecision Rejected(
        LocationRejectionReason reason,
        double? distanceMeters = null) =>
        new(false, null, distanceMeters, reason);
}
