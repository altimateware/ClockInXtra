import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/api_models.dart';
import '../application/attendance_action_controller.dart';

/// Today's attendance, and the one action that applies to it (§11, §12, §14).
///
/// **The server is the source of truth.** The state shown, and therefore which
/// button is offered, is exactly what the last server response said. A
/// clock-in time is the server's timestamp, never the phone's (§31). Once the
/// day is complete there is nothing to press.
///
/// The widget holds only the text being typed; what to send and what a refusal
/// means are [AttendanceActionController]'s (§59).
class AttendanceStatusView extends ConsumerStatefulWidget {
  /// Creates the view.
  const AttendanceStatusView({required this.attendance, super.key});

  /// The server's answer.
  final AttendanceSnapshot attendance;

  @override
  ConsumerState<AttendanceStatusView> createState() => _AttendanceStatusViewState();
}

class _AttendanceStatusViewState extends ConsumerState<AttendanceStatusView> {
  final TextEditingController _userId = TextEditingController();
  final TextEditingController _password = TextEditingController();
  final TextEditingController _code = TextEditingController();

  @override
  void dispose() {
    _userId.dispose();
    _password
      ..clear()
      ..dispose();
    _code.dispose();
    super.dispose();
  }

  Future<void> _clockIn(String? storedUserId) async {
    final String password = _password.text;
    final String code = _code.text;

    // Read, then cleared at once. Neither waits in a field while the request
    // runs, and neither is on screen if the attempt fails and the phone is put
    // down (§24). A failed attempt means typing the code again, which it would
    // anyway: the code changes every thirty seconds.
    _password.clear();
    _code.clear();

    await ref.read(attendanceActionControllerProvider.notifier).clockIn(
          userId: storedUserId ?? _userId.text,
          password: password,
          authenticatorCode: code,
        );
  }

  void _checkWithServer() => ref.read(attendanceActionControllerProvider.notifier).checkWithServer();

  Future<void> _clockOut() async {
    // Clocking out cannot be undone from the phone, and a tap in a pocket is not
    // a decision. One confirmation, no more.
    final bool confirmed = await showDialog<bool>(
          context: context,
          builder: (BuildContext context) => AlertDialog(
            title: const Text('Clock out now?'),
            content: const Text('This ends your attendance for today.'),
            actions: <Widget>[
              TextButton(onPressed: () => Navigator.of(context).pop(false), child: const Text('Cancel')),
              FilledButton(onPressed: () => Navigator.of(context).pop(true), child: const Text('Clock out')),
            ],
          ),
        ) ??
        false;

    if (confirmed) {
      await ref.read(attendanceActionControllerProvider.notifier).clockOut();
    }
  }

  @override
  Widget build(BuildContext context) {
    final AttendanceActionState action = ref.watch(attendanceActionControllerProvider);
    final String? storedUserId = ref.watch(storedUserIdProvider).value;
    final TextTheme text = Theme.of(context).textTheme;
    final AttendanceSummary summary = summarise(widget.attendance);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: <Widget>[
        Text(summary.headline, style: text.headlineSmall, key: const Key('attendance-headline')),
        const SizedBox(height: 8),
        Text(summary.detail),
        const SizedBox(height: 24),
        ...switch (widget.attendance.state) {
          AttendanceState.notClockedIn => _clockInForm(action, storedUserId),
          AttendanceState.clockedIn => <Widget>[
              FilledButton(
                key: const Key('clock-out'),
                onPressed: action.busy ? null : _clockOut,
                child: Text(action.busy ? describeActionStep(action.step!) : 'Clock out'),
              ),
            ],
          AttendanceState.completed => const <Widget>[],
        },
        if (action.failure != null) ...<Widget>[
          const SizedBox(height: 16),
          _Failure(action: action, onCheck: _checkWithServer),
        ],
        const SizedBox(height: 24),
        OutlinedButton(onPressed: action.busy ? null : _checkWithServer, child: const Text('Refresh')),
      ],
    );
  }

  List<Widget> _clockInForm(AttendanceActionState action, String? storedUserId) => <Widget>[
        if (storedUserId == null)
          TextField(
            controller: _userId,
            enabled: !action.busy,
            decoration: const InputDecoration(labelText: 'User ID'),
            autocorrect: false,
            enableSuggestions: false,
            maxLength: 64,
          )
        else
          // Shown, not editable: the server only accepts the employee this phone
          // is registered to, so offering to change it would only offer a refusal.
          Text('Clocking in as $storedUserId', key: const Key('clock-in-user')),
        TextField(
          controller: _password,
          enabled: !action.busy,
          decoration: const InputDecoration(labelText: 'Password'),
          obscureText: true,
          autocorrect: false,
          enableSuggestions: false,
          onChanged: (_) => ref.read(attendanceActionControllerProvider.notifier).dismissFailure(),
        ),
        TextField(
          controller: _code,
          enabled: !action.busy,
          decoration: const InputDecoration(labelText: 'Authenticator code'),
          keyboardType: TextInputType.number,
          inputFormatters: <TextInputFormatter>[FilteringTextInputFormatter.digitsOnly],
          maxLength: 6,
          onSubmitted: (_) => _clockIn(storedUserId),
        ),
        const SizedBox(height: 8),
        FilledButton(
          key: const Key('clock-in'),
          onPressed: action.busy ? null : () => _clockIn(storedUserId),
          child: Text(action.busy ? describeActionStep(action.step!) : 'Clock in'),
        ),
      ];
}

