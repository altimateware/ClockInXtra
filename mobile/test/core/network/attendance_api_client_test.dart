import 'dart:convert';
import 'dart:typed_data';

import 'package:clockinxtra/core/config/api_environment.dart';
import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/core/network/attendance_api_client.dart';
import 'package:clockinxtra/core/network/request_signer.dart';
import 'package:clockinxtra/core/network/signing_interceptor.dart';
import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

/// Returns a fixed signature and records the bytes it was asked to sign.
final class _StubKeyService implements DeviceKeyService {
  Uint8List? signedData;

  @override
  Future<bool> hasKey() async => true;

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async =>
      throw UnimplementedError();

  @override
  Future<Uint8List> sign(Uint8List data) async {
    signedData = data;
    return Uint8List.fromList(List<int>.filled(64, 9));
  }

  @override
  Future<void> deleteKey() async {}
}

/// Stands in for the network, capturing exactly what would go on the wire.
///
/// Reading the body from [requestStream] rather than from `options.data` is the
/// point of this fake: it is the only way to assert that the digest covers the
/// transmitted bytes rather than some earlier representation of them.
final class _CapturingAdapter implements HttpClientAdapter {
  // Set after construction by each test, so the harness builder stays uniform.
  int statusCode = 200;
  String body = '{}';
  Map<String, List<String>>? headers;

  RequestOptions? captured;
  Uint8List? capturedBody;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    captured = options;

    if (requestStream != null) {
      final List<int> bytes = <int>[];

      await for (final Uint8List chunk in requestStream) {
        bytes.addAll(chunk);
      }

      capturedBody = Uint8List.fromList(bytes);
    }

    return ResponseBody.fromString(
      body,
      statusCode,
      headers: <String, List<String>>{
        Headers.contentTypeHeader: <String>[Headers.jsonContentType],
        ...?headers,
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// Builds a client wired to the capturing adapter.
({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
    _buildClient({String? deviceId = 'f0e1d2c3-0000-4000-8000-00000000abcd'}) {
  final ApiEnvironment environment = ApiEnvironment(
    baseUri: Uri.parse('https://attendance.example.com'),
  );

  final _StubKeyService keys = _StubKeyService();
  final _CapturingAdapter adapter = _CapturingAdapter();

  final Dio dio = Dio(
    BaseOptions(
      baseUrl: environment.baseUri.toString(),
      contentType: Headers.jsonContentType,
      responseType: ResponseType.json,
      validateStatus: (_) => true,
    ),
  )..httpClientAdapter = adapter;

  dio.interceptors.add(
    SigningInterceptor(
      signer: RequestSigner(keys),
      keyId: () async => deviceId,
      authority: environment.authority,
    ),
  );

  return (
    client: AttendanceApiClient.withTransport(dio, environment, '1.0.0+1'),
    adapter: adapter,
    keys: keys,
  );
}

const ReportedPosition _position = ReportedPosition(
  latitude: 6.4541,
  longitude: 3.3947,
  accuracyMeters: 4.5,
  isMocked: false,
);

void main() {
  group('signing', () {
    test('digests the bytes that are actually transmitted', () async {
      // The failure this guards against is subtle and total: if the body were
      // re-encoded on its way out, every signed request would be refused with
      // CONTENT_DIGEST_MISMATCH and the symptom would look like a wrong key.
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'success': true,
        'state': 'ClockedIn',
        'serverTimeUtc': '2026-09-13T08:00:00+00:00',
        'correlationId': 'c1',
      });

      await harness.client.clockInAsync(
        userId: 'e.adeyemi',
        password: 'not-logged',
        authenticatorCode: '123456',
        position: _position,
        idempotencyKey: 'aaaaaaaa-0000-4000-8000-000000000001',
      );

      final Uint8List sent = harness.adapter.capturedBody!;
      final String expected = 'sha-256=:${base64.encode(sha256.convert(sent).bytes)}:';

      expect(harness.adapter.captured!.headers['Content-Digest'], expected);
    });

    test('covers the authority and path the request is sent to', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'status': 'Active',
        'employeeActive': true,
        'serverTimeUtc': '2026-09-13T08:00:00+00:00',
      });

      await harness.client.getDeviceStateAsync();

      final String base = ascii.decode(harness.keys.signedData!);

      expect(base, contains('"@authority": attendance.example.com\n'));
      expect(base, contains('"@path": /api/v1/mobile/device/status\n'));
      expect(base, contains('"@method": POST\n'));
    });

    test('sends the registration request with the unregistered key id', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient(deviceId: null);

      harness.adapter.body = jsonEncode(<String, Object?>{
        'success': true,
        'deviceId': 'd0000000-0000-4000-8000-000000000001',
        'requiresApproval': false,
      });

      await harness.client.registerDeviceAsync(
        userId: 'e.adeyemi',
        password: 'not-logged',
        authenticatorCode: '123456',
        challenge: RegistrationChallenge(
          challengeId: 'c0000000-0000-4000-8000-000000000001',
          challenge: Uint8List.fromList(List<int>.filled(32, 1)),
          encoded: base64.encode(List<int>.filled(32, 1)),
          expiresUtc: DateTime.utc(2026, 9, 13, 8, 5),
        ),
        publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 2)]),
        attestation: Uint8List.fromList(List<int>.filled(200, 3)),
        platform: DevicePlatformCode.android,
      );

