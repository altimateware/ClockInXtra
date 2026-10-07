import 'dart:typed_data';

import 'package:dio/dio.dart';

import 'request_signer.dart';

/// How a particular request is authenticated.
///
/// These four cases mirror the server exactly — see
/// `SignatureVerificationMiddleware` and the endpoint attributes it reads. They
/// are kept as an explicit choice per call rather than inferred, because getting
/// it wrong is silent: an over-signed request is refused with
/// `UNREGISTERED_KEY_NOT_PERMITTED`, and an under-signed one arrives
/// unauthenticated.
enum RequestSignaturePolicy {
  /// Send nothing. The endpoint is marked `AllowUnsignedRequest` and this device
  /// has no key the server could resolve — the registration challenge.
  none,

  /// Sign with the key being registered (`keyid="unregistered"`), whose public
  /// key travels in the body. Only `AllowUnregisteredDevice` endpoints accept it.
  unregistered,

  /// Sign if this device is registered, otherwise send unsigned.
  ///
  /// For the endpoints reachable before enrolment. A registered device still
  /// signs, because the server gives a signed caller a slightly fuller answer
  /// (the matched office) and because an unsigned request skips the body digest.
  optional,

  /// Sign, or fail locally without sending anything.
  required,
}

/// Resolves the device's registered key identifier, or null before registration.
typedef KeyIdResolver = Future<String?> Function();

/// Attaches an RFC 9421 signature to outgoing requests.
///
/// **The body is signed exactly as it is transmitted.** The client hands dio a
/// [Uint8List], which dio sends verbatim without passing it through a
/// transformer — verified in `dio_mixin.dart`, where `data is Uint8List` short
/// circuits the transform. Anything else would be re-encoded on its way out, and
/// the digest would cover bytes the server never saw. That mistake produces a
/// rejection indistinguishable from a wrong key, so this interceptor refuses a
/// body of any other type outright rather than signing something it cannot
/// vouch for.
final class SigningInterceptor extends Interceptor {
  /// Creates the interceptor.
  const SigningInterceptor({
    required this.signer,
    required this.keyId,
    required this.authority,
  });

  /// The key under which a call states its [RequestSignaturePolicy].
  static const String policyExtra = 'clockinxtra.signaturePolicy';

  /// Produces the signature over each request.
  final RequestSigner signer;

  /// Supplies this device's registered key identifier, or null before enrolment.
  final KeyIdResolver keyId;

  /// The authority the signature covers. See `ApiEnvironment.authority`.
  final String authority;

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    final RequestSignaturePolicy policy =
        options.extra[policyExtra] as RequestSignaturePolicy? ??
            RequestSignaturePolicy.required;

    if (policy == RequestSignaturePolicy.none) {
      handler.next(options);
      return;
    }

    final Object? data = options.data;

    if (data != null && data is! Uint8List) {
      // A programming error, not a runtime condition: signing bytes other than
      // the ones sent would be worse than not signing at all.
      handler.reject(
        DioException(
          requestOptions: options,
          type: DioExceptionType.unknown,
          error: StateError(
            'A signed request must carry its body as Uint8List so the digest '
            'covers the transmitted bytes; got ${data.runtimeType}.',
          ),
        ),
        true,
      );
      return;
    }

    final String? resolvedKeyId = switch (policy) {
      RequestSignaturePolicy.unregistered => RequestSigner.unregisteredKeyId,
      _ => await keyId(),
    };

    if (resolvedKeyId == null) {
      if (policy == RequestSignaturePolicy.optional) {
        handler.next(options);
        return;
      }

      handler.reject(
        DioException(
          requestOptions: options,
          type: DioExceptionType.unknown,
          error: const _NotRegistered(),
        ),
        true,
      );
      return;
    }

    final SignedHeaders headers = await signer.sign(
      method: options.method.toUpperCase(),
      authority: authority,
      path: options.uri.path,
      keyId: resolvedKeyId,
      body: data as Uint8List?,
    );

    options.headers['Signature-Input'] = headers.signatureInput;
    options.headers['Signature'] = headers.signature;

    if (headers.contentDigest != null) {
      options.headers['Content-Digest'] = headers.contentDigest;
    }

    handler.next(options);
  }
}

/// Marks a local refusal to sign, translated by the client into an
/// [ApiException] the UI can explain.
final class _NotRegistered implements Exception {
  const _NotRegistered();

  @override
  String toString() => 'This device is not registered.';
}

/// Whether a rejection came from this device having no key.
bool isNotRegisteredFailure(Object? error) => error is _NotRegistered;
