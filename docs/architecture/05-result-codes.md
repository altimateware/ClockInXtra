# 05 — Result Code Catalogue

| Item | Value |
|---|---|
| Document | ClockInXtra — Result and error code catalogue |
| Phase | 5–6 (shared by database, application and API layers) |
| Version | 0.1 |
| Date | 2026-09-12 |

This catalogue is the single source of truth for three things that must stay in step:

1. `@ResultCode` returned by every stored procedure (design decision DB-10).
2. The `AttendanceResultCode` enum in `Attendance.Domain`.
3. The `code` string in the API error body (`Claude.md` §34 and §60).

A stored procedure never returns an expected business outcome by raising an
error. It sets `@ResultCode` and returns. `THROW` is reserved for genuinely
unexpected failures, which the API maps to `INTERNAL_ERROR` after logging.

---

## 1. Numbering scheme

| Range | Meaning |
|---|---|
| 0 | Success |
| 1000–1009 | Request-level failures |
| 1010–1019 | Identity and credentials |
| 1020–1029 | Device |
| 1030–1039 | Request integrity, replay and idempotency |
| 1040–1049 | Location |
| 1050–1059 | Attendance rules and state |
| 1060–1069 | Registration and enrolment |
| 1070–1079 | Administration and configuration |
| 1080–1089 | Maintenance and integrity verification |
| 1090–1099 | Internal |

---

## 2. Catalogue

