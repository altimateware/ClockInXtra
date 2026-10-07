import 'dart:typed_data';

import 'package:clockinxtra/core/device/app_identity.dart';
import 'package:clockinxtra/core/device/device_integrity_service.dart';
import 'package:clockinxtra/core/di/providers.dart';
import 'package:clockinxtra/core/location/location_service.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/core/network/attendance_api.dart';
import 'package:clockinxtra/core/security/device_key_service.dart';
import 'package:clockinxtra/core/storage/secure_store.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Hand-written test doubles for the startup and registration flows.
///
/// Each records what it was asked, because several of the rules being tested
/// are about what must NOT happen — no attendance call after a refused location,
/// no second registration attempt, no password in storage.

const AppIdentity testIdentity = AppIdentity(
  appVersion: '1.0.0+1',
  platform: DevicePlatformCode.android,
  deviceModel: 'Test Phone',
  osVersion: 'Android 16',
);

final class FakeIntegrity implements DeviceIntegrityService {
  FakeIntegrity([this.report = const IntegrityReport(indicators: <String>[], isEmulator: false, checked: true)]);

  IntegrityReport report;

  @override
  Future<IntegrityReport> inspect() async => report;
}

final class FakeLocation implements LocationService {
  FakeLocation({
    this.readiness = LocationReadiness.ready,
    this.afterRequest,
    this.failure,
  });

  LocationReadiness readiness;
  LocationReadiness? afterRequest;
  LocationUnavailableException? failure;

  int permissionRequests = 0;
  int positionRequests = 0;
  LocationReadiness? openedSettingsFor;

  @override
  Future<LocationReadiness> checkReadiness() async => readiness;

  @override
  Future<LocationReadiness> requestPermission() async {
    permissionRequests++;
    return afterRequest ?? readiness;
  }

  @override
  Future<DevicePosition> currentPosition() async {
    positionRequests++;

    if (failure != null) {
      throw failure!;
    }

    return const DevicePosition(latitude: 6.465422, longitude: 3.406448, accuracyMeters: 4, isMocked: false);
  }

  @override
  Future<void> openSettingsFor(LocationReadiness readiness) async => openedSettingsFor = readiness;
}

final class FakeStore implements SecureStore {
  FakeStore({this.devicePublicId, this.userId});

  String? devicePublicId;
  String? userId;
  int clears = 0;

  @override
  Future<String?> readDevicePublicId() async => devicePublicId;

  @override
  Future<String?> readUserId() async => userId;

  @override
  Future<void> writeDevicePublicId(String value) async => devicePublicId = value;

  @override
  Future<void> writeUserId(String value) async => userId = value;

  @override
  Future<void> clear() async {
    clears++;
    devicePublicId = null;
    userId = null;
  }
}

final class FakeKeys implements DeviceKeyService {
  FakeKeys({this.failure});

  DeviceKeyException? failure;
  int generated = 0;

  @override
  Future<bool> hasKey() async => generated > 0;

  @override
  Future<DeviceKeyAttestation> generateKey(Uint8List challenge) async {
    if (failure != null) {
      throw failure!;
    }

    generated++;
    return DeviceKeyAttestation(
      publicKey: Uint8List.fromList(<int>[0x04, ...List<int>.filled(64, 1)]),
      attestation: Uint8List.fromList(List<int>.filled(100, 2)),
      keyId: Uint8List(0),
    );
  }

  @override
  Future<Uint8List> sign(Uint8List data) async => Uint8List.fromList(List<int>.filled(64, 3));

  @override
  Future<void> deleteKey() async {}
}

/// A scripted API. Each endpoint is a function, so a test can answer, throw, or
/// change its answer between calls.
final class FakeApi implements AttendanceApi {
  Future<LocationValidation> Function() onValidateLocation =
      () async => const LocationValidation(success: true, officeLocationId: null);

  Future<DeviceRegistrationState> Function() onDeviceState = () async => DeviceRegistrationState(
        status: 'Active',
        employeeActive: true,
        revokedReason: null,
        serverTimeUtc: DateTime.utc(2026, 9, 17, 8),
      );

  Future<AttendanceSnapshot> Function() onUserStatus = () async => snapshot(AttendanceState.notClockedIn);

  Future<RegistrationChallenge> Function() onChallenge = () async => RegistrationChallenge(
        challengeId: 'c0000000-0000-4000-8000-000000000001',
        challenge: Uint8List.fromList(List<int>.filled(32, 7)),
        encoded: 'BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=',
        expiresUtc: DateTime.utc(2026, 9, 17, 8, 5),
      );

