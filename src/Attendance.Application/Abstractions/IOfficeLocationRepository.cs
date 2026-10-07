using Attendance.Domain.Services;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Supplies the approved office locations a reported position is measured
/// against.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>mobile.usp_OfficeLocation_GetActive</c>. The distance calculation
/// itself is not done here and not done in SQL: it lives in
/// <see cref="Attendance.Domain.Services.GeoDistance"/> so there is exactly one
/// implementation, unit-testable against the boundary cases §51 requires.
/// </para>
/// <para>
/// The result is cached briefly by the implementation, because every startup
/// check and every attendance transaction needs it and the table changes rarely.
/// The cache is invalidated when an administrator changes a location: a stale
/// entry would mean an office that was just disabled still accepting clock-ins,
/// which is why the attendance procedures re-check the office inside their own
/// transaction regardless (result code 1043).
/// </para>
/// </remarks>
public interface IOfficeLocationRepository
{
    /// <summary>
    /// Returns the active office locations.
    /// </summary>
    /// <remarks>
    /// An empty list is a valid answer and a meaningful one: it means nobody can
    /// clock in anywhere, which is an administrative problem rather than the
    /// employee being in the wrong place, and the location policy reports it
    /// distinctly.
    /// </remarks>
    Task<IReadOnlyList<OfficeLocationCandidate>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops any cached copy, so the next read reflects an administrator's
    /// change immediately.
    /// </summary>
    void InvalidateCache();
}
