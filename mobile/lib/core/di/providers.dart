import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../config/api_environment.dart';
import '../device/app_identity.dart';
import '../device/device_integrity_service.dart';
import '../location/location_service.dart';
import '../network/attendance_api.dart';
import '../network/attendance_api_client.dart';
import '../network/request_signer.dart';
import '../security/device_key_service.dart';
import '../storage/secure_store.dart';

/// The application's services, as overridable providers.
///
/// Composition happens here and nowhere else, so every feature receives its
/// dependencies rather than constructing them (§59) — and a test replaces any of
/// them with `overrideWithValue` without touching the feature under test.

/// On-device root and jailbreak signals.
final Provider<DeviceIntegrityService> deviceIntegrityServiceProvider =
    Provider<DeviceIntegrityService>((Ref ref) => PlatformDeviceIntegrityService.fromBuild());

/// The handset's position.
final Provider<LocationService> locationServiceProvider =
    Provider<LocationService>((Ref ref) => const GeolocatorLocationService());

/// Keychain / Keystore-backed storage for the user and device identifiers.
final Provider<SecureStore> secureStoreProvider =
    Provider<SecureStore>((Ref ref) => PlatformSecureStore());

/// The hardware-backed signing key.
final Provider<DeviceKeyService> deviceKeyServiceProvider =
    Provider<DeviceKeyService>((Ref ref) => const PlatformDeviceKeyService());

/// Reads what the app reports about itself.
final Provider<Future<AppIdentity> Function()> appIdentityLoaderProvider =
    Provider<Future<AppIdentity> Function()>((Ref ref) => AppIdentity.load);

/// Builds the API client for a given app identity.
///
/// A factory rather than a client because the app version is only known
/// asynchronously, and because building one can fail — an unconfigured or
/// cleartext base URL throws a [StateError] — which the startup flow reports
/// instead of crashing on.
typedef AttendanceApiFactory = AttendanceApi Function(AppIdentity identity);

/// The API client factory.
final Provider<AttendanceApiFactory> attendanceApiFactoryProvider =
    Provider<AttendanceApiFactory>((Ref ref) {
  final SecureStore store = ref.watch(secureStoreProvider);
  final DeviceKeyService keys = ref.watch(deviceKeyServiceProvider);

  return (AppIdentity identity) => AttendanceApiClient(
        environment: ApiEnvironment.fromCompileTimeConfiguration(),
        signer: RequestSigner(keys),
        keyId: store.readDevicePublicId,
        appVersion: identity.appVersion,
      );
});
