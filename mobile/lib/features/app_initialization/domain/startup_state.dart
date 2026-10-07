import 'package:flutter/foundation.dart';

import '../../../core/location/location_service.dart';
import '../../../core/network/api_models.dart';

/// Where the startup sequence of §8.1 has got to.
///
/// Sealed, so the screen must handle every outcome. There is no state that
/// means "probably fine": the only way to reach attendance is [StartupReady],
/// and only the server's answers lead there.
sealed class StartupState {
  const StartupState();
}

/// A step is running.
@immutable
final class StartupInProgress extends StartupState {
  /// Creates the state.
  const StartupInProgress(this.step);

  /// Which step.
  final StartupStep step;
}

/// The steps, in the order §8.1 requires them.
enum StartupStep {
  /// Looking for signs of root or jailbreak.
  checkingIntegrity,

  /// Checking, and if necessary asking for, location permission.
  checkingLocationPermission,

  /// Waiting for a position fix.
  locating,

  /// The server is checking the position against the approved offices.
  validatingLocation,

  /// The server is reporting this device's registration state.
  checkingDevice,

  /// The server is reporting today's attendance state.
  loadingAttendance,
}

/// Startup stopped, and attendance is not offered.
@immutable
final class StartupBlocked extends StartupState {
  /// Creates the state.
  const StartupBlocked(
    this.reason, {
    this.serverMessage,
    this.correlationId,
    this.locationReadiness,
  });

  /// Why.
  final StartupBlockReason reason;

  /// The server's own sentence, where it gave one. Safe to show: the API never
  /// puts internal detail in a message (§34).
  final String? serverMessage;

  /// Quoted to support, to find the request in the server log.
  final String? correlationId;

  /// For the location reasons, which system setting would fix it.
  final LocationReadiness? locationReadiness;

  /// Whether trying again could plausibly succeed without anyone else acting.
  bool get canRetry => switch (reason) {
        StartupBlockReason.deviceCompromised ||
        StartupBlockReason.integrityCheckUnavailable ||
        StartupBlockReason.unsupportedPlatform ||
        StartupBlockReason.notConfigured ||
        StartupBlockReason.appVersionUnsupported ||
        StartupBlockReason.deviceRevoked ||
        StartupBlockReason.employeeInactive =>
          false,
        _ => true,
      };

  /// Whether a system settings screen can fix it.
  bool get canOpenSettings =>
      reason == StartupBlockReason.locationServicesDisabled ||
      reason == StartupBlockReason.locationPermissionDeniedForever;
}

/// Why startup stopped.
enum StartupBlockReason {
  /// Signs of root or jailbreak were found (§25).
  deviceCompromised,

  /// The integrity check could not run. Refused, not assumed to pass.
  integrityCheckUnavailable,

  /// Not Android or iOS.
  unsupportedPlatform,

  /// The build has no valid API address.
  notConfigured,

  /// Location services are switched off.
  locationServicesDisabled,

  /// Location permission was refused; it can be asked for again.
  locationPermissionDenied,

  /// Location permission was refused permanently; only settings can restore it.
  locationPermissionDeniedForever,

  /// No position fix could be obtained in time.
  noLocationFix,

  /// The server did not accept the position (§9).
  locationRejected,

  /// The device's clock is too far from the server's for a signature to verify.
  clockSkew,

  /// The server no longer supports this app version.
  appVersionUnsupported,

  /// The device was revoked by an administrator.
  deviceRevoked,

  /// The employee's account is not active.
  employeeInactive,

  /// The business hours have not been configured, so attendance cannot operate.
  attendanceNotConfigured,

  /// The server could not be reached.
  serviceUnreachable,

  /// Anything else the server refused.
  unexpected,
}

/// Location passed, and this device has never been registered.
final class StartupNeedsRegistration extends StartupState {
  /// Creates the state.
  const StartupNeedsRegistration();
}

/// The device is registered and waiting for an administrator (DEC-04).
final class StartupAwaitingApproval extends StartupState {
  /// Creates the state.
  const StartupAwaitingApproval();
}

/// Every check passed; attendance can be offered.
@immutable
final class StartupReady extends StartupState {
  /// Creates the state.
  const StartupReady(this.attendance);

  /// Today's attendance, as the server reports it (§11).
  final AttendanceSnapshot attendance;
}
