import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../config/api_environment.dart';
import 'api_exception.dart';
import 'attendance_api.dart';
import 'api_models.dart';
import 'request_signer.dart';
import 'signing_interceptor.dart';

/// The mobile application's only route to the attendance API.
///
/// **Why every call goes through one class.** The signature, the digest, the
/// idempotency key and the error mapping are each easy to omit on a single call
/// site, and each omission fails in a way that looks like something else. Keeping
/// them in one place means a feature cannot accidentally issue an unsigned
/// request, and the rules have somewhere to be tested.
///
/// **What is deliberately absent: a logging interceptor.** `dio`'s `LogInterceptor`
/// prints request bodies, and the clock-in body contains the employee's password
/// and authenticator code. §24 and §33 forbid both from reaching any log, so the
/// diagnostics this client offers are the correlation id and the error code —
/// which are enough to find the request in the server's log, where the detail
/// belongs.
final class AttendanceApiClient implements AttendanceApi {
  /// Creates a client over an existing [Dio], for tests.
  AttendanceApiClient.withTransport(this._dio, this._environment, [this._appVersion = '']);

  /// Creates the client the application uses.
  factory AttendanceApiClient({
    required ApiEnvironment environment,
    required RequestSigner signer,
    required KeyIdResolver keyId,
    required String appVersion,
  }) {
    final Dio dio = Dio(
      BaseOptions(
        baseUrl: environment.baseUri.toString(),
        connectTimeout: environment.connectTimeout,
        sendTimeout: environment.sendTimeout,
        receiveTimeout: environment.receiveTimeout,
        contentType: Headers.jsonContentType,
        responseType: ResponseType.json,

        // Every status is a response to be interpreted, not an exception. The
        // API answers a refusal with a structured body (§34) that carries the
        // code and correlation id, and letting dio throw would discard it.
        validateStatus: (_) => true,
      ),
    );

    dio.interceptors.add(
      SigningInterceptor(
        signer: signer,
        keyId: keyId,
        authority: environment.authority,
      ),
    );

    return AttendanceApiClient.withTransport(dio, environment, appVersion);
  }

  final Dio _dio;
  final ApiEnvironment _environment;
  final String _appVersion;

  /// The application version reported to the server, for diagnostics only.
  String get appVersion => _appVersion;

  /// The environment this client talks to.
  ApiEnvironment get environment => _environment;

