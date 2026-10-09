import 'package:flutter/services.dart';

/// Access to the device's hardware-backed signing key.
///
/// This is the credential the whole mobile security design rests on. The private
/// key is generated **inside** the device's secure hardware — the Android
/// Keystore's TEE or StrongBox, or the iOS Secure Enclave — and cannot be
/// exported. The application can ask for a signature; it can never obtain the
/// key itself, so a compromised app, or a copy of its storage, does not yield
/// anything an attacker can sign with elsewhere.
///
/// There is no pub.dev package that does this. Generating a non-exportable
/// hardware key with an attestation challenge requires calling platform APIs
/// directly, so this is a method channel with native implementations. Using a
/// Dart cryptography package instead would produce a key living in ordinary
/// memory, which would look identical to the server and be worth nothing.
///
/// See `docs/architecture/03-solution-architecture.md` §6.1 for the key
/// parameters this contract requires.
abstract interface class DeviceKeyService {
  /// Whether a registration key already exists on this device.
  Future<bool> hasKey();

  /// Generates a new hardware-backed P-256 key bound to a server challenge.
  ///
  /// The challenge is embedded in the platform attestation, which is what stops
  /// a previously captured attestation being replayed. Generating a key
  /// discards any previous one, so the device must re-register afterwards.
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge);

  /// Signs bytes with the device key, returning raw `r‖s` (64 bytes).
  ///
  /// RFC 9421 §3.3.4 specifies that form for `ecdsa-p256-sha256`. Android
  /// produces DER and the native layer converts; iOS CryptoKit is already raw.
  Future<Uint8List> sign(Uint8List data);

  /// Removes the key, so the next start registers afresh.
  Future<void> deleteKey();
}

/// The material a freshly generated key produces.
final class DeviceKeyAttestation {
  /// Creates the attestation result.
  const DeviceKeyAttestation({
    required this.publicKey,
    required this.attestation,
    required this.keyId,
  });

  /// Uncompressed P-256 point: `0x04 ‖ X(32) ‖ Y(32)`, 65 bytes.
  final Uint8List publicKey;

  /// The platform attestation: an X.509 chain on Android, a CBOR object on iOS.
  final Uint8List attestation;

  /// Apple's key identifier. Empty on Android, which has no equivalent.
  final Uint8List keyId;
}

/// Raised when the platform refuses to produce or use a hardware key.
final class DeviceKeyException implements Exception {
  /// Creates the exception.
  const DeviceKeyException(this.code, this.message);

  /// A stable code the presentation layer can branch on.
  final String code;

  /// A description for logs. Never shown verbatim to an employee.
  final String message;

  @override
  String toString() => 'DeviceKeyException($code): $message';
}

/// Method channel implementation of [DeviceKeyService].
final class PlatformDeviceKeyService implements DeviceKeyService {
  /// Creates the service.
  const PlatformDeviceKeyService([
    this._channel = const MethodChannel('com.altimateware.clockinxtra/device_key'),
  ]);

  final MethodChannel _channel;

  @override
  Future<bool> hasKey() async {
    try {
      return await _channel.invokeMethod<bool>('hasKey') ?? false;
    } on PlatformException catch (error) {
      throw DeviceKeyException(error.code, error.message ?? 'hasKey failed');
    }
  }

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async {
    try {
      final Map<Object?, Object?>? result = await _channel
          .invokeMethod<Map<Object?, Object?>>('generateKey', <String, Object>{
        'challenge': challenge,
      });

      if (result == null) {
        throw const DeviceKeyException(
          'NO_RESULT',
          'The platform returned no key material.',
        );
      }

      return DeviceKeyAttestation(
        publicKey: result['publicKey']! as Uint8List,
        attestation: result['attestation']! as Uint8List,
        keyId: (result['keyId'] as Uint8List?) ?? Uint8List(0),
      );
    } on PlatformException catch (error) {
      throw DeviceKeyException(error.code, error.message ?? 'generateKey failed');
    }
  }

  @override
  Future<Uint8List> sign(Uint8List data) async {
    try {
      final Uint8List? signature = await _channel.invokeMethod<Uint8List>(
        'sign',
        <String, Object>{'data': data},
      );

      if (signature == null || signature.length != 64) {
        throw DeviceKeyException(
          'BAD_SIGNATURE',
          'Expected 64 raw signature bytes, got ${signature?.length ?? 0}.',
        );
      }

      return signature;
    } on PlatformException catch (error) {
      throw DeviceKeyException(error.code, error.message ?? 'sign failed');
    }
  }

  @override
  Future<void> deleteKey() async {
    try {
      await _channel.invokeMethod<void>('deleteKey');
    } on PlatformException catch (error) {
      throw DeviceKeyException(error.code, error.message ?? 'deleteKey failed');
    }
  }
}
