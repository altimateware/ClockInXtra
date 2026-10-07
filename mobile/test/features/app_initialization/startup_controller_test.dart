import 'package:clockinxtra/core/device/device_integrity_service.dart';
import 'package:clockinxtra/core/location/location_service.dart';
import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/features/app_initialization/application/startup_controller.dart';
import 'package:clockinxtra/features/app_initialization/domain/startup_state.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fakes.dart';

/// The startup sequence of §8.1.
///
/// The property under test throughout: attendance is reached only when every
/// check has passed, in order, and nothing further is asked of the server once a
/// check has failed.
void main() {
  Future<StartupState> run(Harness harness) async {
    await harness.container.read(startupControllerProvider.notifier).restart();
    return harness.container.read(startupControllerProvider);
  }

  const ApiException locationRefused = ApiException(
    code: 'LOCATION_NOT_ALLOWED',
    message: 'You do not appear to be at an approved office location.',
    correlationId: '9f0c',
    statusCode: 403,
  );

  group('integrity', () {
    test('stops a compromised device before asking for its location', () async {
      final Harness harness = Harness(
        integrity: FakeIntegrity(const IntegrityReport(
          indicators: <String>['SU_BINARY_PRESENT'],
          isEmulator: false,
          checked: true,
        )),
      );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.deviceCompromised);
      expect(blocked.canRetry, isFalse);
      expect(harness.location.positionRequests, 0);
      expect(harness.api.calls, isEmpty);
    });

    test('treats a check that could not run as a failure, not a pass', () async {
      final Harness harness = Harness(
        integrity: FakeIntegrity(const IntegrityReport.unavailable('no native implementation')),
      );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.integrityCheckUnavailable);
      expect(harness.api.calls, isEmpty);
    });
  });

  test('reports an unconfigured build instead of crashing', () async {
    final Harness harness = Harness(apiFactoryFailure: StateError('no base URL'));

    final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

    expect(blocked.reason, StartupBlockReason.notConfigured);
  });

  group('location', () {
    test('asks for permission once, and stops when it is refused', () async {
      final Harness harness = Harness(
        location: FakeLocation(readiness: LocationReadiness.notRequested, afterRequest: LocationReadiness.denied),
      );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.locationPermissionDenied);
      expect(blocked.canRetry, isTrue);
      expect(harness.location.permissionRequests, 1);
      expect(harness.api.calls, isEmpty);
    });

    test('does not ask again once permission is refused permanently, and offers settings', () async {
      final Harness harness = Harness(location: FakeLocation(readiness: LocationReadiness.deniedForever));

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.locationPermissionDeniedForever);
      expect(blocked.canOpenSettings, isTrue);
      expect(harness.location.permissionRequests, 0);
    });

    test('stops when location services are off', () async {
      final Harness harness = Harness(location: FakeLocation(readiness: LocationReadiness.servicesDisabled));

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.locationServicesDisabled);
      expect(blocked.canOpenSettings, isTrue);
    });

    test('stops when no fix can be obtained', () async {
      final Harness harness = Harness(
        location: FakeLocation(failure: const LocationUnavailableException(LocationReadiness.ready, 'timeout')),
      );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.noLocationFix);
      expect(harness.api.calls, isEmpty);
    });

    test('shows the server refusal and asks nothing further of the server', () async {
      // §8.1: if validation fails, attendance is not exposed and startup does not
      // continue to the device or attendance checks.
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onValidateLocation = () async => throw locationRefused;

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.locationRejected);
      expect(blocked.serverMessage, locationRefused.message);
      expect(blocked.correlationId, '9f0c');
      expect(harness.api.calls, <String>['validateLocation']);
    });
  });

  test('reports an unreachable server as retryable, never as a result', () async {
    final Harness harness = Harness()
      ..api.onValidateLocation = () async => throw const ApiException(
            code: ApiException.networkUnavailable,
            message: 'The attendance service could not be reached.',
          );

    final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

    expect(blocked.reason, StartupBlockReason.serviceUnreachable);
    expect(blocked.canRetry, isTrue);
  });

  group('device', () {
    test('asks an unregistered device to register, without loading attendance', () async {
      final Harness harness = Harness();

      expectType<StartupNeedsRegistration>(await run(harness));

      expect(harness.api.calls, <String>['validateLocation']);
    });

    test('shows a pending device as waiting, via the status endpoint that answers it', () async {
      // A pending device's signed location check is refused before the location
      // is considered; the status endpoint is what says why.
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'));

      harness.api.onValidateLocation = () async {
        throw const ApiException(code: 'DEVICE_NOT_APPROVED', message: 'waiting', statusCode: 403);
      };
      harness.api.onDeviceState = () async => DeviceRegistrationState(
            status: 'PendingApproval',
            employeeActive: true,
            revokedReason: null,
            serverTimeUtc: DateTime.utc(2026, 9, 17),
          );

      expectType<StartupAwaitingApproval>(await run(harness));

      expect(harness.api.calls, isNot(contains('userStatus')));
    });

    test('shows a revoked device with the administrator’s reason', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onDeviceState = () async => DeviceRegistrationState(
              status: 'Revoked',
              employeeActive: true,
              revokedReason: 'Handset reported lost',
              serverTimeUtc: DateTime.utc(2026, 9, 17),
            );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.deviceRevoked);
      expect(blocked.serverMessage, 'Handset reported lost');
      expect(harness.api.calls, isNot(contains('userStatus')));
    });

    test('stops when the employee is no longer active', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onDeviceState = () async => DeviceRegistrationState(
              status: 'Active',
              employeeActive: false,
              revokedReason: null,
              serverTimeUtc: DateTime.utc(2026, 9, 17),
            );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.employeeInactive);
    });

    test('never treats a status it does not recognise as active', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onDeviceState = () async => DeviceRegistrationState(
              status: 'SomethingNew',
              employeeActive: true,
              revokedReason: null,
              serverTimeUtc: DateTime.utc(2026, 9, 17),
            );

      expectType<StartupBlocked>(await run(harness));
      expect(harness.api.calls, isNot(contains('userStatus')));
    });

    test('forgets a device the server no longer knows, and starts again once', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'gone', userId: 'e.adeyemi'));
      int attempts = 0;

      harness.api.onValidateLocation = () async {
        attempts++;
        if (attempts == 1) {
          throw const ApiException(code: 'DEVICE_NOT_REGISTERED', message: 'unknown', statusCode: 401);
        }
        return const LocationValidation(success: true, officeLocationId: null);
      };

      expectType<StartupNeedsRegistration>(await run(harness));

      expect(harness.store.clears, 1);
      expect(harness.store.devicePublicId, isNull);
      expect(attempts, 2);
    });

    test('does not loop when the server keeps saying the device is unknown', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'gone'))
        ..api.onValidateLocation =
            () async => throw const ApiException(code: 'DEVICE_NOT_REGISTERED', message: 'unknown', statusCode: 401);

      await run(harness);

      expect(harness.api.calls.where((String call) => call == 'validateLocation'), hasLength(lessThanOrEqualTo(2)));
    });
  });

  group('attendance', () {
    test('reaches attendance only after every check, with the server’s state', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onUserStatus = () async => snapshot(AttendanceState.clockedIn);

      final StartupReady ready = expectType<StartupReady>(await run(harness));

      expect(ready.attendance.state, AttendanceState.clockedIn);
      expect(harness.api.calls, <String>['validateLocation', 'deviceState', 'userStatus']);
    });

    test('reports unconfigured business hours rather than showing a state', () async {
      final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
        ..api.onUserStatus = () async => throw const ApiException(
              code: 'ATTENDANCE_NOT_CONFIGURED',
              message: 'Attendance is not yet configured.',
              statusCode: 503,
            );

      final StartupBlocked blocked = expectType<StartupBlocked>(await run(harness));

      expect(blocked.reason, StartupBlockReason.attendanceNotConfigured);
    });
  });

  test('joins a run already in progress instead of starting another', () async {
    // The location permission dialog sends the app to the background and back;
    // that resume must not start a second sequence racing the first.
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'));
    final StartupController controller = harness.container.read(startupControllerProvider.notifier);

    await Future.wait(<Future<void>>[controller.restart(), controller.restart(), controller.restart()]);

    expect(harness.api.calls.where((String call) => call == 'validateLocation'), hasLength(1));
  });
}
