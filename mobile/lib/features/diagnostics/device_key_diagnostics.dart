import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/network/request_signer.dart';
import '../../core/security/device_key_service.dart';

/// A developer screen that exercises the hardware key on a real device.
///
/// **Why this exists as a screen rather than a test.** Nothing about the secure
/// element can be verified on a workstation. Unit tests prove the signature base
/// is byte-correct and the Kotlin compiles, but neither shows whether *this*
/// handset's Keystore will accept the key parameters the design requires —
/// `setUnlockedDeviceRequired` with StrongBox is refused at runtime on some
/// devices, and the attestation chain differs between manufacturers. That is only
/// learnable by asking the hardware.
///
/// It is excluded from release builds by the caller, not by this file.
class DeviceKeyDiagnosticsPage extends StatefulWidget {
  /// Creates the page.
  const DeviceKeyDiagnosticsPage({required this.keyService, super.key});

  /// The service under examination.
  final DeviceKeyService keyService;

  @override
  State<DeviceKeyDiagnosticsPage> createState() => _DeviceKeyDiagnosticsPageState();
}

class _DeviceKeyDiagnosticsPageState extends State<DeviceKeyDiagnosticsPage> {
  final List<_Step> _steps = <_Step>[];
  bool _running = false;

  @override
  void initState() {
    super.initState();

    // Reports what is already here. It does NOT generate anything.
    //
    // It used to call _run() straight away, which generates a key — and
    // generating replaces the registered one, because the private key cannot
    // leave the secure element and a second cannot be kept beside it. So merely
    // opening this tab silently un-registered the handset, and the damage was
    // not recoverable from the phone: every later request failed
    // SIGNATURE_INVALID, and registering again is refused with
    // ACTIVE_DEVICE_EXISTS until an administrator revokes the device. A
    // diagnostic that breaks the thing it is diagnosing, with no prompt, is a
    // trap however loudly the screen is labelled debug.
    WidgetsBinding.instance.addPostFrameCallback((_) => _inspect());
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Device key diagnostics')),
      body: Column(
        children: <Widget>[
          Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                const Text(
                  'Generates a hardware-backed P-256 key, reads its attestation, '
                  'and signs with it. Everything here depends on this handset, '
                  'not on the code.',
                ),
                const SizedBox(height: 12),
                FilledButton(
                  onPressed: _running ? null : _run,
                  child: Text(_running ? 'Running…' : 'Run checks'),
                ),
              ],
            ),
          ),
          const Divider(height: 1),
          Expanded(
            child: ListView.separated(
              itemCount: _steps.length,
              separatorBuilder: (_, _) => const Divider(height: 1),
              itemBuilder: (BuildContext context, int index) {
                final _Step step = _steps[index];

                return ListTile(
                  leading: Icon(
                    step.ok ? Icons.check_circle : Icons.error,
                    color: step.ok ? Colors.green : Colors.red,
                  ),
                  title: Text(step.name),
                  subtitle: Text(step.detail),
                  isThreeLine: step.detail.length > 48,
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  /// Reports the current key without touching it.
  Future<void> _inspect() async {
    final bool existed = await widget.keyService.hasKey();

    if (!mounted) {
      return;
    }

    setState(() {
      _steps
        ..clear()
        ..add(_Step(
          'Key already present',
          true,
          existed
              ? 'yes — running the checks below REPLACES it and un-registers this phone'
              : 'no — running the checks below is safe',
        ));
    });
  }

  Future<void> _run() async {
    // Destroying a registered key is a decision, so it is asked for once and in
    // the words of its consequence, not hidden behind "Run checks".
    if (await widget.keyService.hasKey()) {
      if (!mounted) {
        return;
      }

      final bool confirmed = await showDialog<bool>(
            context: context,
            builder: (BuildContext context) => AlertDialog(
              title: const Text('Replace the registered key?'),
              content: const Text(
                'This phone holds a device key. Running the checks generates a new one, '
                'which destroys it: the phone stops being able to sign requests, and '
                'registering again is refused until an administrator revokes this device.',
              ),
              actions: <Widget>[
                TextButton(
                  onPressed: () => Navigator.of(context).pop(false),
                  child: const Text('Cancel'),
                ),
                FilledButton(
                  onPressed: () => Navigator.of(context).pop(true),
                  child: const Text('Replace the key'),
                ),
              ],
            ),
          ) ??
          false;

      if (!confirmed) {
        return;
      }
    }

    if (!mounted) {
      return;
    }

    setState(() {
      _running = true;
      _steps.clear();
    });

    // A server-issued challenge would normally arrive from the API. Here it is
    // local, because the point is the hardware rather than the round trip.
    final Uint8List challenge = Uint8List.fromList(
      List<int>.generate(32, (_) => Random.secure().nextInt(256)),
    );

    try {
      final bool existed = await widget.keyService.hasKey();
      _record('Key already present', true, existed ? 'yes — it will be replaced' : 'no');

      final DeviceKeyAttestation attestation =
          await widget.keyService.generateKey(challenge);

      _record(
        'Generated hardware key',
        attestation.publicKey.length == 65 && attestation.publicKey.first == 0x04,
        'public key ${attestation.publicKey.length} bytes, '
        'first byte 0x${attestation.publicKey.first.toRadixString(16)} '
        '(expected 65 and 0x04)',
      );

      _record(
        'Attestation returned',
        attestation.attestation.isNotEmpty,
        '${attestation.attestation.length} bytes — this is what the server '
        'verifies against Google\'s root',
      );

      // The real test of the signature path: the bytes must come back as raw
      // r‖s of exactly 64 bytes, not DER.
      final Uint8List signature = await widget.keyService.sign(
        Uint8List.fromList(<int>[for (int i = 0; i < 100; i++) i]),
      );

      _record(
        'Signed with the key',
        signature.length == 64,
        '${signature.length} bytes (RFC 9421 requires 64, raw r‖s)',
      );

      // And the signer must be able to drive it end to end.
      final RequestSigner signer = RequestSigner(widget.keyService);

      final SignedHeaders headers = await signer.sign(
        method: 'POST',
        authority: 'attendance.example.com',
        path: '/api/v1/mobile/user/status',
        keyId: RequestSigner.unregisteredKeyId,
      );

      _record(
        'Built a signed request',
        headers.signature.startsWith('sig1=:'),
        headers.signatureInput,
      );
    } on DeviceKeyException catch (error) {
      // A refusal here is the finding, not a crash. StrongBox or
      // unlocked-device-required can be rejected by a particular handset.
      _record('Failed', false, '${error.code}: ${error.message}');
    } on Object catch (error) {
      _record('Failed unexpectedly', false, error.toString());
    } finally {
      if (mounted) {
        setState(() => _running = false);
      }
    }
  }

  void _record(String name, bool ok, String detail) {
    // Printed as well as shown: the console is where a developer running this on
    // a handset over a cable actually reads it.
    debugPrint('[device-key] ${ok ? "PASS" : "FAIL"}  $name — $detail');

    if (!mounted) {
      return;
    }

    setState(() => _steps.add(_Step(name, ok, detail)));
  }
}

final class _Step {
  const _Step(this.name, this.ok, this.detail);

  final String name;
  final bool ok;
  final String detail;
}
