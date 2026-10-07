import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter/foundation.dart';
import 'package:package_info_plus/package_info_plus.dart';

import '../network/api_models.dart';

/// What the application reports about itself and the handset it runs on.
///
/// **Only what earns its place (§63).** The app version lets the server refuse a
/// build that is no longer supported. The model and OS version are shown to the
/// administrator approving a registration, so they can recognise the phone the
/// employee is holding in front of them. Nothing here identifies the handset
/// permanently — no IMEI, serial number or advertising id, which modern Android
/// and iOS withhold anyway (§18, §65). Device identity comes from the
/// hardware-backed key, not from an identifier the OS hands out.
///
/// All of it is untrusted client metadata, and the server treats it that way.
@immutable
final class AppIdentity {
  /// Creates the identity.
  const AppIdentity({
    required this.appVersion,
    required this.platform,
    required this.deviceModel,
    required this.osVersion,
  });

  /// Reads the identity from the platform.
  static Future<AppIdentity> load() async {
    final PackageInfo package = await PackageInfo.fromPlatform();
    final DeviceInfoPlugin device = DeviceInfoPlugin();
    final String version = '${package.version}+${package.buildNumber}';

    switch (defaultTargetPlatform) {
      case TargetPlatform.android:
        final AndroidDeviceInfo android = await device.androidInfo;
        return AppIdentity(
          appVersion: version,
          platform: DevicePlatformCode.android,
          deviceModel: _clip(android.model, 64),
          osVersion: _clip('Android ${android.version.release}', 32),
        );

      case TargetPlatform.iOS:
        final IosDeviceInfo ios = await device.iosInfo;
        return AppIdentity(
          appVersion: version,
          platform: DevicePlatformCode.ios,
          deviceModel: _clip(ios.modelName, 64),
          osVersion: _clip('iOS ${ios.systemVersion}', 32),
        );

      // The attendance app is Android and iOS only (§3.1); anything else cannot
      // produce a hardware attestation and is refused at startup.
      case TargetPlatform.fuchsia:
      case TargetPlatform.linux:
      case TargetPlatform.macOS:
      case TargetPlatform.windows:
        throw UnsupportedError('ClockInXtra runs on Android and iOS only.');
    }
  }

  /// The version reported to the API, e.g. `1.0.0+1`.
  final String appVersion;

  /// The platform code the API expects.
  final DevicePlatformCode platform;

  /// The handset model, for the approving administrator.
  final String deviceModel;

  /// The operating system version, for the approving administrator.
  final String osVersion;

  /// Truncated to the API's field lengths, so an unusual manufacturer string
  /// cannot turn a registration into a validation error.
  static String _clip(String value, int length) =>
      value.length <= length ? value : value.substring(0, length);
}
