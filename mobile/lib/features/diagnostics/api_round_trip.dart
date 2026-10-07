import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/config/api_environment.dart';
import '../../core/network/api_exception.dart';
import '../../core/network/api_models.dart';
import '../../core/network/attendance_api_client.dart';
import '../../core/network/request_signer.dart';
import '../../core/security/device_key_service.dart';
import '../../core/storage/secure_store.dart';

/// Drives a real registration round trip against a running API.
///
/// **Why this exists.** The client and the server each implement RFC 9421, and
/// each is tested against a byte sequence written down by hand. That proves both
/// match the specification as I read it — it does not prove they match *each
/// other* over a real connection, where the `Host` header, the transmitted body
/// and the path are produced by the transport rather than by a test.
///
/// The decisive step is registration. The server's handler checks proof of
/// possession *before* it looks at the platform attestation, so:
///
/// * `ATTESTATION_REJECTED` means the signature verified. The two
///   implementations agree, and the request failed on the next check.
/// * `UNAUTHORIZED` means the signature did not verify — the signature bases
///   differ, which is the failure this page exists to catch.
///
/// **What it cannot show.** On an emulator the attestation is software-backed
/// and roots to a test key, so it will be refused whatever the configuration.
/// That refusal is the control working, not evidence that a genuine handset
/// would be accepted; only a physical device can show that (CON-06).
class ApiRoundTripPage extends StatefulWidget {
  /// Creates the page.
  const ApiRoundTripPage({
    required this.keyService,
    required this.secureStore,
    super.key,
  });

  /// The hardware key under test.
  final DeviceKeyService keyService;

  /// Where a successful registration would record the device identifier.
  final SecureStore secureStore;

  @override
  State<ApiRoundTripPage> createState() => _ApiRoundTripPageState();
}

class _ApiRoundTripPageState extends State<ApiRoundTripPage> {
  final List<_Step> _steps = <_Step>[];
  bool _running = false;

  final TextEditingController _userId = TextEditingController();
  final TextEditingController _password = TextEditingController();
  final TextEditingController _code = TextEditingController();