| Code | Constant | API `code` | HTTP | Returned to mobile client? | Meaning |
|---|---|---|---|---|---|
| 0 | `Success` | — | 200 | — | The operation completed |
| 1001 | `InvalidRequest` | `INVALID_REQUEST` | 400 | Yes | Validation failed, or the body digest did not match |
| 1002 | `Unauthorized` | `UNAUTHORIZED` | 401 | Yes | Missing, malformed or invalid signature; unknown key id |
| 1003 | `Forbidden` | `FORBIDDEN` | 403 | Yes | Authenticated device is not permitted to act for this subject |
| 1004 | `RateLimited` | `RATE_LIMITED` | 429 | Yes | Endpoint rate limit exceeded |
| 1005 | `AppVersionUnsupported` | `APP_VERSION_UNSUPPORTED` | 426 | Yes | Below `Mobile.MinimumAppVersion` |
| 1010 | `InvalidCredentials` | `INVALID_CREDENTIALS` | 401 | Yes | Wrong user, password **or** OTP. Deliberately indistinguishable (CON-09 / OPEN-38) |
| 1011 | `AccountLocked` | `ACCOUNT_LOCKED` | 423 | Yes | Lockout threshold reached |
| 1012 | `InvalidOtp` | *(mapped to `INVALID_CREDENTIALS`)* | 401 | **No** | Internal only. Written to `audit.SecurityEvent`, never returned while `Security.CollapseCredentialErrorCodes` is true |
| 1013 | `OtpReplayed` | *(mapped to `INVALID_CREDENTIALS`)* | 401 | **No** | Internal only. The time step was already used |
| 1014 | `UserInactive` | `INVALID_CREDENTIALS` | 401 | Yes (as credentials) | Employee is inactive or suspended. Not distinguished externally, to avoid account enumeration |
| 1015 | `MfaNotEnrolled` | `MFA_NOT_ENROLLED` | 403 | Yes | No active authenticator enrolment exists for the employee |
| 1020 | `DeviceNotRegistered` | `DEVICE_NOT_REGISTERED` | 401 | Yes | No device matches the key id |
| 1021 | `DeviceNotApproved` | `DEVICE_NOT_APPROVED` | 403 | Yes | Registration is awaiting administrator approval |
| 1022 | `DeviceRevoked` | `DEVICE_REVOKED` | 403 | Yes | The device has been revoked |
| 1023 | `DeviceNotBoundToUser` | `FORBIDDEN` | 403 | Yes | The device is bound to a different employee |
| 1024 | `ActiveDeviceAlreadyExists` | `ACTIVE_DEVICE_EXISTS` | 409 | Yes | DEC-04: the employee already has an active device; approval of a replacement revokes it |
| 1025 | `AttestationRejected` | `ATTESTATION_REJECTED` | 403 | Yes | Attestation missing, malformed, or did not meet the required level |
| 1030 | `ReplayedRequest` | `REPLAYED_REQUEST` | 401 | Yes | The signature nonce has already been used |
| 1031 | `ClockSkew` | `CLOCK_SKEW` | 401 | Yes | Signature `created` is outside `Security.SignatureSkewSeconds` |
| 1032 | `IdempotencyKeyReuse` | `IDEMPOTENCY_KEY_REUSED` | 409 | Yes | The same key was used for a different request |
| 1033 | `IdempotentReplay` | — | 200 | — | Not an error: the stored result of the original request is returned |
| 1034 | `IdempotentInProgress` | `REQUEST_IN_PROGRESS` | 409 | Yes | The original request carrying this key is still running. The client retries shortly, or calls `user/status` to reconcile. It must not start a second attendance transaction |
| 1040 | `LocationNotAllowed` | `LOCATION_NOT_ALLOWED` | 403 | Yes | No approved office within its configured radius |
| 1041 | `LocationAccuracyInsufficient` | `LOCATION_ACCURACY_INSUFFICIENT` | 403 | Yes | Reported accuracy is worse than policy allows (the radius is never widened) |
| 1042 | `LocationSourceUntrusted` | `LOCATION_SOURCE_UNTRUSTED` | 403 | Yes | Mocked (Android) or software-simulated (iOS) position |
| 1043 | `OfficeLocationInactive` | `LOCATION_NOT_ALLOWED` | 403 | Yes | The matched office was disabled between validation and the transaction |
| 1050 | `AlreadyClockedIn` | `ALREADY_CLOCKED_IN` | 409 | Yes | A record already exists for this employee and attendance day |
| 1051 | `NotClockedIn` | `NOT_CLOCKED_IN` | 409 | Yes | No open record to close |
| 1052 | `AttendanceWindowClosed` | `ATTENDANCE_WINDOW_CLOSED` | 409 | Yes | Outside the configured clock-in or clock-out window, with the action set to Reject |
| 1053 | `AttendanceNotConfigured` | `ATTENDANCE_NOT_CONFIGURED` | 503 | Yes | A mandatory business setting is still unset (OPEN-5/6/7). **The system refuses rather than assuming a value** |
| 1054 | `AttendanceOperationFailed` | `ATTENDANCE_OPERATION_FAILED` | 500 | Yes | The transaction could not be completed |
| 1055 | `AlreadyClockedOut` | `NOT_CLOCKED_IN` | 409 | Yes | The record for the day is already closed |
| 1060 | `ChallengeInvalid` | `INVALID_REQUEST` | 400 | Yes | Unknown or already consumed registration challenge |
| 1061 | `ChallengeExpired` | `INVALID_REQUEST` | 400 | Yes | Challenge lifetime exceeded |
| 1062 | `RegistrationPendingApproval` | — | 202 | — | Not an error: registration accepted, awaiting approval |
| 1063 | `PublicKeyAlreadyRegistered` | `INVALID_REQUEST` | 409 | Yes | That public key is already bound to a device |
| 1064 | `MfaAlreadyEnrolled` | `MFA_ALREADY_ENROLLED` | 409 | Admin only | The employee already has a pending or active authenticator. Replacing it is a deliberate act requiring `Mfa.Reset`, not a side effect of enrolling again |
| 1070 | `NotFound` | `NOT_FOUND` | 404 | Admin only | Entity does not exist |
| 1071 | `ConcurrencyConflict` | `CONCURRENCY_CONFLICT` | 409 | Admin only | The row changed since it was loaded (`rowversion` mismatch) |
| 1072 | `DuplicateName` | `DUPLICATE_NAME` | 409 | Admin only | Unique name already in use |
| 1073 | `SettingNotConfirmed` | `SETTING_NOT_CONFIRMED` | 409 | Admin only | The value still awaits business confirmation |
| 1074 | `SeparationOfDutiesViolation` | `FORBIDDEN` | 403 | Admin only | An approver cannot approve their own request |
| 1075 | `CorrectionsDisabled` | `FORBIDDEN` | 403 | Admin only | `Attendance.AllowCorrections` is not enabled. It is `true` in the configured system (DEC-08); this code means corrections have been switched off |
| 1076 | `InvalidTimeZone` | `INVALID_REQUEST` | 400 | Admin only | The value is not present in `sys.time_zone_info` |
| 1077 | `LastAdministratorManager` | `FORBIDDEN` | 403 | Admin only | The change would leave no active administrator able to manage administrators |
| 1080 | `LedgerVerificationFailed` | — | — | Never (maintenance only) | `job.usp_Maintenance_VerifyLedger`: the audit ledger does not match an exported digest. A security incident (TH-40), not an application error |
| 1090 | `InternalError` | `INTERNAL_ERROR` | 500 | Yes | Unexpected failure. Details are logged with the correlation ID, never returned |

---

## 3. Rules for using these codes

1. **Never return a code the client should not see.** 1012, 1013 and 1033 are internal; the API maps them before responding.
2. **Never include SQL text, constraint names, stack traces or internal identifiers** in the message accompanying a code (§34).
3. **Every failure that is security-relevant writes an `audit.SecurityEvent` row** with the precise `ReasonCode`, even when the response is collapsed to `INVALID_CREDENTIALS`.
4. **Codes are additive.** Once released, a code's meaning never changes, because mobile clients in the field depend on it (§35).
5. A unique-key violation on `UX_Attendance_MobileUserId_AttendanceDate` is caught inside the procedure and returned as 1050, never surfaced as SQL error 2627.
