import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/device/app_identity.dart';
import '../../../core/device/device_integrity_service.dart';
import '../../../core/di/providers.dart';
import '../../../core/location/location_service.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/network/api_models.dart';
import '../../../core/network/attendance_api.dart';
import '../../../core/storage/secure_store.dart';
import '../domain/startup_state.dart';

/// Runs the startup sequence of §8.1 and decides what the employee may see.
final NotifierProvider<StartupController, StartupState> startupControllerProvider =
    NotifierProvider<StartupController, StartupState>(StartupController.new);

/// The startup sequence.
///
/// **Order, and why.** §8.1 fixes it: integrity, then device identity, then
/// location, then the server's location check — and nothing further unless that
/// passes. After it, the device's registration state and today's attendance
/// come from the server, never from anything this app remembers (§11): a
/// reinstall, a restored backup or a second device would each make a local
/// answer confidently wrong.
///
/// **Every failure is a state, not an exception.** The screen renders exactly
/// what this decides, so there is no path on which an unexpected error leaves
/// attendance on screen. Nothing is retried silently either: an attendance app
/// that quietly retries in the background is how an employee comes to believe
/// something happened that did not (§52). The one internal repeat — after the
/// server's view of this device turns out to differ from the app's — happens at
/// most once per run, and is awaited like everything else.
final class StartupController extends Notifier<StartupState> {
  Future<void>? _running;

  @override
  StartupState build() {
    // Started on first read. Scheduled rather than awaited, because build() must
    // return synchronously; the returned state is the first step.
    scheduleMicrotask(restart);
    return const StartupInProgress(StartupStep.checkingIntegrity);
  }

  /// Runs the whole sequence from the beginning.
  ///
  /// Called at launch, when the employee taps try again, after registering, and
  /// when the app returns to the foreground — the office they were at when they
  /// opened the app is not necessarily where they are now. A call while a run is
  /// already in progress joins that run rather than starting a second, so two
  /// sequences can never race to set the state.
  Future<void> restart() => _running ??= _sequence(mayRepeat: true).whenComplete(() => _running = null);

  /// Shows attendance the server has just reported, such as the response to a
  /// clock-in.
  ///
  /// Only accepted while attendance is already on screen: a response arriving
  /// after the app has moved elsewhere — a restart that found the device revoked,
  /// say — must not put attendance back on top of that.
  void showAttendance(AttendanceSnapshot attendance) {
    if (state is StartupReady) {
      state = StartupReady(attendance);
    }
  }

  Future<void> _sequence({required bool mayRepeat}) async {
    // 1. Integrity.
    state = const StartupInProgress(StartupStep.checkingIntegrity);

    final IntegrityReport integrity = await ref.read(deviceIntegrityServiceProvider).inspect();

    if (!integrity.checked) {
      state = const StartupBlocked(StartupBlockReason.integrityCheckUnavailable);
      return;
    }

    if (integrity.indicators.isNotEmpty) {
      debugPrint('[startup] integrity indicators: ${integrity.indicators.join(', ')}');
      state = const StartupBlocked(StartupBlockReason.deviceCompromised);
      return;
    }

    // 2. Identity: what this app is, and whether this device was registered.
    final SecureStore store = ref.read(secureStoreProvider);
    final AttendanceApi api;

    try {
      final AppIdentity identity = await ref.read(appIdentityLoaderProvider)();
      api = ref.read(attendanceApiFactoryProvider)(identity);
    } on UnsupportedError {
      state = const StartupBlocked(StartupBlockReason.unsupportedPlatform);
      return;
    } on StateError catch (error) {
      debugPrint('[startup] not configured: ${error.message}');
      state = const StartupBlocked(StartupBlockReason.notConfigured);
      return;
    }

    final String? devicePublicId = await store.readDevicePublicId();

    // 3. Location permission, then a fix.
    state = const StartupInProgress(StartupStep.checkingLocationPermission);

    final LocationService location = ref.read(locationServiceProvider);
    LocationReadiness readiness = await location.checkReadiness();

    if (readiness == LocationReadiness.notRequested || readiness == LocationReadiness.denied) {
      readiness = await location.requestPermission();
    }

    final StartupBlockReason? permissionProblem = switch (readiness) {
      LocationReadiness.ready => null,
      LocationReadiness.servicesDisabled => StartupBlockReason.locationServicesDisabled,
      LocationReadiness.deniedForever => StartupBlockReason.locationPermissionDeniedForever,
      LocationReadiness.denied || LocationReadiness.notRequested => StartupBlockReason.locationPermissionDenied,
    };

    if (permissionProblem != null) {
      state = StartupBlocked(permissionProblem, locationReadiness: readiness);
      return;
    }

    state = const StartupInProgress(StartupStep.locating);

    final DevicePosition position;

    try {
      position = await location.currentPosition();
    } on LocationUnavailableException catch (error) {
      debugPrint('[startup] no fix: ${error.message}');
      state = StartupBlocked(StartupBlockReason.noLocationFix, locationReadiness: error.readiness);
      return;
    }

    // 4. The server's location check. Nothing past here unless it passes.
    state = const StartupInProgress(StartupStep.validatingLocation);

    try {
      await api.validateLocationAsync(
        ReportedPosition(
          latitude: position.latitude,
          longitude: position.longitude,
          accuracyMeters: position.accuracyMeters,
          isMocked: position.isMocked,
        ),
      );
    } on ApiException catch (error) {
      if (error.code == 'DEVICE_NOT_REGISTERED' && devicePublicId != null && mayRepeat) {
        // The server does not know the identifier this app stored: the device
        // record was removed, or the database restored without it. The stored
        // identifier is worthless, so it is forgotten and the sequence runs
        // once more as an unregistered device.
        debugPrint('[startup] stored device unknown to the server; forgetting it');
        await store.clear();
        await _sequence(mayRepeat: false);
        return;
      }

      await _handleRefusal(api, error, mayRepeat: mayRepeat);
      return;
    }

    if (devicePublicId == null) {
      state = const StartupNeedsRegistration();
      return;
    }

    // 5. The device's own state, then today's attendance.
    state = const StartupInProgress(StartupStep.checkingDevice);

    final _DeviceVerdict verdict = await _applyDeviceState(api);

    if (verdict != _DeviceVerdict.active) {
      return;
    }

    state = const StartupInProgress(StartupStep.loadingAttendance);

    try {
      state = StartupReady(await api.getUserStatusAsync());
    } on ApiException catch (error) {
      await _handleRefusal(api, error, mayRepeat: mayRepeat);
    }
  }

