# 04 — iOS: status and what deployment requires

## Status: not deployable yet

The iOS app **builds from the same Flutter code but cannot be used**, and it is designed to fail safely rather than work partially:

| Capability | Android | iOS |
|---|---|---|
| Secure storage (Keychain) | Done (flutter_secure_storage) | Done (same plugin, Keychain-backed) |
| Location | Done | Done (geolocator; `NSLocationWhenInUseUsageDescription` is set) |
| Hardware signing key + attestation | `DeviceKeyHandler` (Kotlin, Android Keystore + key attestation) | **Missing**: needs Swift code using a Secure Enclave key and **App Attest** (`DCAppAttestService`), DEC-05 |
| Jailbreak signals | `IntegrityHandler` (Kotlin) | **Missing**: needs a Swift handler on the same method channel |
| Server verification | Android key attestation verifier | Apple App Attest verifier exists on the server and is unit tested; it has never received a real App Attest object |

Without the native channels, the integrity check reports *unavailable* and the startup screen refuses to continue. That is deliberate: an unimplemented check must not ship as a silent pass (§25).

**Why it is not done:** iOS native code can only be built, signed and run on macOS with Xcode, and no Mac was available to this project (CON-06, OPEN-42). Writing the Swift without being able to compile or run it would put unverified security code into the product, which §64 rules out.

## What finishing iOS requires

1. **A macOS build machine** with Xcode and an Apple Developer Program membership held by the organisation.
2. **Swift implementations** of the two method channels, matching the Android contracts exactly:
   - `com.contoso.clockinxtra/integrity` → `inspect` returning `{indicators: [String], isEmulator: Bool}`.
   - `com.contoso.clockinxtra/device_key`, used by `PlatformDeviceKeyService` (`hasKey`, `generateKey(challenge)` returning `publicKey` (65-byte uncompressed P-256), `attestation` (the App Attest CBOR object) and `keyId`, `sign(data)` returning 64-byte raw r‖s, `deleteKey`).
3. **The App Attest capability** on the App ID and in the entitlements, with `production` environment for release builds.
4. **Server configuration:** `Attestation__Apple__RootCertificatePemPath` (Apple's App Attestation root CA, downloaded and verified out of band), `Attestation__Apple__TeamId`, `Attestation__Apple__BundleId`, and `AllowDevelopmentEnvironment=false` in production.
5. **Run the integration tests on a real iPhone:** `flutter test integration_test -d <iphone>`. They cover both channels and must pass before a release; App Attest does not work in the simulator.

## Build and distribution, once implemented

```bash
cd mobile
flutter build ipa --release --dart-define=CLOCKINXTRA_API_BASE_URL=https://attendance.contoso.com
```

Distribution is an organisation decision: Apple Business Manager with the organisation's MDM, or the Apple Developer Enterprise Program where the organisation qualifies. iOS apps cannot be sideloaded freely as Android APKs can.
