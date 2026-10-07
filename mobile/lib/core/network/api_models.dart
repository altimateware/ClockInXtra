import 'dart:convert';

import 'package:flutter/foundation.dart';

/// The platform codes the API's `platform` field accepts.
///
/// Fixed by the server contract (`RegisterDeviceRequest.Platform`, range 1–2),
/// not by Dart's own platform enumeration.
enum DevicePlatformCode {
  /// Android.
  android(1),

  /// iOS.
  ios(2);

  const DevicePlatformCode(this.wireValue);

  /// The integer the API expects.
  final int wireValue;
}

/// A position as reported to the API.
///
/// [isMocked] is what the platform said, passed through unaltered. The app must
/// not decide for itself that a position is genuine, and must not suppress the
/// flag — the server weighs it, and §65 is explicit that a compromised device
/// can lie about it either way.
@immutable
final class ReportedPosition {
  /// Creates a position.
  const ReportedPosition({
    required this.latitude,
    required this.longitude,
    required this.accuracyMeters,
    required this.isMocked,
  });

  /// Latitude in degrees.
  final double latitude;

  /// Longitude in degrees.
  final double longitude;

  /// Horizontal accuracy in metres, where the platform supplied one.
  final double? accuracyMeters;

  /// Whether the platform flagged the fix as mock or simulated.
  final bool isMocked;

  /// The wire representation.
  Map<String, Object?> toJson() => <String, Object?>{
        'latitude': latitude,
        'longitude': longitude,
        'accuracyMeters': accuracyMeters,
        'isMocked': isMocked,
      };
}

/// A single-use challenge to embed in a key attestation.
@immutable
final class RegistrationChallenge {
  /// Creates the challenge.
  const RegistrationChallenge({
    required this.challengeId,
    required this.challenge,
    required this.encoded,
    required this.expiresUtc,
  });

  /// Reads the server's response.
  factory RegistrationChallenge.fromJson(Map<String, Object?> json) {
    final String encoded = json['challenge']! as String;

    return RegistrationChallenge(
      challengeId: json['challengeId']! as String,
      challenge: base64.decode(encoded),
      encoded: encoded,
      expiresUtc: DateTime.parse(json['expiresUtc']! as String),
    );
  }

  /// Returned with the registration request so the server can find its record.
  final String challengeId;

  /// The bytes to pass to the secure element as the attestation challenge.
  final Uint8List challenge;

  /// The same bytes as the server encoded them.
  ///
  /// Echoed back verbatim rather than re-encoded from [challenge], so a
  /// difference in base64 padding cannot turn a valid registration into a
  /// mysterious rejection.
  final String encoded;

  /// When it stops being usable.
  final DateTime expiresUtc;
}

/// What registering a device produced.
@immutable
final class DeviceRegistration {
  /// Creates the result.
  const DeviceRegistration({
    required this.deviceId,
    required this.requiresApproval,
  });

  /// Reads the server's response.
  factory DeviceRegistration.fromJson(Map<String, Object?> json) =>
      DeviceRegistration(
        deviceId: json['deviceId'] as String?,
        requiresApproval: json['requiresApproval'] as bool? ?? false,
      );

  /// The identifier to sign subsequent requests with, once approved.
  final String? deviceId;

  /// Whether an administrator must still approve the device (DEC-04).
  ///
  /// Not an error. The device is registered and must poll its own status; it
  /// simply cannot record attendance yet.
  final bool requiresApproval;
}

/// A device's own registration state, as the server sees it.
@immutable
final class DeviceRegistrationState {
  /// Creates the state.
  const DeviceRegistrationState({
    required this.status,
    required this.employeeActive,
    required this.revokedReason,
    required this.serverTimeUtc,
  });

  /// Reads the server's response.
  factory DeviceRegistrationState.fromJson(Map<String, Object?> json) =>
      DeviceRegistrationState(
        status: json['status']! as String,
        employeeActive: json['employeeActive'] as bool? ?? false,
        revokedReason: json['revokedReason'] as String?,
        serverTimeUtc: DateTime.parse(json['serverTimeUtc']! as String),
      );

  /// `PendingApproval`, `Active` or `Revoked`.
  final String status;

  /// Whether the bound employee is still active.
  final bool employeeActive;

  /// Why the device was revoked, where it was.
  final String? revokedReason;

  /// Authoritative server time.
  final DateTime serverTimeUtc;

  /// Whether this device may currently record attendance.
  bool get isUsable => status == 'Active' && employeeActive;
}

/// The outcome of the startup location check.
@immutable
final class LocationValidation {
  /// Creates the result.
  const LocationValidation({required this.success, required this.officeLocationId});

  /// Reads the server's response.
  factory LocationValidation.fromJson(Map<String, Object?> json) => LocationValidation(
        success: json['success'] as bool? ?? false,
        officeLocationId: json['officeLocationId'] as int?,
      );