  /// Reads the settings and configured flags a mobile client may see.
  ///
  /// Signed when this device is registered, unsigned when it is not — the
  /// endpoint accepts either, and it is one of the few a fresh install can call.
  @override
  Future<MobileConfiguration> getConfigurationAsync() async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'GET',
      path: '/api/v1/mobile/app/config',
      policy: RequestSignaturePolicy.optional,
    );

    return MobileConfiguration.fromJson(body);
  }

  /// Runs the startup location check (§8.1).
  ///
  /// A refusal is returned as an [ApiException] carrying `LOCATION_NOT_ALLOWED`
  /// or one of the accuracy codes, because the caller must show the employee a
  /// different sentence for each and must not continue to the home screen.
  @override
  Future<LocationValidation> validateLocationAsync(ReportedPosition position) async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/location/validate',
      policy: RequestSignaturePolicy.optional,
      json: <String, Object?>{
        'position': position.toJson(),
        'appVersion': _appVersion,
      },
    );

    return LocationValidation.fromJson(body);
  }

  /// Obtains a single-use challenge to bind into a key attestation.
  ///
  /// Sent unsigned. The device has no key the server could resolve, and the
  /// endpoint does not accept `keyid="unregistered"` — signing it would be
  /// refused with `UNREGISTERED_KEY_NOT_PERMITTED`.
  @override
  Future<RegistrationChallenge> requestRegistrationChallengeAsync() async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/device/registration/challenge',
      policy: RequestSignaturePolicy.none,
    );

    return RegistrationChallenge.fromJson(body);
  }

  /// Registers this device against an employee.
  ///
  /// Signed with the key being registered, whose public key is in the body: the
  /// signature is the proof that this caller holds the private half of the key
  /// it is presenting. The attestation is separate evidence, that the key lives
  /// in genuine secure hardware.
  ///
  /// [password] and [authenticatorCode] are used to build the request and then
  /// dropped. They are never stored, cached or logged (§24). Dart offers no way
  /// to wipe a [String] from memory, which is a limitation worth naming rather
  /// than papering over: the mitigation is that neither value is retained beyond
  /// this call, and neither is ever written anywhere.
  @override
  Future<DeviceRegistration> registerDeviceAsync({
    required String userId,
    required String password,
    required String authenticatorCode,
    required RegistrationChallenge challenge,
    required Uint8List publicKey,
    required Uint8List attestation,
    required DevicePlatformCode platform,
    Uint8List? attestationKeyId,
    String? deviceModel,
    String? osVersion,
  }) async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/device/register',
      policy: RequestSignaturePolicy.unregistered,
      json: <String, Object?>{
        'userId': userId,
        'password': password,
        'authenticatorCode': authenticatorCode,
        'challengeId': challenge.challengeId,
        'challenge': challenge.encoded,
        'publicKey': base64.encode(publicKey),
        'platform': platform.wireValue,
        'attestation': base64.encode(attestation),
        if (attestationKeyId != null && attestationKeyId.isNotEmpty)
          'attestationKeyId': base64.encode(attestationKeyId),
        'deviceModel': deviceModel,
        'osVersion': osVersion,
        'appVersion': _appVersion,
      },

      // 202 is not an error: the device is registered and waiting for an
      // administrator (DEC-04). Treating it as a failure would send the employee
      // round the registration loop again, creating a second pending device.
      acceptedStatuses: const <int>[200, 202],
    );

    return DeviceRegistration.fromJson(body);
  }

  /// Asks the server what it thinks of this device.
  ///
  /// Reachable while pending or revoked — a device waiting for approval has to be
  /// able to find out that it was approved.
  @override
  Future<DeviceRegistrationState> getDeviceStateAsync() async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/device/status',
      policy: RequestSignaturePolicy.required,
    );

    return DeviceRegistrationState.fromJson(body);
  }

  /// Reads the employee's attendance state for the current day (§11).
  @override
  Future<AttendanceSnapshot> getUserStatusAsync() async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/user/status',
      policy: RequestSignaturePolicy.required,
    );

    return AttendanceSnapshot.fromJson(body);
  }

  /// Records a clock-in (§12).
  ///
  /// [idempotencyKey] must be new for every attempt. It protects the server
  /// against the same request arriving twice, but it is **not** a way to retry
  /// after a lost answer: the server consumes the authenticator code before it
  /// consults the key, so a resend is refused either as a reused code or, with a
  /// fresh code, as a key reused for a different body. After an unknown outcome
  /// the caller asks for the user status instead (§11, §52) — see
  /// AttendanceActionController.
  @override
  Future<AttendanceSnapshot> clockInAsync({
    required String userId,
    required String password,
    required String authenticatorCode,
    required ReportedPosition position,
    required String idempotencyKey,
  }) async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/attendance/clock-in',
      policy: RequestSignaturePolicy.required,
      idempotencyKey: idempotencyKey,
      json: <String, Object?>{
        'userId': userId,
        'password': password,
        'authenticatorCode': authenticatorCode,
        'position': position.toJson(),
      },
    );

    return AttendanceSnapshot.fromJson(body);
  }

  /// Records a clock-out (§14).
  ///
  /// No password or code, per ASM-04. [idempotencyKey] must be new for every
  /// attempt, as for [clockInAsync].
  @override
  Future<AttendanceSnapshot> clockOutAsync({
    required ReportedPosition position,
    required String idempotencyKey,
  }) async {
    final Map<String, Object?> body = await _sendAsync(
      method: 'POST',
      path: '/api/v1/mobile/attendance/clock-out',
      policy: RequestSignaturePolicy.required,
      idempotencyKey: idempotencyKey,
      json: <String, Object?>{'position': position.toJson()},
    );

    return AttendanceSnapshot.fromJson(body);
  }

  /// Issues one request and returns its body, or throws [ApiException].
  Future<Map<String, Object?>> _sendAsync({
    required String method,
    required String path,
    required RequestSignaturePolicy policy,
    Map<String, Object?>? json,
    String? idempotencyKey,
    List<int> acceptedStatuses = const <int>[200],
  }) async {
    // Encoded once, here. These exact bytes are what the digest covers and what
    // dio transmits, because a Uint8List bypasses dio's request transformer.
    // Handing dio a Map instead would let it re-encode, and the digest would
    // then describe bytes the server never received.
    final Uint8List? payload =
        json == null ? null : Uint8List.fromList(utf8.encode(jsonEncode(json)));

    final Response<Object?> response;

    try {
      response = await _dio.request<Object?>(
        path,
        data: payload,
        options: Options(
          method: method,
          headers: <String, Object?>{'Idempotency-Key': ?idempotencyKey},
          extra: <String, Object?>{SigningInterceptor.policyExtra: policy},
        ),
      );
    } on DioException catch (error) {
      throw _translate(error);
    }

    final Map<String, Object?> body = switch (response.data) {
      final Map<String, Object?> map => map,
      _ => const <String, Object?>{},
    };

    if (!acceptedStatuses.contains(response.statusCode)) {
      throw ApiException(
        code: body['code'] as String? ?? 'INTERNAL_ERROR',
        message: body['message'] as String? ??
            'The server could not complete the request.',
        correlationId: _correlationOf(response, body),
        statusCode: response.statusCode,
      );
    }

    if (response.data is! Map<String, Object?>) {
      throw ApiException(
        code: ApiException.malformedResponse,
        message: 'The server returned an unexpected response.',
        statusCode: response.statusCode,
      );
    }

    return body;
  }

  /// Prefers the body's correlation id, falling back to the echoed header.
  static String? _correlationOf(Response<Object?> response, Map<String, Object?> body) =>
      body['correlationId'] as String? ??
      response.headers.value('X-Correlation-Id');

  /// Turns a transport failure into the application's own error type.
  ///
  /// Each of these leaves the outcome genuinely unknown, which is why none of
  /// them may be presented to the employee as a completed transaction (§52).
  static ApiException _translate(DioException error) {
    if (isNotRegisteredFailure(error.error)) {
      return const ApiException(
        code: ApiException.notRegistered,
        message: 'This device is not registered.',
      );
    }

    if (error.error is HandshakeException || error.type == DioExceptionType.badCertificate) {
      return const ApiException(
        code: ApiException.tlsFailure,
        message: 'The connection could not be trusted. Please contact your administrator.',
      );
    }

    return switch (error.type) {
      DioExceptionType.connectionTimeout ||
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout ||
      DioExceptionType.transformTimeout =>
        const ApiException(
          code: ApiException.networkTimeout,
          message: 'The server did not respond in time. Please try again.',
        ),
      _ => const ApiException(
          code: ApiException.networkUnavailable,
          message: 'The attendance service could not be reached.',
        ),
    };
  }
}
