import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Values held on the device at rest.
///
/// Backed by the Android Keystore and the iOS Keychain, never by
/// `SharedPreferences` or `NSUserDefaults` — those are plain files readable on a
/// rooted or jailbroken handset (§10, §38).
///
/// **What deliberately never appears here:** the password, the TOTP secret, any
/// API key, and the device private key. The first two are never persisted at all;
/// the private key lives inside the secure element and cannot be read out even by
/// this application, which is the point of it.
///
/// The user identifier *is* stored, as §10 requires — and encrypting it changes
/// nothing about authorisation. The server never trusts a stored identifier
/// because it arrived encrypted; identity comes from the request signature, and a
/// stored identifier is only a convenience so the employee does not retype it.
abstract interface class SecureStore {
  /// The employee's sign-in identifier, or null before first use.
  Future<String?> readUserId();

  /// Stores the employee's sign-in identifier.
  Future<void> writeUserId(String userId);

  /// The device identifier the server issued at registration.
  Future<String?> readDevicePublicId();

  /// Stores the device identifier the server issued.
  Future<void> writeDevicePublicId(String devicePublicId);

  /// Clears everything, for sign-out or a failed integrity check.
  Future<void> clear();
}

/// Platform secure storage implementation.
final class PlatformSecureStore implements SecureStore {
  /// Creates the store.
  PlatformSecureStore([FlutterSecureStorage? storage])
      : _storage = storage ??
            const FlutterSecureStorage(
              // Android needs no option to reach the Keystore: version 11 of
              // this plugin always stores through it. Earlier versions required
              // an explicit flag, which is why older guidance mentions one.
              aOptions: AndroidOptions(
                storageNamespace: 'clockinxtra',
              ),
              iOptions: IOSOptions(
                // Not synchronised to iCloud and not restored to a different
                // handset: a value that followed a backup onto another device
                // would undermine the binding the server relies on.
                accessibility: KeychainAccessibility.first_unlock_this_device,
                synchronizable: false,
              ),
            );

  static const String _userIdKey = 'clockinxtra.userId';
  static const String _devicePublicIdKey = 'clockinxtra.devicePublicId';

  final FlutterSecureStorage _storage;

  @override
  Future<String?> readUserId() => _storage.read(key: _userIdKey);

  @override
  Future<void> writeUserId(String userId) =>
      _storage.write(key: _userIdKey, value: userId);

  @override
  Future<String?> readDevicePublicId() => _storage.read(key: _devicePublicIdKey);

  @override
  Future<void> writeDevicePublicId(String devicePublicId) =>
      _storage.write(key: _devicePublicIdKey, value: devicePublicId);

  @override
  Future<void> clear() => _storage.deleteAll();
}
