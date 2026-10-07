import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/device/app_identity.dart';
import '../../../core/di/providers.dart';
import '../../../core/location/location_service.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/network/api_models.dart';
import '../../../core/network/attendance_api.dart';
import '../../../core/utilities/uuid.dart';
import '../../app_initialization/application/startup_controller.dart';

/// Clock-in and clock-out (§12, §14).
final NotifierProvider<AttendanceActionController, AttendanceActionState> attendanceActionControllerProvider =
    NotifierProvider<AttendanceActionController, AttendanceActionState>(AttendanceActionController.new);

/// The identifier this phone was registered to, prefilled into the clock-in form.
///
/// A convenience only (§10): the server accepts clock-in only for the employee
/// the signing device is bound to, whatever is typed.
final FutureProvider<String?> storedUserIdProvider =
    FutureProvider<String?>((Ref ref) => ref.watch(secureStoreProvider).readUserId());

/// What an attendance action is doing, or why it did not happen.
@immutable
final class AttendanceActionState {
  /// Creates the state.
  const AttendanceActionState({this.step, this.failure, this.serverMessage, this.correlationId});

  /// The step in progress, or null when idle.
  final AttendanceActionStep? step;

  /// Why the last attempt did not happen, if it did not.
  final AttendanceActionFailure? failure;

  /// The server's own sentence, where one was given. Safe to show (§34).
  final String? serverMessage;

  /// Quoted to support, to find the request in the server log.
  final String? correlationId;

  /// Whether an action is running; the buttons are disabled meanwhile.
  bool get busy => step != null;
}

/// The steps of an attendance action.
enum AttendanceActionStep {
  /// Taking a fresh position fix.
  locating,

  /// The request is with the server.
  recording,
}

/// Why an attendance action did not happen.
enum AttendanceActionFailure {
  /// Password or code missing or malformed; nothing was sent.
  incomplete,

  /// Wrong password or code. One answer on purpose (CON-09).
  invalidCredentials,

  /// Too many wrong attempts.
  accountLocked,

  /// No authenticator is enrolled for this employee.
  authenticatorNotEnrolled,

  /// No position fix could be taken, or permission is missing.
  locationUnavailable,

  /// The server did not accept the position.
  locationRejected,

  /// Outside the permitted hours.
  windowClosed,

  /// The business hours have not been configured.
  notConfigured,

  /// **The outcome is not known.** The request may or may not have been
  /// recorded. The only correct next step is to ask the server, which the screen
  /// offers — never to show it as done, and never to resend blindly (§52).
  outcomeUnknown,

  /// This phone is registered to a different employee.
  wrongEmployee,

  /// Already in the requested state. The screen has been refreshed from the server.
  alreadyDone,

  /// Anything else the server refused.
  unexpected,
}

/// Records clock-in and clock-out.
///
/// **The server decides everything that matters.** The time recorded is the
/// server's (§14, §31), the location check is the server's, and the resulting
/// state shown afterwards is the server's response, not something this
/// controller works out.
///
/// **What happens when an answer is lost.** A timeout or a dropped connection
/// leaves the outcome genuinely unknown: the server may have recorded the
/// clock-in and the response simply never arrived. This controller never
/// resends in that situation. It reports [AttendanceActionFailure.outcomeUnknown]
/// and the screen offers to ask the server, which is the source of truth (§11).
///
/// Re-sending would not work anyway for clock-in, and the reason is worth
/// recording. The server verifies the password and consumes the authenticator
/// code's time step *before* it looks at the idempotency key, so a byte-identical
/// resend after a success is refused as a reused code, and a resend with a fresh
/// code is a different body, which the server refuses as a reused key. Asking
/// for the status instead needs neither the password nor the code a second time,
/// so neither is held a moment longer than the one request (§24). Duplicate
/// records are prevented regardless: the database allows one open attendance
/// record per employee per day, and a second attempt is refused with
/// `ALREADY_CLOCKED_IN` (§28, §53).
///
/// Each attempt gets its own idempotency key, which protects the server against
/// the same request being delivered twice by anything between here and there.
final class AttendanceActionController extends Notifier<AttendanceActionState> {
  static final RegExp _sixDigits = RegExp(r'^[0-9]{6}$');

  @override
  AttendanceActionState build() => const AttendanceActionState();

  /// Clocks in (§12).
  Future<void> clockIn({required String userId, required String password, required String authenticatorCode}) async {
    if (state.busy) {
      return;
    }

    final String trimmedUserId = userId.trim();
    final String code = authenticatorCode.trim();

    if (trimmedUserId.isEmpty || password.isEmpty || password.length > 256 || !_sixDigits.hasMatch(code)) {
      state = const AttendanceActionState(failure: AttendanceActionFailure.incomplete);
      return;
    }

    await _perform(
      (AttendanceApi api, ReportedPosition position) => api.clockInAsync(
        userId: trimmedUserId,
        password: password,
        authenticatorCode: code,
        position: position,
        idempotencyKey: newUuidV4(),
      ),
    );
  }

