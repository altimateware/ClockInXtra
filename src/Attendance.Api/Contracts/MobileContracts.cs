using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Attendance.Api.Contracts;

/// <summary>
/// A position reported by a device.
/// </summary>
/// <remarks>
/// <para>
/// Ranges are enforced at the boundary (§44). Latitude and longitude outside
/// their real ranges are not a business rejection to be reasoned about later —
/// they are malformed input, and refusing them here keeps impossible values out
/// of the geodesic calculation entirely.
/// </para>
/// <para>
/// <c>isMocked</c> is what the platform reported, not what the app decided. It is
/// a signal, not a guarantee: a compromised device can lie about it (CON-07,
/// §65).
/// </para>
/// </remarks>
public sealed class PositionRequest
{
    /// <summary>Latitude in degrees.</summary>
    [Range(-90d, 90d)]
    [JsonPropertyName("latitude")]
    public double Latitude { get; init; }

    /// <summary>Longitude in degrees.</summary>
    [Range(-180d, 180d)]
    [JsonPropertyName("longitude")]
    public double Longitude { get; init; }

    /// <summary>Horizontal accuracy in metres, where the platform supplies it.</summary>
    [Range(0d, 100_000d)]
    [JsonPropertyName("accuracyMeters")]
    public double? AccuracyMeters { get; init; }

    /// <summary>Whether the platform flagged the position as mock or simulated.</summary>
    [JsonPropertyName("isMocked")]
    public bool IsMocked { get; init; }
}

/// <summary>Startup location check (§8.1).</summary>
public sealed class ValidateLocationRequest
{
    /// <summary>The reported position.</summary>
    [Required]
    [JsonPropertyName("position")]
    public PositionRequest Position { get; init; } = new();

    /// <summary>The application version, for diagnostics.</summary>
    [StringLength(32)]
    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }
}

/// <summary>Result of the startup location check.</summary>
/// <param name="Success">Whether the position was accepted.</param>
/// <param name="OfficeLocationId">
/// The matched office, returned only to a registered device (OPEN-44).
/// </param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct ValidateLocationResponseBody(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("officeLocationId")] int? OfficeLocationId,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>Clock-in (§12).</summary>
public sealed class ClockInRequestBody
{
    /// <summary>
    /// The identifier the employee typed.
    /// </summary>
    /// <remarks>
    /// Accepted only when it matches the device binding (§6.2 step 9). A mobile
    /// client is untrusted; this is a claim, not an identity.
    /// </remarks>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>The password. Verified and discarded; never stored or logged (§24).</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>The six-digit authenticator code.</summary>
    [Required]
    [RegularExpression("^[0-9]{6}$")]
    [JsonPropertyName("authenticatorCode")]
    public string AuthenticatorCode { get; init; } = string.Empty;

    /// <summary>The reported position.</summary>
    [Required]
    [JsonPropertyName("position")]
    public PositionRequest Position { get; init; } = new();
}

/// <summary>Clock-out (§14). No password or code, per ASM-04.</summary>
public sealed class ClockOutRequestBody
{
    /// <summary>The reported position.</summary>
    [Required]
    [JsonPropertyName("position")]
    public PositionRequest Position { get; init; } = new();
}

/// <summary>The state an attendance operation left the employee in.</summary>
/// <param name="Success">Whether the operation completed.</param>
/// <param name="State">NotClockedIn, ClockedIn or Completed.</param>
/// <param name="AttendanceId">The record's external identifier.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ClockInUtc">When the record was opened.</param>
/// <param name="ClockOutUtc">When it was closed.</param>
/// <param name="DurationMinutes">Duration computed by the database.</param>
/// <param name="IsLateClockIn">Late flag, or null when the rule is unconfigured.</param>
/// <param name="IsEarlyClockOut">Early flag, or null when unconfigured.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct AttendanceResponseBody(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attendanceId")] Guid? AttendanceId,
    [property: JsonPropertyName("attendanceDate")] string? AttendanceDate,
    [property: JsonPropertyName("clockInUtc")] DateTimeOffset? ClockInUtc,
    [property: JsonPropertyName("clockOutUtc")] DateTimeOffset? ClockOutUtc,
    [property: JsonPropertyName("durationMinutes")] int? DurationMinutes,
    [property: JsonPropertyName("isLateClockIn")] bool? IsLateClockIn,
    [property: JsonPropertyName("isEarlyClockOut")] bool? IsEarlyClockOut,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>Device registration (§6.3).</summary>
