import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:clockinxtra/core/network/request_signer.dart';
import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';

/// Records what it was asked to sign, and returns a fixed signature.
///
/// The signature value is irrelevant here: these tests are about the *bytes*
/// handed to the key, because that is where client and server must agree.
final class _RecordingKeyService implements DeviceKeyService {
  Uint8List? signedData;

  @override
  Future<bool> hasKey() async => true;

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async =>
      throw UnimplementedError();

  @override
  Future<Uint8List> sign(Uint8List data) async {
    signedData = data;
    return Uint8List.fromList(List<int>.filled(64, 7));
  }

  @override
  Future<void> deleteKey() async {}
}

/// Deterministic source so the nonce is predictable in assertions.
final class _FixedRandom implements Random {
  @override
  bool nextBool() => false;

  @override
  double nextDouble() => 0;

  @override
  int nextInt(int max) => 0;
}

void main() {
  late _RecordingKeyService keyService;
  late RequestSigner signer;

  setUp(() {
    keyService = _RecordingKeyService();
    signer = RequestSigner(keyService, random: _FixedRandom());
  });

  group('signature base', () {
    test('matches the server construction exactly, for a request with a body', () async {
      // This is the assertion that matters. The server builds the same byte
      // sequence from the request it receives; if the two differ by one
      // character, every genuine request fails and the symptom looks exactly
      // like a wrong key.
      final Uint8List body = Uint8List.fromList(utf8.encode('{"hello":"world"}'));

      final SignedHeaders headers = await signer.sign(
        method: 'POST',
        authority: 'attendance.example.com',
        path: '/api/v1/mobile/attendance/clock-out',
        keyId: '8f2c0f3e-0000-4000-8000-000000000001',
        body: body,
        created: DateTime.fromMillisecondsSinceEpoch(1789459200 * 1000, isUtc: true),
      );

      final String digest = 'sha-256=:${base64.encode(sha256.convert(body).bytes)}:';

      const String expectedParameters =
          '("@method" "@authority" "@path" "content-digest")'
          ';created=1789459200'
          ';keyid="8f2c0f3e-0000-4000-8000-000000000001"'
          ';alg="ecdsa-p256-sha256"'
          ';nonce="AAAAAAAAAAAAAAAAAAAAAA"'
          ';tag="clockinxtra-mobile-v1"';

      final String expected = '"@method": POST\n'
          '"@authority": attendance.example.com\n'
          '"@path": /api/v1/mobile/attendance/clock-out\n'
          '"content-digest": $digest\n'
          '"@signature-params": $expectedParameters';

      expect(headers.signatureBase, expected);
      expect(headers.contentDigest, digest);
      expect(headers.signatureInput, 'sig1=$expectedParameters');
    });

    test('ends without a trailing newline', () async {
      // Not cosmetic: an extra newline changes every signature.
      final SignedHeaders headers = await signer.sign(
        method: 'POST',
        authority: 'host',
        path: '/p',
        keyId: 'k',
      );

      expect(headers.signatureBase.endsWith('\n'), isFalse);
    });

    test('omits content-digest when there is no body', () async {
      final SignedHeaders headers = await signer.sign(
        method: 'POST',
        authority: 'host',
        path: '/api/v1/mobile/user/status',
        keyId: 'k',
      );

      expect(headers.contentDigest, isNull);
      expect(headers.signatureBase.contains('content-digest'), isFalse);
      expect(headers.signatureInput.contains('content-digest'), isFalse);
    });

    test('covers the authority, so a signature cannot be reused against another host', () async {
      final SignedHeaders first = await signer.sign(
        method: 'POST', authority: 'attendance.example.com', path: '/p', keyId: 'k');

      final SignedHeaders second = await signer.sign(
        method: 'POST', authority: 'attacker.example.net', path: '/p', keyId: 'k');

      expect(first.signatureBase, isNot(second.signatureBase));
    });

    test('signs the base it reports', () async {
      final SignedHeaders headers = await signer.sign(
        method: 'POST', authority: 'host', path: '/p', keyId: 'k');

      expect(ascii.decode(keyService.signedData!), headers.signatureBase);
    });
  });

  group('profile', () {
    test('always declares the one permitted algorithm and tag', () async {
      final SignedHeaders headers = await signer.sign(
        method: 'POST', authority: 'host', path: '/p', keyId: 'k');

      expect(headers.signatureInput, contains('alg="ecdsa-p256-sha256"'));
      expect(headers.signatureInput, contains('tag="clockinxtra-mobile-v1"'));
    });

    test('encodes the signature as base64 of 64 raw bytes', () async {
      // RFC 9421 §3.3.4 requires raw r‖s for this algorithm, never DER.
      final SignedHeaders headers = await signer.sign(
        method: 'POST', authority: 'host', path: '/p', keyId: 'k');

      final String encoded = headers.signature
          .replaceFirst('sig1=:', '')
          .replaceFirst(RegExp(r':$'), '');

      expect(base64.decode(encoded).length, 64);
    });

    test('issues a different nonce each time from a real random source', () async {
      // The fixed source above makes assertions readable; the production source
      // must not repeat, because the server records each nonce and refuses a
      // repeat as a replay.
      final RequestSigner live = RequestSigner(keyService);

      final Set<String> nonces = <String>{};

      for (int i = 0; i < 25; i++) {
        final SignedHeaders headers = await live.sign(
          method: 'POST', authority: 'host', path: '/p', keyId: 'k');

        nonces.add(
          RegExp(r'nonce="([^"]+)"').firstMatch(headers.signatureInput)!.group(1)!,
        );
      }

      expect(nonces.length, 25);
    });
  });
}
