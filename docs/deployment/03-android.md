# 03 — Building and distributing the Android app

**Verified:** debug builds have been built and run on the Android emulator (API 36, Google Play image), including registration, clock-in, clock-out and revocation against the API (Phase 18), and the on-device integration tests pass there. A **release** build has not been produced in this project, because it needs the organisation's signing key; the release configuration below is what the build script enforces.

---

## 1. Requirements

| Item | Value |
|---|---|
| Flutter | 3.47.x stable (Dart 3.13), as pinned during development |
| JDK | 17 or 21 |
| Android SDK | Platform and build-tools for the `compileSdk` the Flutter version selects |
| Minimum device | Android 9 (API 28, `minSdk = 28`) with a hardware-backed keystore. Devices without one cannot pass key attestation and cannot register |
| Package name | `com.altimateware.clockinxtra`. Change it before the first release if the organisation uses its own namespace: it is bound into every key attestation and cannot be changed later without re-registering every device |

## 2. The release signing key

The release build **fails on purpose** without the organisation's signing key: an APK signed with a debug key would not match what the server expects, and every registration would be refused.

1. Generate the key once, on a controlled machine, and store it in the organisation's secret store:

   ```bash
   keytool -genkeypair -v -keystore clockinxtra-release.jks -alias clockinxtra ^
       -keyalg RSA -keysize 3072 -validity 10000
   ```

2. On the build agent, create `mobile/android/key.properties` (already listed in `.gitignore`; never commit it):

   ```properties
   storeFile=C:/keys/clockinxtra-release.jks
   storePassword=<from the secret store>
   keyAlias=clockinxtra
   keyPassword=<from the secret store>
   ```

3. **Back the key up.** Losing it means a new package identity: every employee reinstalls and every device re-registers.

## 3. Tell the server which app to trust

The server accepts a registration only from an app whose package name **and** signing certificate match its configuration (the `attestationApplicationId` in the key attestation, which the app cannot forge). Take the certificate's SHA-256:

```bash
keytool -list -v -keystore clockinxtra-release.jks -alias clockinxtra
```

Copy the `SHA256:` value, remove the colons, and set it on the API as `Attestation__Android__ExpectedSigningCertificateDigests__0` (see `02-iis.md` §3). Add a second entry before a signing-key rotation so both are accepted during the changeover.

## 4. Build

The API address is compiled in. Release builds accept **only `https://`**; the cleartext switch is honoured in debug builds only.

```bash
cd mobile
flutter build apk --release --dart-define-from-file=dart_defines/deployed.json
```

or `flutter build appbundle --release ...` if the distribution channel wants an app bundle.

Build-time switches that exist, and where they apply:

| Define | Effect | Release build |
|---|---|---|
| `CLOCKINXTRA_API_BASE_URL` | API address | Required, must be `https://`. Held in `mobile/dart_defines/deployed.json` as `https://api.clockinxtra.xwoks.com`, which is what the deployed nginx serves and what the TLS certificate covers. An address is configuration, not a secret, so the file is committed; pass it with `--dart-define-from-file` rather than retyping it |
| `CLOCKINXTRA_ALLOW_CLEARTEXT` | Allows `http://` for a local development server | **Ignored** |
| `CLOCKINXTRA_ALLOW_COMPROMISED_DEVICE_FOR_DEVELOPMENT` | Ignores root indicators so AOSP/"Google APIs" emulator images can be used | **Ignored** |

On Windows build agents where Gradle fails with *Unable to establish loopback connection*, see the note in the repository README (the JDK's AF_UNIX socket cannot be created in some user `TEMP` folders; setting `TEMP`/`TMP` to a plain directory for the build works around it).

## 5. Server-side attestation material

These are provisioned on the API servers, not in the app:

- **Google's hardware attestation root certificate(s):** download from the *Key and ID attestation* page of the Android developer documentation, verify them out of band, save as PEM, and set `Attestation__Android__RootCertificatePemPath`. They are deliberately not fetched at runtime (§2.1).
- **Revocation status list:** Google publishes the list of revoked attestation keys over the internet. Whether a machine may fetch it periodically and copy it to the API servers is an organisation decision. Set `Attestation__Android__RevocationStatusListPath` to the copy. Until then, revocation is not checked, and every accepted registration records that it was not.

## 6. Distribution

The distribution channel is an organisation decision. The app needs no store services at runtime; it can be distributed by the organisation's own mobile device management, or as a signed APK. Whatever the channel:

- Only the release build signed with the key in §2 can register.
- Employees need location permission ("while using the app") and an authenticator app enrolled by an administrator.
- A first launch registers the phone; an administrator approves it in the portal (DEC-04, one active device per employee) before attendance is available.

## 7. On-device checks before a release

With a test device or emulator attached:

```bash
cd mobile
flutter test                                   # 85 unit and widget tests
flutter test integration_test -d <device-id>   # 6 on-device tests
```

The integration tests clear the app's secure storage and replace its signing key. Never run them on a phone someone clocks in with.

## 8. Known limitations to communicate

- **GPS is a proximity control, not proof of presence** (§65). The 5 m radius is below the accuracy many phones achieve indoors; the portal's validation-failure report shows how often accuracy is the reason for refusals (CON-01, OPEN-25).
- **Root detection is a signal, not a barrier** (§25). The server's key attestation (verified boot, hardware-backed key) is the control that holds.
- The app never queues attendance offline (§52): with no connection it says so, and nothing is recorded.
