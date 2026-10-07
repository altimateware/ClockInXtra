import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:clockinxtra/features/app_initialization/application/startup_controller.dart';
import 'package:clockinxtra/features/app_initialization/domain/startup_state.dart';
import 'package:clockinxtra/features/authentication/application/registration_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fakes.dart';

void main() {
  const String password = 'the employee password';

  Future<void> submit(Harness harness, {String userId = 'e.adeyemi', String code = '123456'}) =>
      harness.container
          .read(registrationControllerProvider.notifier)
          .submit(userId: userId, password: password, authenticatorCode: code);

  RegistrationState stateOf(Harness harness) => harness.container.read(registrationControllerProvider);

  test('sends nothing when the form is incomplete', () async {
    final Harness harness = Harness();

    await submit(harness, code: '12345');

    expect(stateOf(harness).failure, RegistrationFailure.incomplete);
    expect(harness.api.calls, isEmpty);
    expect(harness.keys.generated, 0);
  });

  test('registers, stores only the identifiers, and hands back to startup', () async {
    final Harness harness = Harness();

    harness.api.onDeviceState = () async => DeviceRegistrationState(
          status: 'PendingApproval',
          employeeActive: true,
          revokedReason: null,
          serverTimeUtc: DateTime.utc(2026, 9, 17),
        );

    // A pending device's signed location check is refused; the flow then asks
    // the status endpoint, which says it is waiting.
    harness.api.onValidateLocation = () async {
      throw const ApiException(code: 'DEVICE_NOT_APPROVED', message: 'waiting', statusCode: 403);
    };

    await submit(harness);

    expect(harness.api.calls.take(2), <String>['challenge', 'register']);
    expect(harness.keys.generated, 1);
    expect(harness.api.lastRegistrationPassword, password);

    expect(harness.store.devicePublicId, 'd0000000-0000-4000-8000-000000000001');
    expect(harness.store.userId, 'e.adeyemi');

    // §24: the password went to the request and nowhere else — not storage, not state.
    expect(<String?>[harness.store.devicePublicId, harness.store.userId], isNot(contains(password)));
    expect(stateOf(harness).failure, isNull);

    expectType<StartupAwaitingApproval>(harness.container.read(startupControllerProvider));
  });

  test('reports wrong credentials and stores nothing', () async {
    final Harness harness = Harness()
      ..api.onRegister = () async =>
          throw const ApiException(code: 'INVALID_CREDENTIALS', message: 'no', statusCode: 401);

    await submit(harness);

    expect(stateOf(harness).failure, RegistrationFailure.invalidCredentials);
    expect(harness.store.devicePublicId, isNull);
  });

  test('stops before registering when the handset cannot make a hardware key', () async {
    final Harness harness = Harness(keys: FakeKeys(failure: const DeviceKeyException('KEY_GENERATION_FAILED', 'no TEE')));

    await submit(harness);

    expect(stateOf(harness).failure, RegistrationFailure.hardwareKeyUnavailable);
    expect(harness.api.calls, isNot(contains('register')));
  });

  test('explains a refused attestation', () async {
    final Harness harness = Harness()
      ..api.onRegister = () async =>
          throw const ApiException(code: 'ATTESTATION_REJECTED', message: 'no', statusCode: 403);

    await submit(harness);

    expect(stateOf(harness).failure, RegistrationFailure.deviceNotVerified);
  });

  test('does not store a success that carries no device identifier', () async {
    // The app could sign nothing with it; half a registration is worse than none.
    final Harness harness = Harness()
      ..api.onRegister = () async => const DeviceRegistration(deviceId: null, requiresApproval: true);

    await submit(harness);

    expect(stateOf(harness).failure, RegistrationFailure.unexpected);
    expect(harness.store.devicePublicId, isNull);
  });

  test('reports an unreachable server', () async {
    final Harness harness = Harness()
      ..api.onChallenge = () async =>
          throw const ApiException(code: ApiException.networkTimeout, message: 'timeout');

    await submit(harness);

    expect(stateOf(harness).failure, RegistrationFailure.serviceUnreachable);
    expect(harness.keys.generated, 0);
  });
}
