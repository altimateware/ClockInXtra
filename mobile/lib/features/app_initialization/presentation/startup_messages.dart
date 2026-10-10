import 'package:flutter/foundation.dart';

import '../domain/startup_state.dart';

/// What the employee is told about a startup step or a refusal.
///
/// Kept apart from the widgets so every sentence can be checked in a test, and so
/// a new [StartupBlockReason] cannot be added without deciding what to say — the
/// switch is exhaustive.
///
/// **Written for someone at a door, in a hurry.** Each message says what happened
/// and what, if anything, they can do about it. None of them claims more than is
/// known: a refused location says the phone "does not appear" to be at an office,
/// because GPS cannot prove where a phone is (§65).
@immutable
final class StartupMessage {
  /// Creates a message.
  const StartupMessage(this.title, this.body);

  /// A short heading.
  final String title;

  /// What happened and what to do.
  final String body;
}

/// The sentence for a step in progress.
String describeStep(StartupStep step) => switch (step) {
      StartupStep.checkingIntegrity => 'Checking this phone…',
      StartupStep.checkingLocationPermission => 'Checking location permission…',
      StartupStep.locating => 'Finding your location…',
      StartupStep.validatingLocation => 'Checking your location with the office…',
      StartupStep.checkingDevice => 'Checking this device…',
      StartupStep.loadingAttendance => 'Loading today’s attendance…',
    };

/// The message for a refusal.
StartupMessage describeBlock(StartupBlocked blocked) => switch (blocked.reason) {
      StartupBlockReason.deviceCompromised => const StartupMessage(
          'This phone cannot be used',
          'It shows signs of having been rooted or jailbroken, so attendance cannot be recorded on it. '
              'Please use an unmodified phone, or contact your administrator.',
        ),
      StartupBlockReason.integrityCheckUnavailable => const StartupMessage(
          'This phone could not be checked',
          'The app could not confirm that this phone is unmodified, so it cannot continue. '
              'Please contact your administrator.',
        ),
      StartupBlockReason.unsupportedPlatform => const StartupMessage(
          'Not supported on this device',
          'ClockInXtra runs on Android and iPhone only.',
        ),
      StartupBlockReason.notConfigured => const StartupMessage(
          'This copy of the app is not set up',
          'It does not know which attendance service to use. Please install the version provided by your organisation.',
        ),
      StartupBlockReason.locationServicesDisabled => const StartupMessage(
          'Location is switched off',
          'Turn on location services so the app can check you are at your office.',
        ),
      StartupBlockReason.locationPermissionDenied => const StartupMessage(
          'Location permission is needed',
          'The app checks your location against your office before offering attendance. '
              'Try again and allow location access.',
        ),
      StartupBlockReason.locationPermissionDeniedForever => const StartupMessage(
          'Location permission is turned off',
          'Location access was refused for this app. Open settings and allow location while using the app.',
        ),
      StartupBlockReason.noLocationFix => const StartupMessage(
          'Your location could not be found',
          'Move nearer a window or outside, then try again. Location is often weak indoors.',
        ),
      StartupBlockReason.locationRejected => StartupMessage(
          'Not at an approved office',
          blocked.serverMessage ?? 'You do not appear to be at an approved office location.',
        ),
      // Not the same as being in the wrong place, and must not read like it.
      // The phone may well be standing in the office; what the server refused
      // was a measurement too vague to judge against it. Consumer GPS is often
      // worse than the configured radius indoors (§65), so this is an ordinary
      // condition with an ordinary remedy, not an accusation.
      StartupBlockReason.locationAccuracyInsufficient => const StartupMessage(
          'Your location is not precise enough',
          'Your phone cannot tell where it is closely enough to check it against your office. '
              'Move near a window or step outside, wait a few seconds, then try again. '
              'If it keeps happening here, your administrator can review the accuracy setting.',
        ),
      StartupBlockReason.locationSourceUntrusted => const StartupMessage(
          'This location could not be trusted',
          'Your phone reported a simulated location. Turn off any mock-location or GPS-spoofing app, '
              'including one selected under developer options, then try again.',
        ),
      StartupBlockReason.clockSkew => const StartupMessage(
          'This phone’s clock is wrong',
          'Set the date and time to update automatically, then try again.',
        ),
      StartupBlockReason.appVersionUnsupported => const StartupMessage(
          'Please update the app',
          'This version of ClockInXtra is no longer supported.',
        ),
      StartupBlockReason.deviceRevoked => StartupMessage(
          'This device has been removed',
          blocked.serverMessage == null || blocked.serverMessage!.isEmpty
              ? 'An administrator has revoked this device. Please contact your administrator.'
              : 'An administrator has revoked this device: ${blocked.serverMessage}',
        ),
      StartupBlockReason.employeeInactive => const StartupMessage(
          'Your account is not active',
          'Please contact your administrator.',
        ),
      StartupBlockReason.attendanceNotConfigured => const StartupMessage(
          'Attendance is not set up yet',
          'The opening and closing times have not been configured. Please contact your administrator.',
        ),
      StartupBlockReason.serviceUnreachable => const StartupMessage(
          'Cannot reach the attendance service',
          'Check your connection and try again. Nothing has been recorded.',
        ),
      StartupBlockReason.unexpected => StartupMessage(
          'Something went wrong',
          blocked.serverMessage ?? 'Please try again. If it keeps happening, contact your administrator.',
        ),
    };
