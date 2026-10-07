import 'dart:typed_data';

import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:clockinxtra/features/diagnostics/device_key_diagnostics.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Stands in for the platform channel, which does not exist in a widget test.
final class _StubKeyService implements DeviceKeyService {
  @override
  Future<bool> hasKey() async => false;

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async =>
      DeviceKeyAttestation(
        publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 1)]),
        attestation: Uint8List.fromList(List<int>.filled(128, 2)),
        keyId: Uint8List(0),
      );

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
}
