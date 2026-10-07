import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

import '../security/device_key_service.dart';

/// Builds RFC 9421 HTTP Message Signatures for requests to the attendance API.
///
/// This is the client half of the profile specified in
/// `docs/architecture/03-solution-architecture.md` §6. The server rebuilds the
/// same byte sequence from the request it receives and verifies the signature
/// over it, so **the two constructions must agree exactly**. A single extra
/// newline, or an authority spelled differently, makes every request fail with a
/// symptom that looks exactly like a wrong key.
///
/// Deliberate constraints, each mirrored on the server:
///
/// * Only `ecdsa-p256-sha256` is produced, in the raw `r‖s` form RFC 9421
///   §3.3.4 specifies — never DER.
/// * The covered components are exactly `@method`, `@authority`, `@path` and,
///   when there is a body, `content-digest`. Covering less would leave the
///   uncovered parts free to be altered in transit; RFC 9421 §7.2.1 warns that
///   such a signature is worse than none because it looks like protection.
/// * The nonce is 128 bits from a cryptographic source. It is single-use: the
///   server records it and refuses a repeat, which is what stops a captured
///   request being replayed (§37).
final class RequestSigner {
  /// Creates the signer.
  /// Creates the signer over a key service, optionally with a fixed random
  /// source so tests can assert an exact nonce.
  RequestSigner(this._keyService, {Random? random})
      : _random = random ?? Random.secure();

  /// The algorithm identifier, fixed by the profile.
  static const String algorithm = 'ecdsa-p256-sha256';

  /// Scopes a signature to this application.
  static const String tag = 'clockinxtra-mobile-v1';

  /// The key id used before the device is registered (§6.3).
  static const String unregisteredKeyId = 'unregistered';

  /// The signature label. One signature per request, so it never varies.
  static const String _label = 'sig1';

  final DeviceKeyService _keyService;
  final Random _random;

  /// Produces the headers that authenticate one request.
  ///
  /// [keyId] is the device's public identifier once registered, or
  /// [unregisteredKeyId] during registration, when the server cannot yet
  /// resolve a device and the handler verifies possession against the key in
  /// the body instead.
  Future<SignedHeaders> sign({
    required String method,
    required String authority,
    required String path,
    required String keyId,
    Uint8List? body,
    DateTime? created,
  }) async {
    final bool hasBody = body != null && body.isNotEmpty;

    final String? contentDigest =
        hasBody ? 'sha-256=:${base64.encode(sha256.convert(body).bytes)}:' : null;

    final List<String> components = hasBody
        ? const <String>['@method', '@authority', '@path', 'content-digest']
        : const <String>['@method', '@authority', '@path'];

    final int createdSeconds =
        ((created ?? DateTime.now().toUtc()).millisecondsSinceEpoch / 1000).floor();

    final String parameters = '(${components.map((String c) => '"$c"').join(' ')})'
        ';created=$createdSeconds'
        ';keyid="$keyId"'
        ';alg="$algorithm"'
        ';nonce="${_newNonce()}"'
        ';tag="$tag"';

    final String signatureBase = _buildSignatureBase(
      components: components,
      method: method,
      authority: authority,
      path: path,
      contentDigest: contentDigest,
      parameters: parameters,
    );

    final Uint8List signature = await _keyService.sign(
      Uint8List.fromList(ascii.encode(signatureBase)),
    );

    return SignedHeaders(
      signatureInput: '$_label=$parameters',
      signature: '$_label=:${base64.encode(signature)}:',
      contentDigest: contentDigest,
      signatureBase: signatureBase,
    );
  }

  /// Assembles the signature base exactly as RFC 9421 §2.5 defines it.
  ///
  /// Each covered component contributes one `"name": value` line. The final line
  /// is the signature parameters and carries **no trailing newline** — that last
  /// detail is not cosmetic, and getting it wrong invalidates every signature.
  static String _buildSignatureBase({
    required List<String> components,
    required String method,
    required String authority,
    required String path,
    required String? contentDigest,
    required String parameters,
  }) {
    final StringBuffer buffer = StringBuffer();

    for (final String component in components) {
      final String value = switch (component) {
        '@method' => method,
        '@authority' => authority,
        '@path' => path,
        'content-digest' => contentDigest ?? '',
        _ => throw StateError('Component $component is outside the profile.'),
      };

      buffer.write('"$component": $value\n');
    }

    buffer.write('"@signature-params": $parameters');

    return buffer.toString();
  }

  /// A 128-bit nonce, base64url without padding.
  String _newNonce() {
    final Uint8List bytes = Uint8List(16);

    for (int i = 0; i < bytes.length; i++) {
      bytes[i] = _random.nextInt(256);
    }

    return base64Url.encode(bytes).replaceAll('=', '');
  }
}

/// The headers a signed request carries.
final class SignedHeaders {
  /// Creates the header set.
  const SignedHeaders({
    required this.signatureInput,
    required this.signature,
    required this.contentDigest,
    required this.signatureBase,
  });

  /// The `Signature-Input` header value.
  final String signatureInput;

  /// The `Signature` header value.
  final String signature;

  /// The `Content-Digest` header value, or null for a request with no body.
  final String? contentDigest;

  /// The bytes that were signed.
  ///
  /// Exposed for tests and for diagnosing a rejection: when the server refuses a
  /// signature, comparing the two bases is the only way to see why.
  final String signatureBase;
}