  /// Handles a refused request.
  ///
  /// A signed request from a device that cannot act is refused before the
  /// endpoint runs, so the refusal says little. The device-status endpoint does
  /// answer pending and revoked devices, and is asked for the real position.
  Future<void> _handleRefusal(AttendanceApi api, ApiException error, {required bool mayRepeat}) async {
    if (error.code != 'DEVICE_NOT_APPROVED' && error.code != 'DEVICE_REVOKED') {
      state = _blockedBy(error);
      return;
    }

    final _DeviceVerdict verdict = await _applyDeviceState(api);

    if (verdict != _DeviceVerdict.active) {
      return;
    }

    // The status endpoint now says active: the approval landed between the two
    // requests. Run once more; if the server still refuses, show that rather
    // than asking again.
    if (mayRepeat) {
      await _sequence(mayRepeat: false);
    } else {
      state = _blockedBy(error);
    }
  }

  /// Sets the state from the server's view of this device, unless it is active.
  Future<_DeviceVerdict> _applyDeviceState(AttendanceApi api) async {
    final DeviceRegistrationState device;

    try {
      device = await api.getDeviceStateAsync();
    } on ApiException catch (error) {
      state = _blockedBy(error);
      return _DeviceVerdict.handled;
    }

    switch (device.status) {
      case 'PendingApproval':
        state = const StartupAwaitingApproval();
        return _DeviceVerdict.handled;

      case 'Revoked':
        state = StartupBlocked(StartupBlockReason.deviceRevoked, serverMessage: device.revokedReason);
        return _DeviceVerdict.handled;

      case 'Active' when !device.employeeActive:
        state = const StartupBlocked(StartupBlockReason.employeeInactive);
        return _DeviceVerdict.handled;

      case 'Active':
        return _DeviceVerdict.active;

      default:
        // A status this build has no name for. Never treated as active.
        state = const StartupBlocked(StartupBlockReason.unexpected);
        return _DeviceVerdict.handled;
    }
  }

  static StartupBlocked _blockedBy(ApiException error) {
    if (error.isTransport) {
      return StartupBlocked(StartupBlockReason.serviceUnreachable, serverMessage: error.message);
    }

    final StartupBlockReason reason = switch (error.code) {
      'LOCATION_NOT_ALLOWED' ||
      'LOCATION_ACCURACY_INSUFFICIENT' ||
      'LOCATION_SOURCE_UNTRUSTED' =>
        StartupBlockReason.locationRejected,
      'CLOCK_SKEW' => StartupBlockReason.clockSkew,
      'APP_VERSION_UNSUPPORTED' => StartupBlockReason.appVersionUnsupported,
      'ATTENDANCE_NOT_CONFIGURED' => StartupBlockReason.attendanceNotConfigured,
      'DEVICE_REVOKED' => StartupBlockReason.deviceRevoked,
      _ => StartupBlockReason.unexpected,
    };

    return StartupBlocked(reason, serverMessage: error.message, correlationId: error.correlationId);
  }
}

enum _DeviceVerdict {
  /// Active, with an active employee: carry on.
  active,

  /// Anything else; the state has already been set.
  handled,
}
