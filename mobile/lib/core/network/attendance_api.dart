import 'dart:typed_data';

import 'api_models.dart';

/// What the application needs from the attendance API.
///
/// An interface over [AttendanceApiClient] so the startup and registration logic
/// can be tested against scripted responses — including the failures, which are
/// the paths an employee standing at a door most needs to be right. Every method
/// throws `ApiException` on refusal or transport failure; none returns a
/// half-success.
abstract interface class AttendanceApi {
  /// Reads the settings and configured flags a mobile client may see.
  Future<MobileConfiguration> getConfigurationAsync();

  /// Runs the startup location check (§8.1).
  Future<LocationValidation> validateLocationAsync(ReportedPosition position);

  /// Obtains a single-use challenge to bind into a key attestation.
  Future<RegistrationChallenge> requestRegistrationChallengeAsync();

  /// Registers this device against an employee.
  Future<DeviceRegistration> registerDeviceAsync({
    required String userId,
    required String password,
    required String authenticatorCode,
    required RegistrationChallenge challenge,
    required Uint8List publicKey,
    required Uint8List attestation,
    required DevicePlatformCode platform,
    Uint8List? attestationKeyId,
    String? deviceModel,
    String? osVersion,
  });

  /// Asks the server what it thinks of this device.
  Future<DeviceRegistrationState> getDeviceStateAsync();

  /// Reads the employee's attendance state for the current day (§11).
  Future<AttendanceSnapshot> getUserStatusAsync();

  /// Records a clock-in (§12).
  Future<AttendanceSnapshot> clockInAsync({
    required String userId,
    required String password,
    required String authenticatorCode,
    required ReportedPosition position,
    required String idempotencyKey,
  });

  /// Records a clock-out (§14).
  Future<AttendanceSnapshot> clockOutAsync({
    required ReportedPosition position,
    required String idempotencyKey,
  });
}
