import 'package:clockinxtra/app.dart';
import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import '../test/support/fakes.dart';

/// The whole app, on a device: startup, clock-in and clock-out as an employee
/// would perform them, typing on the real keyboard and tapping real buttons.
///
/// The server, position and integrity report are faked so the run is
/// repeatable anywhere; the screens, state management, dialogs and text input
/// are the real ones on the real engine. The server path itself is covered by
/// the API tests, and was exercised end to end on an emulator in Phase 18.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  Future<Harness> launch(WidgetTester tester, AttendanceState state) async {
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1', userId: 'e.adeyemi'))
      ..api.onUserStatus = () async => snapshot(state);

    await tester.pumpWidget(
      UncontrolledProviderScope(container: harness.container, child: const ClockInXtraApp()),
    );
    await tester.pumpAndSettle();

    return harness;
  }

  testWidgets('an employee clocks in, then out, and the day shows as finished', (WidgetTester tester) async {
    final Harness harness = await launch(tester, AttendanceState.notClockedIn);

    expect(find.text('Clocking in as e.adeyemi'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextField, 'Password'), 'the employee password');
    await tester.enterText(find.widgetWithText(TextField, 'Authenticator code'), '123456');
    await tester.tap(find.byKey(const Key('clock-in')));
    await tester.pumpAndSettle();

    expect(find.text('Clocked in'), findsOneWidget);
    expect(find.text('the employee password'), findsNothing);

    await tester.tap(find.byKey(const Key('clock-out')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Clock out').last);
    await tester.pumpAndSettle();

    expect(find.text('Finished for the day'), findsOneWidget);
    expect(harness.api.calls, containsAllInOrder(<String>['clockIn', 'clockOut']));
  });

  testWidgets('with the server unreachable nothing is shown as done', (WidgetTester tester) async {
    // §52: an unknown outcome is never displayed as a completed clock-in.
    final Harness harness = await launch(tester, AttendanceState.notClockedIn);
    harness.api.onClockIn = () async =>
        throw const ApiException(code: ApiException.networkUnavailable, message: 'unreachable');

    await tester.enterText(find.widgetWithText(TextField, 'Password'), 'the employee password');
    await tester.enterText(find.widgetWithText(TextField, 'Authenticator code'), '123456');
    await tester.tap(find.byKey(const Key('clock-in')));
    await tester.pumpAndSettle();

    expect(find.textContaining('it is not known whether this was recorded'), findsOneWidget);
    expect(find.text('Clocked in'), findsNothing);
    expect(harness.api.calls.where((String call) => call == 'clockIn'), hasLength(1));
  });
}