  @override
  void dispose() {
    _userId.dispose();

    // Cleared as well as disposed. It holds a password, and §24 is explicit that
    // it must not linger in application state any longer than the call needs it.
    _password
      ..clear()
      ..dispose();

    _code.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('API round trip')),
      body: Column(
        children: <Widget>[
          Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                TextField(
                  controller: _userId,
                  decoration: const InputDecoration(labelText: 'User identifier'),
                  autocorrect: false,
                  enableSuggestions: false,
                ),
                TextField(
                  controller: _password,
                  decoration: const InputDecoration(labelText: 'Password'),
                  obscureText: true,
                  autocorrect: false,
                  enableSuggestions: false,
                ),
                TextField(
                  controller: _code,
                  decoration: const InputDecoration(labelText: 'Authenticator code'),
                  keyboardType: TextInputType.number,
                  maxLength: 6,
                ),
                const SizedBox(height: 8),
                FilledButton(
                  onPressed: _running ? null : _run,
                  child: Text(_running ? 'Running…' : 'Run round trip'),
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

  Future<void> _run() async {
    setState(() {
      _running = true;
      _steps.clear();
    });

    try {
      final ApiEnvironment environment = ApiEnvironment.fromCompileTimeConfiguration();

      _record('Configured', true, '${environment.baseUri} (authority ${environment.authority})');

      final AttendanceApiClient client = AttendanceApiClient(
        environment: environment,
        signer: RequestSigner(widget.keyService),
        keyId: widget.secureStore.readDevicePublicId,
        appVersion: '1.0.0+1',
      );

      await _configurationAsync(client);
      await _locationAsync(client);
      await _registerAsync(client);
    } on StateError catch (error) {
      _record('Configuration refused', false, error.message);
    } on Object catch (error) {
      _record('Failed unexpectedly', false, error.toString());
    } finally {
      if (mounted) {
        setState(() => _running = false);
      }
    }
  }

  Future<void> _configurationAsync(AttendanceApiClient client) async {
    try {
      final MobileConfiguration config = await client.getConfigurationAsync();

      _record(
        'Read configuration',
        true,
        '${config.settings.length} settings, clock-in configured: '
        '${config.clockInConfigured}, server time ${config.serverTimeUtc.toIso8601String()}',
      );
    } on ApiException catch (error) {
      _record('Read configuration', false, _describe(error));
    }
  }

  Future<void> _locationAsync(AttendanceApiClient client) async {
    // Fixed coordinates, not the handset's GPS. This page is about the wire
    // protocol; feeding it a real fix would add a permission prompt and a second
    // reason for it to fail, which is exactly what a diagnostic must not have.
    const ReportedPosition position = ReportedPosition(
      latitude: 6.465422,
      longitude: 3.406448,
      accuracyMeters: 4,
      isMocked: false,
    );

    try {
      final LocationValidation result = await client.validateLocationAsync(position);

      _record(
        'Validated location',
        result.success,
        'accepted, office ${result.officeLocationId ?? "not disclosed to an unregistered device"}',
      );
    } on ApiException catch (error) {
      // A refusal here is still a successful round trip: the server answered.
      _record(
        'Validated location',
        error.isTransport ? false : true,
        _describe(error),
      );
    }
  }

  Future<void> _registerAsync(AttendanceApiClient client) async {
    final RegistrationChallenge challenge;

    try {
      challenge = await client.requestRegistrationChallengeAsync();

      _record(
        'Obtained challenge',
        true,
        '${challenge.challenge.length} bytes, expires ${challenge.expiresUtc.toIso8601String()}',
      );
    } on ApiException catch (error) {
      _record('Obtained challenge', false, _describe(error));
      return;
    }

    final DeviceKeyAttestation attestation;

    try {
      attestation = await widget.keyService.generateKey(challenge.challenge);

      _record(
        'Generated key bound to the challenge',
        attestation.publicKey.length == 65,
        'public key ${attestation.publicKey.length} bytes, '
        'attestation ${attestation.attestation.length} bytes',
      );

      _dumpAttestation(attestation.attestation);
    } on DeviceKeyException catch (error) {
      _record('Generated key', false, '${error.code}: ${error.message}');
      return;
    }

    try {
      final DeviceRegistration registration = await client.registerDeviceAsync(
        userId: _userId.text.trim(),
        password: _password.text,
        authenticatorCode: _code.text.trim(),
        challenge: challenge,
        publicKey: attestation.publicKey,
        attestation: attestation.attestation,
        platform: DevicePlatformCode.android,
        attestationKeyId: attestation.keyId,
        deviceModel: 'android-emulator',
        osVersion: 'debug',
      );

      if (registration.deviceId case final String deviceId) {
        await widget.secureStore.writeDevicePublicId(deviceId);
        await widget.secureStore.writeUserId(_userId.text.trim());
      }

      _record(
        'Registered',
        true,
        registration.requiresApproval
            ? 'accepted, awaiting administrator approval — device ${registration.deviceId}'
            : 'accepted and active — device ${registration.deviceId}',
      );
    } on ApiException catch (error) {
      _record('Registered', false, _describe(error));
      _record(_verdictFor(error), error.code == 'ATTESTATION_REJECTED', _verdictDetail(error));
    } finally {
      // Whatever happened, the password does not stay in a text field.
      _password.clear();
    }
  }

  /// Writes the attestation chain to the log so it can be examined offline.
  ///
  /// **Why this is worth having.** When a particular handset model is refused in
  /// the field, the only way to find out why is to look at what its platform
  /// actually produced — manufacturers differ in the security level they claim,
  /// the root they chain to, and whether they populate the root-of-trust fields
  /// at all. Without the chain in hand the investigation is guesswork.
  ///
  /// **It is not a secret.** An attestation is a chain of X.509 certificates and
  /// a public key; it contains no private key material and nothing about the
  /// employee. It is evidence about the device, which is exactly why the server
  /// is willing to be shown it by an unauthenticated caller.
  ///
  /// Chunked deliberately: `debugPrint` throttles and re-wraps long lines, which
  /// interleaves them unpredictably. Numbered fixed-width pieces reassemble
  /// reliably no matter what order they arrive in.
  static void _dumpAttestation(Uint8List attestation) {
    final String encoded = base64.encode(attestation);
    const int chunk = 400;
    final int count = (encoded.length / chunk).ceil();

    debugPrint('[attestation] begin $count chunks, ${attestation.length} bytes');

    for (int i = 0; i < count; i++) {
      final int start = i * chunk;
      final int end = start + chunk > encoded.length ? encoded.length : start + chunk;

      debugPrint('[attestation] $i ${encoded.substring(start, end)}');
    }

    debugPrint('[attestation] end');
  }

  /// Turns the registration outcome into the answer this page exists to give.
  static String _verdictFor(ApiException error) => switch (error.code) {
        'ATTESTATION_REJECTED' => 'Signature verified by the server',
        'UNAUTHORIZED' => 'SIGNATURE MISMATCH',
        _ => 'Inconclusive',
      };

  static String _verdictDetail(ApiException error) => switch (error.code) {
        // Proof of possession is checked before the attestation, so getting this
        // far means the server rebuilt the same signature base and the signature
        // verified against the presented key.
        'ATTESTATION_REJECTED' =>
          'Reached the attestation check, which only happens after proof of '
              'possession succeeds. On an emulator the attestation is '
              'software-backed and is refused by design.',
        'UNAUTHORIZED' =>
          'The server rebuilt a different signature base, or the key did not '
              'match. Compare the client base against the server log.',
        'INVALID_CREDENTIALS' =>
          'Stopped at the password or authenticator code, before the signature '
              'was checked. Correct them and run again.',
        _ => 'Did not reach the signature check.',
      };

  static String _describe(ApiException error) =>
      '${error.code} (${error.statusCode ?? "no response"}) — ${error.message}'
      '${error.correlationId == null ? "" : "\ncorrelation ${error.correlationId}"}';

  void _record(String name, bool ok, String detail) {
    debugPrint('[round-trip] ${ok ? "PASS" : "FAIL"}  $name — $detail');

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