  /// Clocks out (§14). No password or code, per ASM-04.
  Future<void> clockOut() async {
    if (state.busy) {
      return;
    }

    await _perform(
      (AttendanceApi api, ReportedPosition position) => api.clockOutAsync(
        position: position,
        idempotencyKey: newUuidV4(),
      ),
    );
  }

  /// Asks the server for the current state, clearing any message about an
  /// earlier attempt first — after an unknown outcome in particular, the answer
  /// replaces the uncertainty rather than sitting beneath it.
  Future<void> checkWithServer() async {
    if (state.busy) {
      return;
    }

    state = const AttendanceActionState();
    await ref.read(startupControllerProvider.notifier).restart();
  }

  /// Clears a shown failure, for example after the employee starts typing again.
  void dismissFailure() {
    if (!state.busy && state.failure != null) {
      state = const AttendanceActionState();
    }
  }

  Future<void> _perform(
    Future<AttendanceSnapshot> Function(AttendanceApi api, ReportedPosition position) send,
  ) async {
    // 1. A fresh fix, taken now. The one from startup could be minutes old, and
    //    the question is where the employee is at the moment of clocking (§12).
    state = const AttendanceActionState(step: AttendanceActionStep.locating);

    final DevicePosition position;

    try {
      position = await ref.read(locationServiceProvider).currentPosition();
    } on LocationUnavailableException catch (error) {
      debugPrint('[attendance] no fix: ${error.message}');
      state = const AttendanceActionState(failure: AttendanceActionFailure.locationUnavailable);
      return;
    }

    // 2. The request.
    state = const AttendanceActionState(step: AttendanceActionStep.recording);

    final AttendanceApi api;

    try {
      final AppIdentity identity = await ref.read(appIdentityLoaderProvider)();
      api = ref.read(attendanceApiFactoryProvider)(identity);
    } on Object catch (error) {
      debugPrint('[attendance] cannot build the client: $error');
      state = const AttendanceActionState(failure: AttendanceActionFailure.unexpected);
      return;
    }

    try {
      final AttendanceSnapshot result = await send(
        api,
        ReportedPosition(
          latitude: position.latitude,
          longitude: position.longitude,
          accuracyMeters: position.accuracyMeters,
          isMocked: position.isMocked,
        ),
      );

      state = const AttendanceActionState();
      ref.read(startupControllerProvider.notifier).showAttendance(result);
    } on ApiException catch (error) {
      debugPrint('[attendance] refused: ${error.code} (${error.correlationId})');
      await _handleRefusal(error);
    }
  }

  Future<void> _handleRefusal(ApiException error) async {
    if (error.isTransport || error.code == 'REQUEST_IN_PROGRESS') {
      state = AttendanceActionState(
        failure: AttendanceActionFailure.outcomeUnknown,
        correlationId: error.correlationId,
      );
      return;
    }

    switch (error.code) {
      // The employee is already where they asked to go — clocked in on another
      // attempt, or out already. Whatever the screen showed was out of date, so
      // the server is asked again.
      case 'ALREADY_CLOCKED_IN':
      case 'NOT_CLOCKED_IN':
        state = AttendanceActionState(failure: AttendanceActionFailure.alreadyDone, serverMessage: error.message);
        await ref.read(startupControllerProvider.notifier).restart();
        return;

      // Something about the device changed since startup. The startup sequence
      // is what knows how to explain that.
      case 'DEVICE_NOT_APPROVED':
      case 'DEVICE_REVOKED':
      case 'DEVICE_NOT_REGISTERED':
        state = const AttendanceActionState();
        await ref.read(startupControllerProvider.notifier).restart();
        return;
    }

    final AttendanceActionFailure failure = switch (error.code) {
      'INVALID_CREDENTIALS' => AttendanceActionFailure.invalidCredentials,
      'ACCOUNT_LOCKED' => AttendanceActionFailure.accountLocked,
      'MFA_NOT_ENROLLED' => AttendanceActionFailure.authenticatorNotEnrolled,
      'LOCATION_NOT_ALLOWED' ||
      'LOCATION_ACCURACY_INSUFFICIENT' ||
      'LOCATION_SOURCE_UNTRUSTED' =>
        AttendanceActionFailure.locationRejected,
      'ATTENDANCE_WINDOW_CLOSED' => AttendanceActionFailure.windowClosed,
      'ATTENDANCE_NOT_CONFIGURED' => AttendanceActionFailure.notConfigured,
      // The user ID in the request is not the employee this phone is registered
      // to. The server compares the two; the app only reports the refusal.
      'FORBIDDEN' => AttendanceActionFailure.wrongEmployee,
      _ => AttendanceActionFailure.unexpected,
    };

    state = AttendanceActionState(
      failure: failure,
      serverMessage: error.message,
      correlationId: error.correlationId,
    );
  }
}
