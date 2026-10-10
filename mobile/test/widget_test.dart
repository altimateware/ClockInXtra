import 'dart:typed_data';

import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:clockinxtra/features/diagnostics/device_key_diagnostics.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Stands in for the platform channel, which does not exist in a widget test.
final class _StubKeyService implements DeviceKeyService {
  _StubKeyService({this.keyExists = false});

  /// Whether the handset already holds a registered key.
  final bool keyExists;

  /// How many times a key was generated — which is how many times a registered
  /// device would have been un-registered.
  int generated = 0;

  @override
  Future<bool> hasKey() async => keyExists;

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async {
    generated++;

    return DeviceKeyAttestation(
        publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 1)]),
        attestation: Uint8List.fromList(List<int>.filled(128, 2)),
        keyId: Uint8List(0),
      );
  }

  @override
  Future<Uint8List> sign(Uint8List data) async =>
      Uint8List.fromList(List<int>.filled(64, 3));

  @override
  Future<void> deleteKey() async {}
}

void main() {
  testWidgets('diagnostics page reports each hardware check', (WidgetTester tester) async {
    await tester.pumpWidget(
      MaterialApp(home: DeviceKeyDiagnosticsPage(keyService: _StubKeyService())),
    );

    expect(find.text('Run checks'), findsOneWidget);

    await tester.tap(find.text('Run checks'));
    await tester.pumpAndSettle();

    // Each step is reported rather than collapsed into one pass or fail: on a
    // real handset it matters *which* step the Keystore refused.
    expect(find.text('Generated hardware key'), findsOneWidget);
    expect(find.text('Attestation returned'), findsOneWidget);
    expect(find.text('Signed with the key'), findsOneWidget);
    expect(find.text('Built a signed request'), findsOneWidget);
  });

  testWidgets('opening the page generates nothing', (WidgetTester tester) async {
    // It used to generate on the first frame. Generating replaces the key in
    // the secure element, so opening this tab un-registered the handset: every
    // later request failed SIGNATURE_INVALID, and registering again was refused
    // until an administrator revoked the device.
    final _StubKeyService keys = _StubKeyService(keyExists: true);

    await tester.pumpWidget(MaterialApp(home: DeviceKeyDiagnosticsPage(keyService: keys)));
    await tester.pumpAndSettle();

    expect(keys.generated, 0);

    // Nothing is even proposed. Asserted as well as the count above, because
    // the confirmation dialog alone would keep the count at zero — so without
    // this the test would still pass with the automatic run restored, and
    // opening a read-only tab would ambush whoever opened it with a dialog
    // about destroying their registration.
    expect(find.text('Replace the registered key?'), findsNothing);
    expect(find.textContaining('REPLACES it'), findsOneWidget);
  });

  testWidgets('a registered key is not replaced without being asked', (WidgetTester tester) async {
    final _StubKeyService keys = _StubKeyService(keyExists: true);

    await tester.pumpWidget(MaterialApp(home: DeviceKeyDiagnosticsPage(keyService: keys)));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Run checks'));
    await tester.pumpAndSettle();

    expect(find.text('Replace the registered key?'), findsOneWidget);

    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(keys.generated, 0);
  });
}