  /// Whether the position was accepted.
  final bool success;

  /// The matched office, returned only to a registered device (OPEN-44).
  final int? officeLocationId;
}

/// Where an employee stands for the current attendance day.
///
/// Every field here comes from the server, including the time. The app never
/// computes attendance state from what it did last, because a reinstall, a
/// restored backup or a second device would each give a confident wrong answer
/// (§11, §31).
@immutable
final class AttendanceSnapshot {
  /// Creates the snapshot.
  const AttendanceSnapshot({
    required this.state,
    required this.attendanceId,
    required this.attendanceDate,
    required this.clockInUtc,
    required this.clockOutUtc,
    required this.durationMinutes,
    required this.isLateClockIn,
    required this.isEarlyClockOut,
    required this.serverTimeUtc,
    required this.correlationId,
  });

  /// Reads the server's response.
  factory AttendanceSnapshot.fromJson(Map<String, Object?> json) => AttendanceSnapshot(
        state: AttendanceState.parse(json['state'] as String?),
        attendanceId: json['attendanceId'] as String?,
        attendanceDate: json['attendanceDate'] as String?,
        clockInUtc: _parseTime(json['clockInUtc']),
        clockOutUtc: _parseTime(json['clockOutUtc']),
        durationMinutes: json['durationMinutes'] as int?,
        isLateClockIn: json['isLateClockIn'] as bool?,
        isEarlyClockOut: json['isEarlyClockOut'] as bool?,
        serverTimeUtc: DateTime.parse(json['serverTimeUtc']! as String),
        correlationId: json['correlationId'] as String? ?? '',
      );

  /// What the app should offer: Clock-In, Clock-Out, or neither.
  final AttendanceState state;

  /// The record's identifier, where one exists.
  final String? attendanceId;

  /// The business-local attendance date, `yyyy-MM-dd`.
  ///
  /// Deliberately a string. It is a date in the organisation's attendance time
  /// zone, and parsing it into a [DateTime] here would silently attach the
  /// handset's zone to it (§31).
  final String? attendanceDate;

  /// When the record was opened.
  final DateTime? clockInUtc;

  /// When it was closed.
  final DateTime? clockOutUtc;

  /// Duration computed by the database, never by the client.
  final int? durationMinutes;

  /// Late flag, or null where the rule is not configured.
  final bool? isLateClockIn;

  /// Early flag, or null where the rule is not configured.
  final bool? isEarlyClockOut;

  /// Authoritative server time.
  final DateTime serverTimeUtc;

  /// Ties this response to the server's logs.
  final String correlationId;

  static DateTime? _parseTime(Object? value) =>
      value is String ? DateTime.parse(value) : null;
}

/// The three states the server reports.
enum AttendanceState {
  /// No record for today: offer Clock-In.
  notClockedIn,

  /// An open record exists: offer Clock-Out.
  clockedIn,

  /// Today's record is closed: offer neither.
  completed;

  /// Reads the server's spelling.
  ///
  /// An unrecognised value becomes [notClockedIn] and **is not** treated as a
  /// reason to offer Clock-Out. If the two ever disagree, the server refuses the
  /// operation anyway; guessing the permissive option would only produce a
  /// button that fails.
  static AttendanceState parse(String? value) => switch (value) {
        'ClockedIn' => AttendanceState.clockedIn,
        'Completed' => AttendanceState.completed,
        _ => AttendanceState.notClockedIn,
      };
}

/// The runtime configuration a mobile client may read.
@immutable
final class MobileConfiguration {
  /// Creates the configuration.
  const MobileConfiguration({
    required this.settings,
    required this.clockInConfigured,
    required this.clockOutConfigured,
    required this.serverTimeUtc,
  });

  /// Reads the server's response.
  factory MobileConfiguration.fromJson(Map<String, Object?> json) {
    final Map<String, Object?> settings =
        (json['settings'] as Map<String, Object?>?) ?? <String, Object?>{};

    return MobileConfiguration(
      settings: <String, String?>{
        for (final MapEntry<String, Object?> entry in settings.entries)
          entry.key: entry.value as String?,
      },
      clockInConfigured: json['clockInConfigured'] as bool? ?? false,
      clockOutConfigured: json['clockOutConfigured'] as bool? ?? false,
      serverTimeUtc: DateTime.parse(json['serverTimeUtc']! as String),
    );
  }

  /// The allow-listed settings. Never contains a security setting.
  final Map<String, String?> settings;

  /// Whether clock-in can operate at all.
  ///
  /// False means the business hours have not been confirmed. The app says so
  /// rather than offering a button that will fail — a poor thing to discover at
  /// 08:00 with a queue behind you (§15, OPEN-06).
  final bool clockInConfigured;

  /// Whether clock-out can operate at all.
  final bool clockOutConfigured;

  /// Authoritative server time.
  final DateTime serverTimeUtc;
}
