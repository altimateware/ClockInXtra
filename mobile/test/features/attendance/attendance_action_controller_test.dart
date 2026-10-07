import 'dart:async';

import 'package:clockinxtra/core/location/location_service.dart';
import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/features/app_initialization/application/startup_controller.dart';
import 'package:clockinxtra/features/app_initialization/domain/startup_state.dart';
import 'package:clockinxtra/features/attendance/application/attendance_action_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fakes.dart';

/// Clock-in and clock-out (§12, §14, §52, §53).
void main() {
  const String password = 'the employee password';

  /// A registered, approved device at the office, not yet clocked in.
  Future<Harness> ready({AttendanceState initial = AttendanceState.notClockedIn}) async {
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1', userId: 'e.adeyemi'))
      ..api.onUserStatus = () async => snapshot(initial);

    await harness.container.read(startupControllerProvider.notifier).restart();
    expectType<StartupReady>(harness.container.read(startupControllerProvider));

    harness.api.calls.clear();
    return harness;
  }

  AttendanceActionController actions(Harness harness) =>
      harness.container.read(attendanceActionControllerProvider.notifier);

  AttendanceActionState actionState(Harness harness) => harness.container.read(attendanceActionControllerProvider);

  StartupState screen(Harness harness) => harness.container.read(startupControllerProvider);

  Future<void> clockIn(Harness harness, {String code = '123456'}) =>
      actions(harness).clockIn(userId: 'e.adeyemi', password: password, authenticatorCode: code);

  group('clock-in', () {
    test('records it and shows the server’s answer', () async {
      final Harness harness = await ready();
      final int fixesBefore = harness.location.positionRequests;

      await clockIn(harness);

      final StartupReady shown = expectType<StartupReady>(screen(harness));
      expect(shown.attendance.state, AttendanceState.clockedIn);
      expect(actionState(harness).failure, isNull);
      expect(harness.api.lastClockInPassword, password);

      // A fresh fix is taken for the clock-in itself; the one from startup
      // could be minutes old (§12).
      expect(harness.location.positionRequests, fixesBefore + 1);
    });

    test('sends nothing when the password or code is missing', () async {
      final Harness harness = await ready();
      final int fixesBefore = harness.location.positionRequests;

      await clockIn(harness, code: '12');

      expect(actionState(harness).failure, AttendanceActionFailure.incomplete);
      expect(harness.api.calls, isEmpty);
      expect(harness.location.positionRequests, fixesBefore);
    });

    test('uses a new idempotency key for every attempt', () async {
      final Harness harness = await ready();

      harness.api.onClockIn = () async =>
          throw const ApiException(code: 'INVALID_CREDENTIALS', message: 'no', statusCode: 401);
      await clockIn(harness);

      harness.api.onClockIn = () async => snapshot(AttendanceState.clockedIn);
      await clockIn(harness);

      expect(harness.api.idempotencyKeys, hasLength(2));
      expect(harness.api.idempotencyKeys.toSet(), hasLength(2));
    });

    test('never resends after a lost answer, and never shows it as done', () async {
      // §52: an unknown outcome is not a success, and a blind resend is not the
      // way to find out.
      final Harness harness = await ready();
      harness.api.onClockIn = () async => throw const ApiException(
            code: ApiException.networkTimeout,
            message: 'The server did not respond in time.',
          );

      await clockIn(harness);

      expect(actionState(harness).failure, AttendanceActionFailure.outcomeUnknown);
      expect(harness.api.calls.where((String call) => call == 'clockIn'), hasLength(1));

      final StartupReady shown = expectType<StartupReady>(screen(harness));
      expect(shown.attendance.state, AttendanceState.notClockedIn);
    });

    test('asks the server what happened, and shows its answer instead of the doubt', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async =>
          throw const ApiException(code: ApiException.networkTimeout, message: 'timeout');

      await clockIn(harness);

      // The request had in fact landed; the server now says so.
      harness.api.onUserStatus = () async => snapshot(AttendanceState.clockedIn);
      await actions(harness).checkWithServer();

      expect(actionState(harness).failure, isNull);
      expect(expectType<StartupReady>(screen(harness)).attendance.state, AttendanceState.clockedIn);
      expect(harness.api.calls.where((String call) => call == 'clockIn'), hasLength(1));
    });

    test('treats an already-recorded clock-in as information and shows the latest state', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async =>
          throw const ApiException(code: 'ALREADY_CLOCKED_IN', message: 'already', statusCode: 409);
      harness.api.onUserStatus = () async => snapshot(AttendanceState.clockedIn);

      await clockIn(harness);

      expect(actionState(harness).failure, AttendanceActionFailure.alreadyDone);
      expect(expectType<StartupReady>(screen(harness)).attendance.state, AttendanceState.clockedIn);
    });

    test('sends nothing when no position fix can be taken', () async {
      final Harness harness = await ready();
      harness.location.failure = const LocationUnavailableException(LocationReadiness.ready, 'timeout');

      await clockIn(harness);

      expect(actionState(harness).failure, AttendanceActionFailure.locationUnavailable);
      expect(harness.api.calls, isEmpty);
    });

    test('shows the server’s reason for refusing the location', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async => throw const ApiException(
            code: 'LOCATION_ACCURACY_INSUFFICIENT',
            message: 'Your location could not be determined accurately enough.',
            correlationId: 'ref-9',
            statusCode: 403,
          );

      await clockIn(harness);

      final AttendanceActionState state = actionState(harness);
      expect(state.failure, AttendanceActionFailure.locationRejected);
      expect(state.serverMessage, 'Your location could not be determined accurately enough.');
      expect(state.correlationId, 'ref-9');
    });

    test('reports hours outside the permitted window', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async => throw const ApiException(
            code: 'ATTENDANCE_WINDOW_CLOSED',
            message: 'That action is outside the permitted hours.',
            statusCode: 409,
          );

      await clockIn(harness);

      expect(actionState(harness).failure, AttendanceActionFailure.windowClosed);
    });

    test('reports a phone registered to a different employee', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async => throw const ApiException(code: 'FORBIDDEN', message: 'no', statusCode: 403);

      await clockIn(harness);

      expect(actionState(harness).failure, AttendanceActionFailure.wrongEmployee);
    });

    test('hands a device revoked mid-session back to startup, which explains it', () async {
      final Harness harness = await ready();
      harness.api.onClockIn = () async =>
          throw const ApiException(code: 'DEVICE_REVOKED', message: 'revoked', statusCode: 403);
      harness.api.onValidateLocation = () async =>
          throw const ApiException(code: 'DEVICE_REVOKED', message: 'revoked', statusCode: 403);
      harness.api.onDeviceState = () async => DeviceRegistrationState(
            status: 'Revoked',
            employeeActive: true,
            revokedReason: 'Handset reported lost',
            serverTimeUtc: DateTime.utc(2026, 9, 18),
          );

      await clockIn(harness);

      final StartupBlocked blocked = expectType<StartupBlocked>(screen(harness));
      expect(blocked.reason, StartupBlockReason.deviceRevoked);
    });

    test('ignores a second tap while the first is in flight', () async {
      final Harness harness = await ready();
      final Completer<AttendanceSnapshot> answer = Completer<AttendanceSnapshot>();
      harness.api.onClockIn = () => answer.future;

      final Future<void> first = clockIn(harness);
      await Future<void>.delayed(Duration.zero);
      await clockIn(harness);

      answer.complete(snapshot(AttendanceState.clockedIn));
      await first;

      expect(harness.api.calls.where((String call) => call == 'clockIn'), hasLength(1));
    });
  });

  group('clock-out', () {
    test('records it and shows the completed day', () async {
      final Harness harness = await ready(initial: AttendanceState.clockedIn);

      await actions(harness).clockOut();

      expect(expectType<StartupReady>(screen(harness)).attendance.state, AttendanceState.completed);
      expect(harness.api.calls, <String>['clockOut']);
    });

    test('treats "not clocked in" as information and shows the latest state', () async {
      final Harness harness = await ready(initial: AttendanceState.clockedIn);
      harness.api.onClockOut = () async =>
          throw const ApiException(code: 'NOT_CLOCKED_IN', message: 'none open', statusCode: 409);
      harness.api.onUserStatus = () async => snapshot(AttendanceState.completed);

      await actions(harness).clockOut();

      expect(actionState(harness).failure, AttendanceActionFailure.alreadyDone);
      expect(expectType<StartupReady>(screen(harness)).attendance.state, AttendanceState.completed);
    });
  });

  test('a response arriving after the app moved on does not put attendance back', () async {
    // If startup has since found the device revoked, a late clock-in response
    // must not reappear on top of that.
    final Harness harness = await ready();
    final StartupController startup = harness.container.read(startupControllerProvider.notifier);

    harness.api.onValidateLocation = () async =>
        throw const ApiException(code: 'LOCATION_NOT_ALLOWED', message: 'not here', statusCode: 403);
    await startup.restart();
    expectType<StartupBlocked>(screen(harness));

    startup.showAttendance(snapshot(AttendanceState.clockedIn));

    expectType<StartupBlocked>(screen(harness));
  });
}