      // No key is registered yet, so the resolver returns null — and the request
      // must still be signed, with the key being presented.
      expect(
        harness.adapter.captured!.headers['Signature-Input'],
        contains('keyid="unregistered"'),
      );
    });

    test('echoes the challenge exactly as the server encoded it', () async {
      // Re-encoding from the decoded bytes would be correct base64 and still
      // wrong if the server's padding differed; the server compares the record
      // it issued.
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient(deviceId: null);

      harness.adapter.body = jsonEncode(<String, Object?>{'success': true});

      await harness.client.registerDeviceAsync(
        userId: 'e.adeyemi',
        password: 'not-logged',
        authenticatorCode: '123456',
        challenge: RegistrationChallenge(
          challengeId: 'c0000000-0000-4000-8000-000000000001',
          challenge: Uint8List.fromList(List<int>.filled(32, 1)),
          encoded: 'THE-SERVERS-EXACT-STRING',
          expiresUtc: DateTime.utc(2026, 9, 13, 8, 5),
        ),
        publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 2)]),
        attestation: Uint8List.fromList(List<int>.filled(200, 3)),
        platform: DevicePlatformCode.android,
      );

      final Map<String, Object?> body = jsonDecode(
        utf8.decode(harness.adapter.capturedBody!),
      ) as Map<String, Object?>;

      expect(body['challenge'], 'THE-SERVERS-EXACT-STRING');
    });

    test('sends the challenge request unsigned', () async {
      // The endpoint rejects keyid="unregistered", so signing it would turn a
      // fresh install into an unexplainable 401.
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'challengeId': 'c0000000-0000-4000-8000-000000000001',
        'challenge': base64.encode(List<int>.filled(32, 1)),
        'expiresUtc': '2026-09-13T08:05:00+00:00',
        'correlationId': 'c1',
      });

      await harness.client.requestRegistrationChallengeAsync();

      expect(harness.adapter.captured!.headers.containsKey('Signature-Input'), isFalse);
    });

    test('sends an optional endpoint unsigned when the device has no key', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient(deviceId: null);

      harness.adapter.body = jsonEncode(<String, Object?>{'success': true});

      await harness.client.validateLocationAsync(_position);

      expect(harness.adapter.captured!.headers.containsKey('Signature-Input'), isFalse);
    });

    test('signs an optional endpoint once the device is registered', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'success': true,
        'officeLocationId': 7,
      });

      final LocationValidation result =
          await harness.client.validateLocationAsync(_position);

      expect(harness.adapter.captured!.headers.containsKey('Signature-Input'), isTrue);
      expect(result.officeLocationId, 7);
    });

    test('refuses a required endpoint locally when the device has no key', () async {
      // Nothing is sent. A request that cannot be authenticated is not worth a
      // round trip, and the UI needs a distinguishable reason.
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient(deviceId: null);

      await expectLater(
        harness.client.getUserStatusAsync(),
        throwsA(
          isA<ApiException>().having(
            (ApiException e) => e.code,
            'code',
            ApiException.notRegistered,
          ),
        ),
      );

      expect(harness.adapter.captured, isNull);
    });
  });

  group('idempotency', () {
    test('carries the key the caller supplied, unchanged', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'state': 'Completed',
        'serverTimeUtc': '2026-09-13T17:00:00+00:00',
      });

      await harness.client.clockOutAsync(
        position: _position,
        idempotencyKey: 'bbbbbbbb-0000-4000-8000-000000000002',
      );

      expect(
        harness.adapter.captured!.headers['Idempotency-Key'],
        'bbbbbbbb-0000-4000-8000-000000000002',
      );
    });

    test('omits the header entirely where the endpoint has no such rule', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'state': 'NotClockedIn',
        'serverTimeUtc': '2026-09-13T08:00:00+00:00',
      });

      await harness.client.getUserStatusAsync();

      expect(harness.adapter.captured!.headers.containsKey('Idempotency-Key'), isFalse);
    });
  });

  group('errors', () {
    test('surfaces the server code and correlation id', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter
        ..statusCode = 403
        ..body = jsonEncode(<String, Object?>{
          'success': false,
          'code': 'LOCATION_NOT_ALLOWED',
          'message': 'You do not appear to be at an approved office location.',
          'correlationId': '1f0a5b2c-0000-4000-8000-000000000009',
        });

      await expectLater(
        harness.client.validateLocationAsync(_position),
        throwsA(
          isA<ApiException>()
              .having((ApiException e) => e.code, 'code', 'LOCATION_NOT_ALLOWED')
              .having((ApiException e) => e.statusCode, 'status', 403)
              .having(
                (ApiException e) => e.correlationId,
                'correlationId',
                '1f0a5b2c-0000-4000-8000-000000000009',
              ),
        ),
      );
    });

    test('treats 202 on registration as success awaiting approval', () async {
      // A pending device is registered. Reporting it as a failure would send the
      // employee round the loop and create a second pending registration.
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient(deviceId: null);

      harness.adapter
        ..statusCode = 202
        ..body = jsonEncode(<String, Object?>{
          'success': true,
          'deviceId': 'd0000000-0000-4000-8000-000000000001',
          'requiresApproval': true,
        });

      final DeviceRegistration result = await harness.client.registerDeviceAsync(
        userId: 'e.adeyemi',
        password: 'not-logged',
        authenticatorCode: '123456',
        challenge: RegistrationChallenge(
          challengeId: 'c0000000-0000-4000-8000-000000000001',
          challenge: Uint8List.fromList(List<int>.filled(32, 1)),
          encoded: base64.encode(List<int>.filled(32, 1)),
          expiresUtc: DateTime.utc(2026, 9, 13, 8, 5),
        ),
        publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 2)]),
        attestation: Uint8List.fromList(List<int>.filled(200, 3)),
        platform: DevicePlatformCode.android,
      );

      expect(result.requiresApproval, isTrue);
      expect(result.deviceId, 'd0000000-0000-4000-8000-000000000001');
    });

    test('reports an unreachable server as a transport failure', () async {
      // §52: this must never look like a completed transaction.
      final ApiEnvironment environment =
          ApiEnvironment(baseUri: Uri.parse('https://attendance.example.com'));

      final Dio dio = Dio(BaseOptions(baseUrl: environment.baseUri.toString()))
        ..httpClientAdapter = _FailingAdapter();

      dio.interceptors.add(
        SigningInterceptor(
          signer: RequestSigner(_StubKeyService()),
          keyId: () async => 'f0e1d2c3-0000-4000-8000-00000000abcd',
          authority: environment.authority,
        ),
      );

      final AttendanceApiClient client =
          AttendanceApiClient.withTransport(dio, environment);

      await expectLater(
        client.getUserStatusAsync(),
        throwsA(
          isA<ApiException>()
              .having((ApiException e) => e.code, 'code', ApiException.networkUnavailable)
              .having((ApiException e) => e.isTransport, 'isTransport', isTrue),
        ),
      );
    });
  });

  group('responses', () {
    test('reads the attendance state the server reports', () async {
      final ({AttendanceApiClient client, _CapturingAdapter adapter, _StubKeyService keys})
          harness = _buildClient();

      harness.adapter.body = jsonEncode(<String, Object?>{
        'success': true,
        'state': 'ClockedIn',
        'attendanceDate': '2026-09-13',
        'clockInUtc': '2026-09-13T07:58:11+00:00',
        'serverTimeUtc': '2026-09-13T08:00:00+00:00',
        'correlationId': 'c1',
      });

      final AttendanceSnapshot snapshot = await harness.client.getUserStatusAsync();

      expect(snapshot.state, AttendanceState.clockedIn);
      expect(snapshot.attendanceDate, '2026-09-13');
      expect(snapshot.clockInUtc, DateTime.utc(2026, 9, 13, 7, 58, 11));
    });

    test('does not offer clock-out for a state it does not recognise', () async {
      // Guessing the permissive option would only produce a button that fails.
      expect(AttendanceState.parse('SomethingNew'), AttendanceState.notClockedIn);
      expect(AttendanceState.parse(null), AttendanceState.notClockedIn);
    });
  });
}

/// Fails every request, as an unreachable server does.
final class _FailingAdapter implements HttpClientAdapter {
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async =>
      throw DioException.connectionError(
        requestOptions: options,
        reason: 'no route to host',
      );

  @override
  void close({bool force = false}) {}
}