class _Failure extends StatelessWidget {
  const _Failure({required this.action, required this.onCheck});

  final AttendanceActionState action;
  final VoidCallback onCheck;

  @override
  Widget build(BuildContext context) {
    final TextTheme text = Theme.of(context).textTheme;
    final bool unknown = action.failure == AttendanceActionFailure.outcomeUnknown;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        Text(
          describeActionFailure(action),
          key: const Key('attendance-failure'),
          style: text.bodyMedium?.copyWith(
            color: action.failure == AttendanceActionFailure.alreadyDone ? null : Theme.of(context).colorScheme.error,
          ),
        ),
        if (action.correlationId != null) ...<Widget>[
          const SizedBox(height: 4),
          SelectableText('Reference: ${action.correlationId}', style: text.bodySmall),
        ],
        if (unknown) ...<Widget>[
          const SizedBox(height: 8),
          // The one correct response to an unknown outcome: ask the server.
          FilledButton(onPressed: onCheck, child: const Text('Check whether it was recorded')),
        ],
      ],
    );
  }
}

/// The label a busy button shows.
String describeActionStep(AttendanceActionStep step) => switch (step) {
      AttendanceActionStep.locating => 'Finding your location…',
      AttendanceActionStep.recording => 'Recording…',
    };

/// The sentence for an action that did not happen.
String describeActionFailure(AttendanceActionState action) => switch (action.failure!) {
      AttendanceActionFailure.incomplete => 'Enter your password and the six-digit code.',
      AttendanceActionFailure.invalidCredentials => 'The details entered were not correct.',
      AttendanceActionFailure.accountLocked =>
        'Your account is temporarily locked after too many attempts. Please try again later.',
      AttendanceActionFailure.authenticatorNotEnrolled =>
        'No authenticator is set up for your account. Please contact your administrator.',
      AttendanceActionFailure.locationUnavailable =>
        'Your location could not be found. Move nearer a window or outside, then try again.',
      AttendanceActionFailure.locationRejected =>
        action.serverMessage ?? 'You do not appear to be at an approved office location.',
      AttendanceActionFailure.windowClosed => action.serverMessage ?? 'That is outside the permitted hours.',
      AttendanceActionFailure.notConfigured =>
        'Attendance is not set up yet. Please contact your administrator.',
      AttendanceActionFailure.outcomeUnknown =>
        'The attendance service did not answer, so it is not known whether this was recorded. '
            'Check before trying again.',
      AttendanceActionFailure.wrongEmployee => 'This phone is registered to a different employee.',
      AttendanceActionFailure.alreadyDone => 'This was already recorded. The screen shows the latest.',
      AttendanceActionFailure.unexpected =>
        action.serverMessage ?? 'Something went wrong. Please try again.',
    };

/// A plain-language summary of an attendance snapshot.
@immutable
final class AttendanceSummary {
  /// Creates a summary.
  const AttendanceSummary(this.headline, this.detail);

  /// The state in a few words.
  final String headline;

  /// The supporting detail.
  final String detail;
}

/// Summarises a snapshot. Separate from the widget so it can be tested.
///
/// Times are converted to the phone's local time for display only. The
/// attendance date is shown as the server sent it, because it is a date in the
/// organisation's attendance time zone and converting it would move it (§31).
AttendanceSummary summarise(AttendanceSnapshot attendance) {
  final String date = attendance.attendanceDate ?? 'today';

  return switch (attendance.state) {
    AttendanceState.notClockedIn => AttendanceSummary(
        'Not clocked in',
        'You have not clocked in for $date.',
      ),
    AttendanceState.clockedIn => AttendanceSummary(
        'Clocked in',
        attendance.clockInUtc == null
            ? 'You are clocked in for $date.'
            : 'You clocked in at ${formatTime(attendance.clockInUtc!)} for $date.',
      ),
    AttendanceState.completed => AttendanceSummary(
        'Finished for the day',
        _completedDetail(attendance, date),
      ),
  };
}

String _completedDetail(AttendanceSnapshot attendance, String date) {
  final StringBuffer detail = StringBuffer('Your attendance for $date is complete');

  if (attendance.clockInUtc != null && attendance.clockOutUtc != null) {
    detail.write(': ${formatTime(attendance.clockInUtc!)} to ${formatTime(attendance.clockOutUtc!)}');
  }

  if (attendance.durationMinutes case final int minutes) {
    detail.write(' (${minutes ~/ 60} h ${minutes % 60} min)');
  }

  detail.write('.');
  return detail.toString();
}

/// `HH:mm` in the phone's local time.
String formatTime(DateTime utc) {
  final DateTime local = utc.toLocal();
  return '${local.hour.toString().padLeft(2, '0')}:${local.minute.toString().padLeft(2, '0')}';
}
