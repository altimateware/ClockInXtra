# ClockInXtra mobile app

The employee app: it checks the device and location at startup, registers the phone once, and clocks in and out. Flutter, feature-first Clean Architecture, Riverpod for state.

```
lib/
  core/          config, network (signed API client), security (device key), device (integrity,
                 app identity), location, storage (secure store), di (providers)
  features/
    app_initialization/   the §8.1 startup sequence and its screen
    authentication/       device registration
    attendance/           status, clock-in, clock-out
    diagnostics/          debug builds only; compiled out of release
android/app/src/main/kotlin/…   IntegrityHandler.kt, DeviceKeyHandler.kt (method channels)
test/               unit and widget tests (fakes in test/support/fakes.dart)
integration_test/   on-device tests: Keystore, hardware key, integrity channel, full flow
```

## Run in development

Against a local API started with its `http` profile on port 5047 (see the repository README), on the Android emulator, where the host machine is `10.0.2.2`:

```bash
flutter run \
  --dart-define=CLOCKINXTRA_API_BASE_URL=http://10.0.2.2:5047 \
  --dart-define=CLOCKINXTRA_ALLOW_CLEARTEXT=true
```

Add `--dart-define=CLOCKINXTRA_ALLOW_COMPROMISED_DEVICE_FOR_DEVELOPMENT=true` on an AOSP or "Google APIs" emulator image, which reports root indicators. All three switches except the URL are ignored by release builds, and a release build accepts only `https://`.

Registration on an emulator is refused by a correctly configured server, because an emulator's key is software-backed; that is the attestation check working.

## Test

```bash
flutter analyze
flutter test                                    # unit and widget tests
flutter test integration_test -d <device-id>    # on a test device or emulator only
```

The integration tests clear the app's secure storage and replace its signing key.

## Build for release

See `docs/deployment/03-android.md` (signing key, server trust settings, build command) and `docs/deployment/04-ios.md` (iOS status and what finishing it requires).

## Rules the code keeps

- The server is the source of truth for attendance state; nothing is shown as done that the server did not confirm, and nothing is queued offline (§52).
- After an unknown outcome the app asks the server what happened; it never resends a clock-in (the authenticator code is single-use, and the idempotency key is per attempt).
- The password and authenticator code are sent once and never stored, cached or logged (§24).
- A check that cannot run is a failure: an unavailable integrity check blocks startup.
