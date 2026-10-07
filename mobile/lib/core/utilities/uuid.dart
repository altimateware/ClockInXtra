import 'dart:math';
import 'dart:typed_data';

/// Produces a random (version 4) UUID in the canonical hyphenated form.
///
/// Written here rather than taken from a package because it is fifteen lines
/// with one thing to get right, and the server parses it with `Guid.TryParse`,
/// which accepts exactly this form.
///
/// The source must be cryptographic. These values become idempotency keys, and a
/// predictable key would let one caller collide with another's in-flight
/// clock-in — the server keys its idempotency record on it (§53).
String newUuidV4([Random? random]) {
  final Random source = random ?? Random.secure();
  final Uint8List bytes = Uint8List(16);

  for (int i = 0; i < bytes.length; i++) {
    bytes[i] = source.nextInt(256);
  }

  // RFC 9562 §5.4: version in the high nibble of octet 6, variant 10x in the
  // two high bits of octet 8.
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  final String hex = <String>[
    for (final int byte in bytes) byte.toRadixString(16).padLeft(2, '0'),
  ].join();

  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}
