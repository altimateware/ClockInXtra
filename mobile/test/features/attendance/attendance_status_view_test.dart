import 'package:clockinxtra/core/network/api_exception.dart';
import 'package:clockinxtra/core/network/api_models.dart';
import 'package:clockinxtra/features/app_initialization/application/startup_controller.dart';
import 'package:clockinxtra/features/app_initialization/presentation/startup_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fakes.dart';

void main() {
  Future<Harness> pumpReady(WidgetTester tester, AttendanceState state) async {
    final Harness harness = Harness(store: FakeStore(devicePublicId: 'd1', userId: 'e.adeyemi'))
      ..api.onUserStatus = () async => snapshot(state);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: harness.container,
        child: const MaterialApp(home: StartupScreen()),
      ),
    );
    await harness.container.read(startupControllerProvider.notifier).restart();
    await tester.pumpAndSettle();

    return harness;
  }

  testWidgets('offers clock-in, as the registered employee, when not clocked in', (WidgetTester tester) async {
    await pumpReady(tester, AttendanceState.notClockedIn);

    expect(find.text('Clocking in as e.adeyemi'), findsOneWidget);
    expect(find.byKey(const Key('clock-in')), findsOneWidget);
    expect(find.byKey(const Key('clock-out')), findsNothing);
  });

  testWidgets('clocking in clears the password field and shows the new state', (WidgetTester tester) async {
    final Harness harness = await pumpReady(tester, AttendanceState.notClockedIn);

    await tester.enterText(find.widgetWithText(TextField, 'Password'), 'the employee password');
    await tester.enterText(find.widgetWithText(TextField, 'Authenticator code'), '123456');
    await tester.tap(find.byKey(const Key('clock-in')));
    await tester.pumpAndSettle();

    expect(harness.api.lastClockInPassword, 'the employee password');
    expect(find.text('Clocked in'), findsOneWidget);
    expect(find.text('the employee password'), findsNothing);
  });

  testWidgets('offers clock-out when clocked in, and asks first', (WidgetTester tester) async {
    final Harness harness = await pumpReady(tester, AttendanceState.clockedIn);

    await tester.tap(find.byKey(const Key('clock-out')));
    await tester.pumpAndSettle();
    expect(find.text('Clock out now?'), findsOneWidget);

    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(harness.api.calls, isNot(contains('clockOut')));

    await tester.tap(find.byKey(const Key('clock-out')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Clock out').last);
    await tester.pumpAndSettle();

    expect(harness.api.calls, contains('clockOut'));
    expect(find.text('Finished for the day'), findsOneWidget);
  });

  testWidgets('a completed day offers nothing to press', (WidgetTester tester) async {
    await pumpReady(tester, AttendanceState.completed);

    expect(find.byKey(const Key('clock-in')), findsNothing);
    expect(find.byKey(const Key('clock-out')), findsNothing);
  });

  testWidgets('a lost answer says so, and offers to ask the server', (WidgetTester tester) async {
    final Harness harness = await pumpReady(tester, AttendanceState.clockedIn);
    harness.api.onClockOut = () async =>
        throw const ApiException(code: ApiException.networkUnavailable, message: 'unreachable');

    await tester.tap(find.byKey(const Key('clock-out')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Clock out').last);
    await tester.pumpAndSettle();

    expect(find.textContaining('it is not known whether this was recorded'), findsOneWidget);
    expect(find.text('Check whether it was recorded'), findsOneWidget);

    // Still shown as clocked in: nothing is displayed as done that was not confirmed.
    expect(find.text('Clocked in'), findsOneWidget);
  });
}
