import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../application/registration_controller.dart';

/// The form that registers this phone to an employee.
///
/// Holds only the text the employee is typing. The decisions — what to send,
/// what a refusal means, where to go next — are the controller's (§59).
class RegistrationView extends ConsumerStatefulWidget {
  /// Creates the view.
  const RegistrationView({super.key});

  @override
  ConsumerState<RegistrationView> createState() => _RegistrationViewState();
}

class _RegistrationViewState extends ConsumerState<RegistrationView> {
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

  Future<void> _submit() async {
    final String password = _password.text;

    // Cleared as soon as it has been read. The password never waits in a field
    // while the request runs, and is not there to be seen if registration fails
    // and the phone is handed to someone else (§24).
    _password.clear();

    await ref.read(registrationControllerProvider.notifier).submit(
          userId: _userId.text,
          password: password,
          authenticatorCode: _code.text,
        );

    _code.clear();
  }

  @override
  Widget build(BuildContext context) {
    final RegistrationState state = ref.watch(registrationControllerProvider);
    final TextTheme text = Theme.of(context).textTheme;

    return ListView(
      padding: const EdgeInsets.all(24),
      children: <Widget>[
        Text('Register this phone', style: text.headlineSmall),
        const SizedBox(height: 12),
        const Text(
          'Enter your user ID, password and the six-digit code from your authenticator app. '
          'An administrator then approves the phone before you can clock in.',
        ),
        const SizedBox(height: 24),
        TextField(
          controller: _userId,
          enabled: !state.submitting,
          decoration: const InputDecoration(labelText: 'User ID'),
          autocorrect: false,
          enableSuggestions: false,
          textInputAction: TextInputAction.next,
          maxLength: 64,
        ),
        TextField(
          controller: _password,
          enabled: !state.submitting,
          decoration: const InputDecoration(labelText: 'Password'),
          obscureText: true,
          autocorrect: false,
          enableSuggestions: false,
          textInputAction: TextInputAction.next,
        ),
        TextField(
          controller: _code,
          enabled: !state.submitting,
          decoration: const InputDecoration(labelText: 'Authenticator code'),
          keyboardType: TextInputType.number,
          inputFormatters: <TextInputFormatter>[FilteringTextInputFormatter.digitsOnly],
          maxLength: 6,
          onSubmitted: (_) => _submit(),
        ),
        if (state.failure != null) ...<Widget>[
          const SizedBox(height: 8),
          Text(
            describeRegistrationFailure(state.failure!),
            key: const Key('registration-failure'),
            style: text.bodyMedium?.copyWith(color: Theme.of(context).colorScheme.error),
          ),
        ],
        const SizedBox(height: 16),
        FilledButton(
          onPressed: state.submitting ? null : _submit,
          child: Text(state.submitting ? 'Registering…' : 'Register'),
        ),
      ],
    );
  }
}

/// The sentence for each registration failure.
String describeRegistrationFailure(RegistrationFailure failure) => switch (failure) {
      RegistrationFailure.incomplete =>
        'Enter your user ID, your password and the six-digit code.',
      RegistrationFailure.invalidCredentials =>
        'The details entered were not correct.',
      RegistrationFailure.accountLocked =>
        'Your account is temporarily locked after too many attempts. Please try again later.',
      RegistrationFailure.authenticatorNotEnrolled =>
        'No authenticator is set up for your account yet. Please contact your administrator.',
      RegistrationFailure.activeDeviceExists =>
        'You already have a registered phone. An administrator must approve a replacement.',
      RegistrationFailure.deviceNotVerified =>
        'This phone could not be verified as genuine. Please contact your administrator.',
      RegistrationFailure.hardwareKeyUnavailable =>
        'This phone cannot create the secure key the app needs, so it cannot be registered.',
      RegistrationFailure.expired =>
        'The registration took too long and expired. Please try again.',
      RegistrationFailure.serviceUnreachable =>
        'Cannot reach the attendance service. Check your connection and try again.',
      RegistrationFailure.unexpected =>
        'Something went wrong. Please try again.',
    };
