import 'package:clockinxtra/core/config/api_environment.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('authority', () {
    // These four cases are the whole of the rule dart:io applies when it writes
    // the Host header (_HttpHeaders._updateHostHeader). The server rebuilds
    // @authority from that header, so a mismatch here breaks every signature
    // while looking exactly like a wrong key.
    test('omits the default port for https', () {
      expect(
        ApiEnvironment(baseUri: Uri.parse('https://attendance.example.com')).authority,
        'attendance.example.com',
      );
    });

    test('omits an explicitly written default port', () {
      expect(
        ApiEnvironment(baseUri: Uri.parse('https://attendance.example.com:443')).authority,
        'attendance.example.com',
      );
    });

    test('includes a non-default port', () {
      expect(
        ApiEnvironment(baseUri: Uri.parse('https://attendance.example.com:8443')).authority,
        'attendance.example.com:8443',
      );
    });

    test('omits port 80 for http', () {
      expect(
        ApiEnvironment(
          baseUri: Uri.parse('http://10.0.2.2'),
          allowCleartext: true,
        ).authority,
        '10.0.2.2',
      );
    });

    test('includes a non-default http port', () {
      expect(
        ApiEnvironment(
          baseUri: Uri.parse('http://10.0.2.2:5099'),
          allowCleartext: true,
        ).authority,
        '10.0.2.2:5099',
      );
    });
  });

  group('transport safety', () {
    test('refuses cleartext unless it was explicitly permitted', () {
      // §39. The default must be the safe one, so that forgetting to think
      // about it produces HTTPS rather than plaintext credentials.
      expect(
        () => ApiEnvironment(baseUri: Uri.parse('http://attendance.example.com')),
        throwsA(isA<StateError>()),
      );
    });

    test('permits cleartext in a debug build when asked', () {
      // Tests run in debug, which is the only mode where this is reachable at
      // all — kReleaseMode makes the check unconditional in a release build.
      expect(
        ApiEnvironment(
          baseUri: Uri.parse('http://10.0.2.2:5099'),
          allowCleartext: true,
        ).baseUri.scheme,
        'http',
      );
    });

    test('refuses a URL with no host', () {
      expect(
        () => ApiEnvironment(baseUri: Uri.parse('https:///api')),
        throwsA(isA<StateError>()),
      );
    });
  });
}