  Future<DeviceRegistration> Function() onRegister =
      () async => const DeviceRegistration(deviceId: 'd0000000-0000-4000-8000-000000000001', requiresApproval: true);

  Future<AttendanceSnapshot> Function() onClockIn = () async => snapshot(AttendanceState.clockedIn);

  Future<AttendanceSnapshot> Function() onClockOut = () async => snapshot(AttendanceState.completed);

  final List<String> calls = <String>[];

  /// Idempotency keys sent, in order — each attempt must use a new one.
  final List<String> idempotencyKeys = <String>[];

  /// The last clock-in's password, captured only to prove it reached the request.
  String? lastClockInPassword;

  /// The last registration's password, captured only so a test can prove it was
  /// passed through and nowhere else.
  String? lastRegistrationPassword;

  @override
  Future<LocationValidation> validateLocationAsync(ReportedPosition position) {
    calls.add('validateLocation');
    return onValidateLocation();
  }

  @override
  Future<DeviceRegistrationState> getDeviceStateAsync() {
    calls.add('deviceState');
    return onDeviceState();
  }

  @override
  Future<AttendanceSnapshot> getUserStatusAsync() {
    calls.add('userStatus');
    return onUserStatus();
  }

  @override
  Future<RegistrationChallenge> requestRegistrationChallengeAsync() {
    calls.add('challenge');
    return onChallenge();
  }

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
  }) {
    calls.add('register');
    lastRegistrationPassword = password;
    return onRegister();
  }

  @override
  Future<MobileConfiguration> getConfigurationAsync() => throw UnimplementedError();

  @override
  Future<AttendanceSnapshot> clockInAsync({
    required String userId,
    required String password,
    required String authenticatorCode,
    required ReportedPosition position,
    required String idempotencyKey,
  }) {
    calls.add('clockIn');
    idempotencyKeys.add(idempotencyKey);
    lastClockInPassword = password;
    return onClockIn();
  }

  @override
  Future<AttendanceSnapshot> clockOutAsync({required ReportedPosition position, required String idempotencyKey}) {
    calls.add('clockOut');
    idempotencyKeys.add(idempotencyKey);
    return onClockOut();
  }
}

AttendanceSnapshot snapshot(AttendanceState state) => AttendanceSnapshot(
      state: state,
      attendanceId: null,
      attendanceDate: '2026-09-17',
      clockInUtc: state == AttendanceState.notClockedIn ? null : DateTime.utc(2026, 9, 17, 7, 58),
      clockOutUtc: state == AttendanceState.completed ? DateTime.utc(2026, 9, 17, 17, 1) : null,
      durationMinutes: state == AttendanceState.completed ? 543 : null,
      isLateClockIn: null,
      isEarlyClockOut: null,
      serverTimeUtc: DateTime.utc(2026, 9, 17, 8),
      correlationId: 'c1',
    );

/// Everything a flow test needs, wired into a container.
final class Harness {
  Harness({
    FakeIntegrity? integrity,
    FakeLocation? location,
    FakeStore? store,
    FakeKeys? keys,
    FakeApi? api,
    this.apiFactoryFailure,
  })  : integrity = integrity ?? FakeIntegrity(),
        location = location ?? FakeLocation(),
        store = store ?? FakeStore(),
        keys = keys ?? FakeKeys(),
        api = api ?? FakeApi();

  final FakeIntegrity integrity;
  final FakeLocation location;
  final FakeStore store;
  final FakeKeys keys;
  final FakeApi api;

  /// When set, building the API client throws this — an unconfigured build.
  final Object? apiFactoryFailure;

  late final ProviderContainer container = ProviderContainer.test(
    overrides: [
      deviceIntegrityServiceProvider.overrideWithValue(integrity),
      locationServiceProvider.overrideWithValue(location),
      secureStoreProvider.overrideWithValue(store),
      deviceKeyServiceProvider.overrideWithValue(keys),
      appIdentityLoaderProvider.overrideWithValue(() async => testIdentity),
      attendanceApiFactoryProvider.overrideWithValue((AppIdentity identity) {
        if (apiFactoryFailure != null) {
          throw apiFactoryFailure!;
        }
        return api;
      }),
    ],
  );
}

/// Fails the test with a readable message if [value] is not a [T].
T expectType<T>(Object? value) {
  expect(value, isA<T>());
  return value! as T;
}
