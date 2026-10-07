import 'package:flutter/foundation.dart';

/// Where the application finds its API, and on what terms.
///
/// **Not hard-coded** (§47). The address differs between a developer's machine,
/// a test environment and the production DMZ, and baking one into the binary
/// means rebuilding the app to move it. It is supplied at build time:
///
/// ```
/// flutter run --dart-define=CLOCKINXTRA_API_BASE_URL=https://attendance.example.com
/// ```
///
/// **The cleartext rule is enforced here rather than described in a comment.**
/// §39 requires HTTPS with certificate validation. A plain `http://` base URL is
/// refused outright unless the build is a debug build *and* the developer has
/// explicitly asked for it with a second define — two deliberate acts, neither
/// of which can happen in a release build, because [kReleaseMode] is a
/// compile-time constant and the check is unconditional.
@immutable
final class ApiEnvironment {
  /// Creates an environment, refusing an unsafe combination.
  ///
  /// Throws [StateError] rather than returning something unusable: a
  /// misconfigured build should fail loudly at startup, not at 08:00 in front of
  /// an employee.
  ApiEnvironment({
    required this.baseUri,
    this.connectTimeout = const Duration(seconds: 10),
    this.sendTimeout = const Duration(seconds: 20),
    this.receiveTimeout = const Duration(seconds: 20),
    bool allowCleartext = false,
  }) {
    if (!baseUri.isScheme('https')) {
      if (!allowCleartext || kReleaseMode) {
        throw StateError(
          'The API base URL must use https. Cleartext is permitted only in a '
          'debug build started with --dart-define=CLOCKINXTRA_ALLOW_CLEARTEXT=true.',
        );
      }
    }

    if (baseUri.host.isEmpty) {
      throw StateError('The API base URL has no host: $baseUri');
    }
  }

  /// Reads the environment from the values supplied at build time.
  factory ApiEnvironment.fromCompileTimeConfiguration() {
    const String configured = String.fromEnvironment(baseUrlKey);

    if (configured.isEmpty) {
      throw StateError(
        'No API base URL was configured. Build with '
        '--dart-define=$baseUrlKey=https://attendance.example.com',
      );
    }

    final Uri? parsed = Uri.tryParse(configured);

    if (parsed == null || !parsed.hasScheme) {
      throw StateError('The configured API base URL is not a valid URL: $configured');
    }

    return ApiEnvironment(
      baseUri: parsed,
      allowCleartext: const bool.fromEnvironment(allowCleartextKey),
    );
  }

  /// The build-time key naming the API root.
  static const String baseUrlKey = 'CLOCKINXTRA_API_BASE_URL';

  /// The build-time key that permits cleartext, honoured only in a debug build.
  static const String allowCleartextKey = 'CLOCKINXTRA_ALLOW_CLEARTEXT';

  /// The API root, without a trailing path segment.
  final Uri baseUri;

  /// How long to wait for a connection.
  final Duration connectTimeout;

  /// How long to wait while sending a request.
  final Duration sendTimeout;

  /// How long to wait for a response.
  final Duration receiveTimeout;

  /// The value the `Host` header will carry for a request to this API.
  ///
  /// **This must match what the server sees**, because the signature covers
  /// `@authority` and the server rebuilds it from the received `Host` header. The
  /// rule is the one `dart:io` applies: the port appears only when it is not the
  /// default for the scheme (`_HttpHeaders._updateHostHeader`).
  ///
  /// A reverse proxy that rewrites `Host` would break every signature, which is
  /// why the deployment guidance requires `preserveHostHeader` (§46).
  String get authority {
    final int defaultPort = baseUri.isScheme('https') ? 443 : 80;

    return baseUri.port == defaultPort
        ? baseUri.host
        : '${baseUri.host}:${baseUri.port}';
  }
}
