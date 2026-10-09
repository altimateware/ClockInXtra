import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Checks whether this device shows signs of being rooted or jailbroken (§25).
///
/// **A signal, not a boundary.** The check runs inside a process a rooted
/// device controls, so determined root can hide from it. It stops casual use of
/// a compromised phone. What actually refuses such a device is the server, which
/// verifies a hardware-signed attestation of the boot state at registration.
abstract interface class DeviceIntegrityService {
  /// Inspects the device.
  Future<IntegrityReport> inspect();
}

/// What the inspection found.
@immutable
final class IntegrityReport {
  /// Creates a report.
  const IntegrityReport({
    required this.indicators,
    required this.isEmulator,
    required this.checked,
  }) : unavailableReason = null;

  /// A report for a device where the check could not run.
  const IntegrityReport.unavailable(String reason)
      : indicators = const <String>[],
        isEmulator = false,
        checked = false,
        unavailableReason = reason;

  /// Codes for each sign of compromise found, e.g. `SU_BINARY_PRESENT`.
  final List<String> indicators;

  /// Whether the device appears to be an emulator. Reported, not blocking on
  /// its own: the server refuses an emulator's attestation regardless.
  final bool isEmulator;

  /// Whether the inspection actually ran.
  final bool checked;

  /// Why it did not run, when it did not.
  final String? unavailableReason;

  /// Whether the device may be used.
  ///
  /// **An inspection that did not run is not a pass.** Treating "could not
  /// check" as "fine" would make disabling the check the easiest bypass of all.
  bool get isTrusted => checked && indicators.isEmpty;
}

/// Method-channel implementation backed by `IntegrityHandler.kt`.
///
/// iOS has no native implementation yet — it needs a macOS build agent
/// (CON-06) — so on iOS the channel call fails and the report is
/// [IntegrityReport.unavailable], which the startup flow refuses. That is
/// deliberate: an unimplemented check must not ship as a silent pass.
final class PlatformDeviceIntegrityService implements DeviceIntegrityService {
  /// Creates the service.
  const PlatformDeviceIntegrityService({
    this.channel = const MethodChannel('com.altimateware.clockinxtra/integrity'),
    this.allowCompromisedForDevelopment = false,
  });

  /// Reads the development override from the build.
  ///
  /// Honoured only when [kReleaseMode] is false, and only when asked for
  /// explicitly with a define. AOSP and "Google APIs" emulator images ship with
  /// `su` and a `userdebug` OS, so without this the app could not be exercised
  /// on one at all ("Google Play" images are `user` builds and pass without
  /// it); a release build ignores the define entirely.
  factory PlatformDeviceIntegrityService.fromBuild() => PlatformDeviceIntegrityService(
        allowCompromisedForDevelopment: !kReleaseMode &&
            const bool.fromEnvironment(developmentOverrideKey),
      );

  /// The build-time key that relaxes the check in a debug build.
  static const String developmentOverrideKey = 'CLOCKINXTRA_ALLOW_COMPROMISED_DEVICE_FOR_DEVELOPMENT';

  /// The channel to the native check.
  final MethodChannel channel;

  /// Whether found indicators are ignored. Only ever true in a debug build
  /// started with [developmentOverrideKey]; see [PlatformDeviceIntegrityService.fromBuild].
  final bool allowCompromisedForDevelopment;

  @override
  Future<IntegrityReport> inspect() async {
    final Map<Object?, Object?>? result;

    try {
      result = await channel.invokeMethod<Map<Object?, Object?>>('inspect');
    } on MissingPluginException {
      return const IntegrityReport.unavailable('No integrity check is implemented on this platform.');
    } on PlatformException catch (error) {
      return IntegrityReport.unavailable('The integrity check failed: ${error.code}');
    }

    if (result == null) {
      return const IntegrityReport.unavailable('The integrity check returned nothing.');
    }

    final List<String> found = <String>[
      for (final Object? indicator in (result['indicators'] as List<Object?>?) ?? const <Object?>[])
        if (indicator is String) indicator,
    ];

    if (found.isNotEmpty && allowCompromisedForDevelopment) {
      debugPrint('[integrity] development override: ignoring ${found.join(', ')}');
    }

    return IntegrityReport(
      indicators: allowCompromisedForDevelopment ? const <String>[] : found,
      isEmulator: result['isEmulator'] as bool? ?? false,
      checked: true,
    );
  }
}