public sealed class RegisterDeviceRequest
{
    /// <summary>The identifier the employee typed.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>The password. Never stored or logged (§24).</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>The six-digit authenticator code.</summary>
    [Required]
    [RegularExpression("^[0-9]{6}$")]
    [JsonPropertyName("authenticatorCode")]
    public string AuthenticatorCode { get; init; } = string.Empty;

    /// <summary>The challenge identifier issued by the challenge endpoint.</summary>
    [Required]
    [JsonPropertyName("challengeId")]
    public Guid ChallengeId { get; init; }

    /// <summary>The challenge bytes, base64.</summary>
    [Required]
    [StringLength(64, MinimumLength = 4)]
    [JsonPropertyName("challenge")]
    public string Challenge { get; init; } = string.Empty;

    /// <summary>The uncompressed P-256 public key, base64 (65 bytes).</summary>
    [Required]
    [StringLength(128, MinimumLength = 4)]
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>1 for Android, 2 for iOS.</summary>
    [Range(1, 2)]
    [JsonPropertyName("platform")]
    public int Platform { get; init; }

    /// <summary>The platform attestation blob, base64.</summary>
    [Required]
    [StringLength(32_768, MinimumLength = 4)]
    [JsonPropertyName("attestation")]
    public string Attestation { get; init; } = string.Empty;

    /// <summary>Apple's key identifier, base64. Unused on Android.</summary>
    [StringLength(128)]
    [JsonPropertyName("attestationKeyId")]
    public string? AttestationKeyId { get; init; }

    /// <summary>Untrusted client metadata.</summary>
    [StringLength(64)]
    [JsonPropertyName("deviceModel")]
    public string? DeviceModel { get; init; }

    /// <summary>Untrusted client metadata.</summary>
    [StringLength(32)]
    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; init; }

    /// <summary>Untrusted client metadata.</summary>
    [StringLength(32)]
    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }
}

/// <summary>An issued registration challenge.</summary>
/// <param name="ChallengeId">Returned with the registration request.</param>
/// <param name="Challenge">Bytes to embed in the attestation, base64.</param>
/// <param name="ExpiresUtc">When it stops being usable.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct ChallengeResponseBody(
    [property: JsonPropertyName("challengeId")] Guid ChallengeId,
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>The outcome of a registration.</summary>
/// <param name="Success">Whether a device record now exists.</param>
/// <param name="DeviceId">The new device's external identifier.</param>
/// <param name="RequiresApproval">Whether an administrator must still approve it.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct RegisterDeviceResponseBody(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("deviceId")] Guid? DeviceId,
    [property: JsonPropertyName("requiresApproval")] bool RequiresApproval,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>A device's own registration state.</summary>
/// <param name="Status">PendingApproval, Active or Revoked.</param>
/// <param name="EmployeeActive">Whether the bound employee is still active.</param>
/// <param name="RevokedReason">Why it was revoked, for its holder.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct DeviceStatusResponseBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("employeeActive")] bool EmployeeActive,
    [property: JsonPropertyName("revokedReason")] string? RevokedReason,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc,
    [property: JsonPropertyName("correlationId")] string CorrelationId);

/// <summary>The runtime configuration a mobile client may read.</summary>
/// <param name="Settings">Allow-listed settings.</param>
/// <param name="ClockInConfigured">Whether clock-in can operate.</param>
/// <param name="ClockOutConfigured">Whether clock-out can operate.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
public readonly record struct AppConfigResponseBody(
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, string?> Settings,
    [property: JsonPropertyName("clockInConfigured")] bool ClockInConfigured,
    [property: JsonPropertyName("clockOutConfigured")] bool ClockOutConfigured,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc,
    [property: JsonPropertyName("correlationId")] string CorrelationId);
