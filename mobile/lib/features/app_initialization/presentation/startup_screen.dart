import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/di/providers.dart';
import '../../../core/location/location_service.dart';
import '../../attendance/presentation/attendance_status_view.dart';
import '../../authentication/presentation/registration_view.dart';
import '../application/startup_controller.dart';
import '../domain/startup_state.dart';
import 'startup_messages.dart';

/// The app's single entry screen: whatever the startup sequence decided.
///
/// Attendance is reachable only through [StartupReady]. Every other state renders
/// something that is not attendance, which is how §8.1's "do not expose the
/// attendance functionality" is kept — by construction rather than by remembering
/// to hide a button.
class StartupScreen extends ConsumerWidget {
  /// Creates the screen.
  const StartupScreen({this.actions = const <Widget>[], super.key});

  /// App bar actions, such as the debug-only diagnostics entry.
  final List<Widget> actions;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final StartupState state = ref.watch(startupControllerProvider);
    final StartupController controller = ref.read(startupControllerProvider.notifier);

    return Scaffold(
      appBar: AppBar(title: const Text('ClockInXtra'), actions: actions),
      body: SafeArea(
        child: switch (state) {
          StartupInProgress(:final StartupStep step) => _Progress(describeStep(step)),
          StartupBlocked() => _Blocked(
              blocked: state,
              onRetry: controller.restart,
              onOpenSettings: () => ref
                  .read(locationServiceProvider)
                  .openSettingsFor(state.locationReadiness ?? LocationReadiness.deniedForever),
            ),
          StartupNeedsRegistration() => const RegistrationView(),
          StartupAwaitingApproval() => _AwaitingApproval(onCheckAgain: controller.restart),
          StartupReady(:final attendance) => AttendanceStatusView(attendance: attendance),
        },
      ),
    );
  }
}

class _Progress extends StatelessWidget {
  const _Progress(this.message);

  final String message;

  @override
  Widget build(BuildContext context) => Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(message, key: const Key('startup-progress')),
          ],
        ),
      );
}

class _Blocked extends StatelessWidget {
  const _Blocked({required this.blocked, required this.onRetry, required this.onOpenSettings});

  final StartupBlocked blocked;
  final VoidCallback onRetry;
  final VoidCallback onOpenSettings;

  @override
  Widget build(BuildContext context) {
    final StartupMessage message = describeBlock(blocked);
    final TextTheme text = Theme.of(context).textTheme;

    return ListView(
      padding: const EdgeInsets.all(24),
      children: <Widget>[
        Icon(Icons.block, size: 48, color: Theme.of(context).colorScheme.error),
        const SizedBox(height: 16),
        Text(message.title, style: text.headlineSmall, key: const Key('startup-blocked-title')),
        const SizedBox(height: 8),
        Text(message.body),
        if (blocked.correlationId != null) ...<Widget>[
          const SizedBox(height: 16),
          // Quotable to support: it finds this request in the server's log without
          // exposing anything about it.
          SelectableText('Reference: ${blocked.correlationId}', style: text.bodySmall),
        ],
        const SizedBox(height: 24),
        if (blocked.canOpenSettings)
          FilledButton(onPressed: onOpenSettings, child: const Text('Open settings')),
        if (blocked.canRetry)
          OutlinedButton(onPressed: onRetry, child: const Text('Try again')),
      ],
    );
  }
}

class _AwaitingApproval extends StatelessWidget {
  const _AwaitingApproval({required this.onCheckAgain});

  final VoidCallback onCheckAgain;

  @override
  Widget build(BuildContext context) => ListView(
        padding: const EdgeInsets.all(24),
        children: <Widget>[
          Text(
            'Waiting for approval',
            style: Theme.of(context).textTheme.headlineSmall,
            key: const Key('awaiting-approval'),
          ),
          const SizedBox(height: 8),
          const Text(
            'This phone is registered. An administrator needs to approve it before you can clock in. '
            'You do not need to register again.',
          ),
          const SizedBox(height: 24),
          // Checked when asked, not polled in the background: approval is a
          // person's decision and can take a while, and a phone repeatedly
          // waking up to ask costs battery for nothing.
          OutlinedButton(onPressed: onCheckAgain, child: const Text('Check again')),
        ],
      );
}
