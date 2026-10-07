import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/device/app_identity.dart';
import '../../../core/di/providers.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/network/api_models.dart';
import '../../../core/network/attendance_api.dart';
import '../../../core/security/device_key_service.dart';
import '../../../core/storage/secure_store.dart';
import '../../app_initialization/application/startup_controller.dart';

/// Registers this device against an employee.
final NotifierProvider<RegistrationController, RegistrationState> registrationControllerProvider =
    NotifierProvider<RegistrationController, RegistrationState>(RegistrationController.new);

/// What the registration form should show.
@immutable
final class RegistrationState {
  /// Creates the state.
  const RegistrationState({this.submitting = false, this.failure});

  /// A request is in flight; the form is disabled.
  final bool submitting;

  /// Why the last attempt failed, if it did.
  final RegistrationFailure? failure;
}

/// Why registration failed — one entry per sentence the employee needs to see.
enum RegistrationFailure {
  /// Fields missing or malformed; nothing was sent.
  incomplete,

  /// Wrong identifier, password or code. Deliberately one answer (CON-09).
  invalidCredentials,

  /// Too many wrong attempts.
  accountLocked,

  /// No authenticator is set up for this employee yet.
  authenticatorNotEnrolled,

  /// The employee already has an active device; a replacement needs approval.
  activeDeviceExists,

  /// The platform attestation was refused: not genuine secure hardware.
  deviceNotVerified,

  /// This handset could not create a hardware-backed key.
  hardwareKeyUnavailable,

  /// The challenge expired while the form was open.
  expired,

  /// The server could not be reached.
  serviceUnreachable,

  /// Anything else.
  unexpected,
}

/// The registration flow (§6.3, §18).
///
/// **What registration proves, in order:** the employee knows their password
/// and holds their authenticator; this handset created a private key inside
/// secure hardware, bound to a challenge the server just issued; and the
/// request was signed with that key. None of it proves the person holding the
/// phone is the employee — that is what the administrator's approval is for
/// (DEC-04), so success usually ends at "waiting for approval".
///
/// **The password and code are not kept.** They are passed straight to the
/// request and go out of scope with this method. Nothing here writes them to a
/// field, the state, storage or a log (§24).
final class RegistrationController extends Notifier<RegistrationState> {
  static final RegExp _sixDigits = RegExp(r'^[0-9]{6}$');

  @override
  RegistrationState build() => const RegistrationState();

  /// Registers, then hands back to the startup sequence.
  Future<void> submit({
    required String userId,
    required String password,
    required String authenticatorCode,
  }) async {
    if (state.submitting) {
      return;
    }

    final String trimmedUserId = userId.trim();
    final String code = authenticatorCode.trim();

    if (trimmedUserId.isEmpty ||
        trimmedUserId.length > 64 ||
        password.isEmpty ||
        password.length > 256 ||
        !_sixDigits.hasMatch(code)) {
      state = const RegistrationState(failure: RegistrationFailure.incomplete);
      return;
    }

    state = const RegistrationState(submitting: true);

    final RegistrationFailure? failure = await _register(trimmedUserId, password, code);

    if (failure != null) {
      state = RegistrationState(failure: failure);
      return;
    }

    state = const RegistrationState();

    // Registration succeeded, so the startup sequence decides what comes next:
    // almost always "waiting for approval", and the server — not this
    // controller — is what says so.
    await ref.read(startupControllerProvider.notifier).restart();
  }

  Future<RegistrationFailure?> _register(String userId, String password, String code) async {
    final AppIdentity identity;
    final AttendanceApi api;

    try {
      identity = await ref.read(appIdentityLoaderProvider)();
      api = ref.read(attendanceApiFactoryProvider)(identity);
    } on Object catch (error) {
      debugPrint('[registration] cannot build the client: $error');
      return RegistrationFailure.unexpected;
    }

    try {
      final RegistrationChallenge challenge = await api.requestRegistrationChallengeAsync();

      final DeviceKeyAttestation key;

      try {
        key = await ref.read(deviceKeyServiceProvider).generateKey(challenge.challenge);
      } on DeviceKeyException catch (error) {
        debugPrint('[registration] hardware key refused: ${error.code}');
        return RegistrationFailure.hardwareKeyUnavailable;
      }

      final DeviceRegistration registration = await api.registerDeviceAsync(
        userId: userId,
        password: password,
        authenticatorCode: code,
        challenge: challenge,
        publicKey: key.publicKey,
        attestation: key.attestation,
        attestationKeyId: key.keyId,
        platform: identity.platform,
        deviceModel: identity.deviceModel,
        osVersion: identity.osVersion,
      );

      if (registration.deviceId case final String deviceId when deviceId.isNotEmpty) {
        final SecureStore store = ref.read(secureStoreProvider);
        await store.writeDevicePublicId(deviceId);
        await store.writeUserId(userId);
        return null;
      }

      // A success without an identifier would leave the app unable to sign
      // anything. Treated as a failure rather than stored half-done.
      return RegistrationFailure.unexpected;
    } on ApiException catch (error) {
      debugPrint('[registration] refused: ${error.code} (${error.correlationId})');

      if (error.isTransport) {
        return RegistrationFailure.serviceUnreachable;
      }

      return switch (error.code) {
        'INVALID_CREDENTIALS' => RegistrationFailure.invalidCredentials,
        'ACCOUNT_LOCKED' => RegistrationFailure.accountLocked,
        'MFA_NOT_ENROLLED' => RegistrationFailure.authenticatorNotEnrolled,
        'ACTIVE_DEVICE_EXISTS' => RegistrationFailure.activeDeviceExists,
        'ATTESTATION_REJECTED' => RegistrationFailure.deviceNotVerified,
        'INVALID_REQUEST' => RegistrationFailure.expired,
        _ => RegistrationFailure.unexpected,
      };
    }
  }
}
