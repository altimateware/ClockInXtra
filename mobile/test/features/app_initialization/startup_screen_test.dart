import 'package:clockinxtra/core/device/device_integrity_service.dart';
import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/features/app_initialization/domain/startup_state.dart';
import 'package:clockinxtra/features/app_initialization/presentation/startup_messages.dart';
import 'package:clockinxtra/features/app_initialization/presentation/startup_screen.dart';
import 'package:clockinxtra/features/attendance/presentation/attendance_status_view.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fakes.dart';

void main() {
  Future<void> pumpScreen(WidgetTester tester, Harness harness) async {
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: harness.container,
        child: const MaterialApp(home: StartupScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('a refused location shows the refusal and no attendance', (WidgetTester tester) async {
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
      ..api.onValidateLocation = () async => throw const ApiException(
            code: 'LOCATION_NOT_ALLOWED',
            message: 'You do not appear to be at an approved office location.',
            correlationId: 'ref-123',
            statusCode: 403,
          );

    await pumpScreen(tester, harness);

    expect(find.text('Not at an approved office'), findsOneWidget);
    expect(find.text('Reference: ref-123'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
    expect(find.byKey(const Key('attendance-headline')), findsNothing);
  });

  testWidgets('a compromised device offers no way to retry', (WidgetTester tester) async {
    final Harness harness = Harness(
      integrity: FakeIntegrity(const IntegrityReport(
        indicators: <String>['ROOT_MANAGER_INSTALLED'],
        isEmulator: false,
        checked: true,
      )),
    );

    await pumpScreen(tester, harness);

    expect(find.text('This phone cannot be used'), findsOneWidget);
    expect(find.text('Try again'), findsNothing);
  });

  testWidgets('an unregistered device is shown the registration form', (WidgetTester tester) async {
    await pumpScreen(tester, Harness());

    expect(find.text('Register this phone'), findsOneWidget);
  });

  testWidgets('a ready device shows the server’s attendance state', (WidgetTester tester) async {
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1'))
      ..api.onUserStatus = () async => snapshot(AttendanceState.notClockedIn);

    await pumpScreen(tester, harness);

    expect(find.text('Not clocked in'), findsOneWidget);
  });

  group('messages', () {
    test('every block reason has a title and a body', () {
      for (final StartupBlockReason reason in StartupBlockReason.values) {
        final StartupMessage message = describeBlock(StartupBlocked(reason));

        expect(message.title, isNotEmpty, reason: reason.name);
        expect(message.body, isNotEmpty, reason: reason.name);
      }
    });

    test('a refused location never claims certainty GPS cannot give', () {
      // §65: a proximity check, not proof of where the phone is.
      final StartupMessage message = describeBlock(const StartupBlocked(StartupBlockReason.locationRejected));

      expect(message.body, contains('appear'));
    });

    test('summarises a completed day with the server’s times and duration', () {
      final AttendanceSummary summary = summarise(snapshot(AttendanceState.completed));

      expect(summary.headline, 'Finished for the day');
      expect(summary.detail, contains('9 h 3 min'));
      expect(summary.detail, contains('2026-09-17'));
    });
  });
}
