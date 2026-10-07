import 'package:clockinxtra/core/device/device_integrity_service.dart';
import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:clockinxtra/core/storage/secure_store.dart';
import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// The platform services, on a real device or emulator.
///
/// Every widget and unit test replaces these with fakes, because they are
/// method channels into Kotlin (and, later, Swift) code that only exists on a
/// device. That leaves exactly the code most likely to break unnoticed — a
/// renamed channel, a changed return type, a Keystore behaviour that differs
/// between OS versions — untested. These run it for real.
///
/// **Destructive to local app state.** They clear the secure store and replace
/// the device signing key, as a reinstall would. Run them on a test device or
/// emulator, never on a phone someone clocks in with. Run with:
///
///     flutter test integration_test -d <device-id>
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('secure store (Android Keystore / iOS Keychain)', () {
    final SecureStore store = PlatformSecureStore();

    tearDown(store.clear);

    testWidgets('keeps the user and device identifiers, and forgets them on clear', (WidgetTester tester) async {
      await store.clear();
      expect(await store.readUserId(), isNull);

      await store.writeUserId('itest.employee');
      await store.writeDevicePublicId('3f2504e0-4f89-11d3-9a0c-0305e82c3301');

      expect(await store.readUserId(), 'itest.employee');
      expect(await store.readDevicePublicId(), '3f2504e0-4f89-11d3-9a0c-0305e82c3301');

      await store.clear();

      expect(await store.readUserId(), isNull);
      expect(await store.readDevicePublicId(), isNull);
    });
  });

  group('device signing key', () {
    const DeviceKeyService keys = PlatformDeviceKeyService();

    tearDown(() async {
      if (await keys.hasKey()) {
        await keys.deleteKey();
      }
    });

    testWidgets('generates an attested P-256 key and signs with it', (WidgetTester tester) async {
      if (await keys.hasKey()) {
        await keys.deleteKey();
      }

      final Uint8List challenge = Uint8List.fromList(List<int>.generate(32, (int i) => i));
      final DeviceKeyAttestation generated = await keys.generateKey(challenge);

      // The shapes the server's parser depends on: an uncompressed point, and
      // on Android a DER certificate chain (a SEQUENCE, 0x30).
      expect(generated.publicKey.length, 65);
      expect(generated.publicKey.first, 0x04);
      expect(generated.attestation, isNotEmpty);
      if (defaultTargetPlatform == TargetPlatform.android) {
        expect(generated.attestation.first, 0x30);
        expect(generated.keyId, isEmpty);
      }

      expect(await keys.hasKey(), isTrue);

      // Raw r‖s, as RFC 9421's ecdsa-p256-sha256 requires — not DER. The
      // server verifies these bytes; its tests hold a golden sample captured
      // from this very code path on an emulator.
      final Uint8List data = Uint8List.fromList('@method: POST'.codeUnits);
      final Uint8List first = await keys.sign(data);
      final Uint8List second = await keys.sign(data);

      expect(first.length, 64);
      expect(second.length, 64);

      // ECDSA signing is randomised: two signatures over the same input
      // differing is evidence a real signer ran, not a stub returning a constant.
      expect(first, isNot(equals(second)));
    });

    testWidgets('a deleted key cannot sign', (WidgetTester tester) async {
      await keys.generateKey(Uint8List(32));
      await keys.deleteKey();

      expect(await keys.hasKey(), isFalse);
      await expectLater(
        keys.sign(Uint8List.fromList(<int>[1, 2, 3])),
        throwsA(isA<DeviceKeyException>()),
      );
    });
  });

  group('integrity check', () {
    testWidgets('the native check answers and reports what it finds', (WidgetTester tester) async {
      // Without the development define, exactly as a release build behaves.
      const DeviceIntegrityService integrity = PlatformDeviceIntegrityService();

      final IntegrityReport report = await integrity.inspect();

      // The channel exists and ran: an unavailable report would mean the Kotlin
      // handler is missing or renamed, which the app treats as untrusted.
      expect(report.checked, isTrue, reason: report.unavailableReason);

      final bool isPhysical = defaultTargetPlatform == TargetPlatform.android
          ? (await DeviceInfoPlugin().androidInfo).isPhysicalDevice
          : (await DeviceInfoPlugin().iosInfo).isPhysicalDevice;

      if (!isPhysical) {
        expect(report.isEmulator, isTrue);

        // Whether root indicators appear depends on the system image, and each
        // must be reported for what it is. AOSP and "Google APIs" images are
        // userdebug builds with su (SU_BINARY_PRESENT, NON_PRODUCTION_OS_BUILD);
        // "Google Play" images are user/release-keys builds without su, and a
        // check that flagged them as rooted would be inventing evidence. An
        // emulator is refused regardless — by the server, whose attestation
        // check rejects a software-backed key (see the golden emulator sample in
        // AndroidKeyAttestationVerifierTests).
        if (defaultTargetPlatform == TargetPlatform.android) {
          final bool productionImage = (await DeviceInfoPlugin().androidInfo).type == 'user';
          if (productionImage) {
            expect(report.indicators, isNot(contains('NON_PRODUCTION_OS_BUILD')));
          } else {
            expect(report.indicators, contains('NON_PRODUCTION_OS_BUILD'));
            expect(report.isTrusted, isFalse);
          }
        }
      }
    });
  });
}
