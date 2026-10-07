using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Records signature nonces so a captured request cannot be replayed.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>mobile.usp_RequestNonce_TryInsert</c>. <b>The check is the
/// insert.</b> There is no "has this been seen" read followed by a write, so two
/// concurrent requests carrying the same nonce cannot both pass — one insert
/// succeeds and the other violates the primary key.
/// </para>
/// <para>
/// This is what closes the window TLS leaves open. TLS stops an observer reading
/// or altering a request; it does nothing about a request captured on a
/// compromised client and sent again (§37). The nonce is single-use, and rows
/// outlive the clock-skew window so that a request cannot be held back and
/// replayed once its record has been purged.
/// </para>
/// </remarks>
public interface INonceStore
{
    /// <summary>
    /// Claims a nonce for a device.
    /// </summary>
    /// <returns>
    /// <see cref="AttendanceResultCode.Success"/> when the nonce is new, or
    /// <see cref="AttendanceResultCode.ReplayedRequest"/> when it has been seen.
    /// </returns>
    Task<AttendanceResultCode> TryClaimAsync(
        int deviceId,
        ReadOnlyMemory<byte> nonce,
        DateTimeOffset signatureCreatedUtc,
        CancellationToken cancellationToken);
}
