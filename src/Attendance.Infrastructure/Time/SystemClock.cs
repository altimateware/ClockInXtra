using Attendance.Application.Abstractions;

namespace Attendance.Infrastructure.Time;

/// <summary>
/// The system clock, in UTC.
/// </summary>
/// <remarks>
/// Registered as a singleton. It is used for signature skew windows, challenge
/// expiry and TOTP evaluation — never for attendance timestamps, which are taken
/// by SQL Server inside the transaction that writes them (Claude.md §31,
/// decision DB-05). See <see cref="IClock"/> for why that distinction matters.
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
