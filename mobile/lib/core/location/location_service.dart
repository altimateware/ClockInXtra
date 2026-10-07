import 'package:geolocator/geolocator.dart';

/// Obtains the device's position for an attendance request.
///
/// **This is a proximity signal, not proof of presence.** A device can report
/// manipulated coordinates, and the server treats the reading as evidence to be
/// weighed rather than a fact (§65). Everything here is about getting the best
/// reading the hardware will give and reporting honestly how good it is — the
/// decision is the server's.
abstract interface class LocationService {
  /// Whether location permission and services allow a reading.
  Future<LocationReadiness> checkReadiness();

  /// Requests permission, returning the resulting readiness.
  Future<LocationReadiness> requestPermission();

  /// Takes a single reading.
  ///
  /// Throws [LocationUnavailableException] when no reading can be obtained.
  Future<DevicePosition> currentPosition();

  /// Opens the system screen that can fix [readiness]: location settings when
  /// services are off, the app's permission settings otherwise.
  ///
  /// Needed because once permission is denied permanently, the app is no longer
  /// allowed to ask — only the employee can restore it, from settings.
  Future<void> openSettingsFor(LocationReadiness readiness);
}

/// Why a position cannot be taken, if it cannot.
enum LocationReadiness {
  /// A reading can be taken.
  ready,

  /// The employee has not been asked yet.
  notRequested,

  /// The employee refused, but can be asked again.
  denied,

  /// The employee refused permanently; only system settings can restore it.
  deniedForever,

  /// Location services are switched off device-wide.
  servicesDisabled,
}

/// A position, with the honesty the server needs to judge it.
final class DevicePosition {
  /// Creates the position.
  const DevicePosition({
    required this.latitude,
    required this.longitude,
    required this.accuracyMeters,
    required this.isMocked,
  });

  /// Degrees north.
  final double latitude;

  /// Degrees east.
  final double longitude;

  /// The horizontal accuracy the platform reported, in metres.
  ///
  /// Sent as-is, including when it is poor. Suppressing or improving this number
  /// would deprive the server of the one thing that says how much the reading is
  /// worth — and the 5-metre radius is frequently finer than a phone can resolve
  /// (CON-01).
  final double accuracyMeters;

  /// Whether the platform flagged the position as artificial.
  ///
  /// Android exposes this directly. On iOS there is no equivalent flag, so this
  /// is false there and the absence is a known limitation rather than a claim
  /// that the position is genuine (CON-07).
  final bool isMocked;
}

/// Raised when no position can be obtained.
final class LocationUnavailableException implements Exception {
  /// Creates the exception.
  const LocationUnavailableException(this.readiness, [this.message]);

  /// Why it could not be obtained.
  final LocationReadiness readiness;

  /// Detail for logs, never for display.
  final String? message;

  @override
  String toString() => 'LocationUnavailableException($readiness): $message';
}

/// Geolocator implementation.
final class GeolocatorLocationService implements LocationService {
  /// Creates the service.
  const GeolocatorLocationService();

  /// How long to wait for a reading before giving up.
  ///
  /// A fix can take a while from cold, especially indoors. Waiting forever would
  /// leave an employee holding a spinner at a door, so the attempt is bounded and
  /// the failure is reported plainly.
  static const Duration _timeout = Duration(seconds: 20);

  @override
  Future<LocationReadiness> checkReadiness() async {
    if (!await Geolocator.isLocationServiceEnabled()) {
      return LocationReadiness.servicesDisabled;
    }

    return _map(await Geolocator.checkPermission());
  }

  @override
  Future<LocationReadiness> requestPermission() async {
    if (!await Geolocator.isLocationServiceEnabled()) {
      return LocationReadiness.servicesDisabled;
    }

    return _map(await Geolocator.requestPermission());
  }

  @override
  Future<DevicePosition> currentPosition() async {
    final LocationReadiness readiness = await checkReadiness();

    if (readiness != LocationReadiness.ready) {
      throw LocationUnavailableException(readiness);
    }

    try {
      final Position position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          // Best available: the radius is metres, so a coarse reading is not
          // worth sending.
          accuracy: LocationAccuracy.best,
          timeLimit: _timeout,
        ),
      );

      return DevicePosition(
        latitude: position.latitude,
        longitude: position.longitude,
        accuracyMeters: position.accuracy,
        isMocked: position.isMocked,
      );
    } on Exception catch (error) {
      throw LocationUnavailableException(
        LocationReadiness.ready,
        'No fix within ${_timeout.inSeconds}s: $error',
      );
    }
  }

  @override
  Future<void> openSettingsFor(LocationReadiness readiness) async {
    if (readiness == LocationReadiness.servicesDisabled) {
      await Geolocator.openLocationSettings();
    } else {
      await Geolocator.openAppSettings();
    }
  }

  static LocationReadiness _map(LocationPermission permission) => switch (permission) {
        LocationPermission.always || LocationPermission.whileInUse =>
          LocationReadiness.ready,
        LocationPermission.denied => LocationReadiness.denied,
        LocationPermission.deniedForever => LocationReadiness.deniedForever,
        LocationPermission.unableToDetermine => LocationReadiness.notRequested,
      };
}
