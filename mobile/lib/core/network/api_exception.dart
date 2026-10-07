import 'package:flutter/foundation.dart';

/// A failed API call, whether the server refused it or it never arrived.
///
/// The application treats both the same way, because §52 forbids pretending
/// otherwise: a clock-in that could not reach the server is **not** a clock-in,
/// and the app must say so rather than queue it or show it as done.
///
/// [code] is the server's stable code from §60 where there was a response, or
/// one of the local codes below where there was not. The distinction matters to
/// the UI — [isTransport] failures are worth a retry, a refusal usually is not.
@immutable
final class ApiException implements Exception {
  /// Creates the exception.
  const ApiException({
    required this.code,
    required this.message,
    this.correlationId,
    this.statusCode,
  });

  /// The request never reached the server.
  static const String networkUnavailable = 'NETWORK_UNAVAILABLE';

  /// The server did not answer in time. **The outcome is unknown**: the request
  /// may well have succeeded, which is exactly why clock-in carries an
  /// idempotency key (§53).
  static const String networkTimeout = 'NETWORK_TIMEOUT';

  /// The TLS handshake failed — an untrusted or mismatched certificate (§39).
  static const String tlsFailure = 'TLS_FAILURE';

  /// A response arrived but was not the shape the contract promises.
  static const String malformedResponse = 'MALFORMED_RESPONSE';

  /// This device has no registration key yet, so the call cannot be signed.
  static const String notRegistered = 'DEVICE_NOT_REGISTERED';

  /// The machine-readable code.
  final String code;

  /// A sentence safe to show a person.
  final String message;

  /// The server's correlation id, where one was returned.
  ///
  /// This is the single most useful thing an employee can quote to support: it
  /// joins what they saw to what the server logged, without exposing anything.
  final String? correlationId;

  /// The HTTP status, or null when nothing arrived.
  final int? statusCode;

  /// Whether the call failed below the application layer.
  bool get isTransport =>
      code == networkUnavailable || code == networkTimeout || code == tlsFailure;

  /// Whether the server said this device is not usable until someone acts.
  ///
  /// Each of these is a dead end for the employee — retrying changes nothing,
  /// and the app should route them to an explanation rather than a button.
  bool get requiresAdministrator =>
      code == 'DEVICE_NOT_REGISTERED' ||
      code == 'DEVICE_NOT_APPROVED' ||
      code == 'DEVICE_REVOKED' ||
      code == 'MFA_NOT_ENROLLED' ||
      code == 'ACTIVE_DEVICE_EXISTS' ||
      code == 'ATTESTATION_REJECTED';

  @override
  String toString() => 'ApiException($code, status: $statusCode): $message';
}
