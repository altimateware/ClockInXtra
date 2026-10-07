# Claude.md — Enterprise Clock-In / Clock-Out Attendance Solution

## 1. Role and Operating Instructions

You are acting as a **Senior Solutions Architect, Senior C#/.NET Engineer, Senior Flutter Engineer, Database Architect, and Application Security Engineer**.

Your responsibility is to design and implement a production-grade **Clock-In / Clock-Out (Attendance) solution** consisting of:

1. A **Flutter mobile application** for employees to clock in and clock out.
2. A **C#/.NET backend administrative application** for configuration, user administration, location management, reporting, and operational administration.
3. A **C#/.NET REST API** consumed by the mobile application.
4. An **MS SQL Server database** accessed through **Dapper and stored procedures only**.

The solution must be suitable for an enterprise environment and must be designed with security, auditability, maintainability, availability, observability, and operational support in mind.

---

# 2. Non-Negotiable Engineering Rules

## 2.1 No Cloud

Do not use cloud services.

The complete solution must be deployable on infrastructure controlled by the organization, such as:

- Windows Server
- IIS
- Microsoft SQL Server
- Active Directory where appropriate
- Internal network infrastructure
- Organization-controlled certificates/PKI
- Organization-controlled SMTP/SMS infrastructure if such services are later required

Do not introduce:

- Azure
- AWS
- Google Cloud
- Firebase
- Supabase
- Cloud-hosted authentication
- Cloud-hosted databases
- Cloud-hosted storage
- Cloud-hosted telemetry
- SaaS dependencies

If a feature normally uses a cloud service, design an **on-premises alternative**.

---

# 3. Technology Stack

## 3.1 Mobile

Use:

- Dart
- Flutter
- Flutter stable channel
- Clean Architecture
- Feature-first architecture
- Android
- iOS

The mobile application must use platform-native capabilities through Flutter plugins where required for:

- Location
- Secure storage
- Device/app integrity
- Network security
- Device identification
- Application lifecycle
- Local authentication if later enabled

Do not use Flutter Web for the attendance mobile application.

---

## 3.2 Backend

Use Microsoft technologies wherever appropriate.

Preferred stack:

- C#
- ASP.NET Core
- .NET LTS version appropriate to the implementation date
- ASP.NET Core Web API
- ASP.NET Core MVC/Razor for the administration portal
- Dapper
- Microsoft SQL Server
- IIS for production hosting
- Serilog or another mature structured logging solution
- ASP.NET Core built-in dependency injection
- Options pattern
- Health checks
- OpenAPI/Swagger for API documentation

Do not introduce Entity Framework Core.

---

# 4. Database Rules

The database must be Microsoft SQL Server.

## Absolutely prohibited

- Entity Framework
- EF Core
- LINQ-to-SQL
- Inline SQL inside C# code
- Dynamic SQL unless there is a demonstrated and unavoidable database requirement
- SQL embedded in repositories
- SQL embedded in controllers
- SQL embedded in services

## Required

All database access must use:

**C# → Repository → Dapper → Stored Procedure → SQL Server**

Every database operation must be implemented through a stored procedure.

Generate actual stored procedures.

Never create:

- `TODO`
- `IMPLEMENT`
- `YOUR_SQL_HERE`
- placeholder stored procedures
- pseudo-code SQL
- incomplete CRUD procedures

Where database functionality is required, provide the complete SQL implementation.

---

# 5. Clean Architecture

Use Clean Architecture.

Recommended backend structure:

```text
src/
 ├── Attendance.Domain/
 │   ├── Entities/
 │   ├── ValueObjects/
 │   ├── Enums/
 │   ├── Exceptions/
 │   └── Interfaces/
 │
 ├── Attendance.Application/
 │   ├── Abstractions/
 │   ├── DTOs/
 │   ├── Features/
 │   ├── Validators/
 │   ├── Behaviors/
 │   └── Services/
 │
 ├── Attendance.Infrastructure/
 │   ├── Persistence/
 │   │   ├── Dapper/
 │   │   ├── Repositories/
 │   │   └── Connection/
 │   ├── Security/
 │   ├── Cryptography/
 │   ├── Identity/
 │   ├── Logging/
 │   └── Services/
 │
 ├── Attendance.Api/
 │   ├── Controllers/
 │   ├── Middleware/
 │   ├── Filters/
 │   ├── Configuration/
 │   └── Program.cs
 │
 └── Attendance.Admin/
     ├── Controllers/
     ├── Views/
     ├── ViewModels/
     ├── Middleware/
     └── Program.cs
```

Dependency direction must remain inward:

```text
API/Admin
   ↓
Application
   ↓
Domain

Infrastructure → Application/Domain
```

Domain must not depend on Infrastructure.

---

# 6. Flutter Architecture

Use feature-first Clean Architecture.

Recommended structure:

```text
lib/
 ├── core/
 │   ├── constants/
 │   ├── errors/
 │   ├── network/
 │   ├── security/
 │   ├── storage/
 │   ├── location/
 │   ├── device/
 │   ├── routing/
 │   ├── widgets/
 │   └── utilities/
 │
 ├── features/
 │   ├── app_initialization/
 │   │   ├── data/
 │   │   ├── domain/
 │   │   └── presentation/
 │   │
 │   ├── location_validation/
 │   │   ├── data/
 │   │   ├── domain/
 │   │   └── presentation/
 │   │
 │   ├── authentication/
 │   │   ├── data/
 │   │   ├── domain/
 │   │   └── presentation/
 │   │
 │   ├── attendance/
 │   │   ├── data/
 │   │   ├── domain/
 │   │   └── presentation/
 │   │
 │   └── settings/
 │       ├── data/
 │       ├── domain/
 │       └── presentation/
 │
 └── main.dart
```

Use an appropriate, actively maintained state-management solution. Select it based on current Flutter ecosystem facts rather than assumption.

---

# 7. Functional Overview

The solution has three major actors:

```text
Employee
   │
   ▼
Flutter Mobile App
   │
   ▼
Secure Attendance API
   │
   ▼
SQL Server

Administrator
   │
   ▼
Administrative Web Application
   │
   ▼
Application Layer
   │
   ▼
SQL Server
```

---

# 8. Mobile Application Requirements

## 8.1 Initial App Startup

When the application starts:

1. Perform application integrity/security checks.
2. Detect whether the device is rooted/jailbroken.
3. Obtain the device/application identifier using a platform-appropriate mechanism.
4. Obtain the current geolocation.
5. Send:
   - Device ID
   - Latitude
   - Longitude
   - Appropriate location accuracy metadata
   - Application version
   - Device/platform information where justified
6. Call the **Validate Location API**.
7. If validation succeeds:
   - Continue application initialization.
8. If validation fails:
   - Display an appropriate message.
   - Do not expose the attendance functionality.
   - Do not continue to the normal home screen.

Do not claim that GPS can prove a device is physically inside an office with absolute certainty.

The location check must be treated as a proximity control.

---

# 9. Location Validation

The API must determine whether the submitted coordinates are within **5 metres** of an approved office location.

For each configured office location, store at minimum:

- Office ID
- Office name
- Latitude
- Longitude
- Allowed radius
- Status
- Created date
- Updated date

Although the initial requirement is 5 metres, design the database so the radius can be configured per location.

Default:

```text
AllowedRadiusMeters = 5
```

The distance must be calculated using a correct geodesic distance calculation, such as the Haversine formula or an equivalent validated implementation.

Do not use simplistic Euclidean distance on latitude/longitude degrees.

The API response should communicate:

- Valid/invalid
- Location identifier where appropriate
- Failure reason code
- Correlation ID

Do not expose unnecessary internal location data to the mobile client.

---

# 10. User Identity on Mobile

During the first authorized use of the application:

1. Obtain the employee's user ID.
2. Store it securely on the device.
3. Do not store it in plain SharedPreferences.
4. Use platform secure storage backed by:
   - Android Keystore
   - iOS Keychain

The user ID must be protected at rest.

Important:

**Encryption of the user ID does not replace server-side authentication.**

The API must never trust a locally stored user ID merely because it was encrypted.

The server must independently validate authorization.

---

# 11. User Status

After successful application/location initialization:

Call:

```text
GET/POST equivalent:
IsClockedIn / UserStatus
```

Input:

- User ID
- Device ID

The API checks whether the user has already clocked in for the current attendance day.

If already clocked in:

```text
Display Clock-Out
```

If not clocked in:

```text
Display Clock-In
```

The server is the source of truth.

Do not determine attendance state only from local application state.

---

# 12. Clock-In

Clock-In requires:

- User ID
- Password
- 6-digit authenticator token
- Device ID
- Latitude
- Longitude
- Location accuracy information where available

The API must:

1. Validate request integrity.
2. Validate the device.
3. Validate the user.
4. Validate password.
5. Validate the six-digit authenticator token.
6. Validate location.
7. Validate attendance business rules.
8. Check for an existing clock-in for the applicable attendance day.
9. Create the attendance record atomically.
10. Return a successful response.
11. Mobile application changes state to Clock-Out.

---

# 13. Authenticator / MFA

The requirement specifies a six-digit token generated from an authenticator application.

Do not assume a specific authenticator application without documenting the protocol.

Prefer a standards-based implementation such as:

- TOTP
- RFC 6238

Where TOTP is selected:

- Secrets must never be stored in plaintext.
- Secrets must be encrypted at rest.
- Secrets must never be logged.
- Verification must be performed server-side.
- Implement appropriate time-window tolerance.
- Implement replay protection where appropriate.
- Implement brute-force/rate-limit protection.
- Provide controlled enrollment and reset processes.

Do not invent a proprietary OTP mechanism when TOTP can satisfy the requirement.

---

# 14. Clock-Out

Clock-Out requires:

- User ID
- Device ID
- Latitude
- Longitude
- Location accuracy metadata where available

The API must:

1. Validate request integrity.
2. Validate device.
3. Validate user authorization.
4. Validate location.
5. Determine the user's open attendance record.
6. Ensure the user is actually clocked in.
7. Record clock-out timestamp.
8. Calculate the duration where appropriate.
9. Persist the operation atomically.
10. Return the resulting status.

The server must determine the official timestamp.

Do not trust a client-supplied clock timestamp.

---

# 15. Attendance Business Rules

Design the system so these rules are configurable:

- Clock-in opening time
- Clock-in closing time
- Clock-out opening time
- Grace period if approved
- Attendance day definition
- Duplicate clock-in handling
- Duplicate clock-out handling
- Whether early clock-in is permitted
- Whether late clock-in is permitted
- Whether clock-out before configured time is permitted
- Whether administrators can correct attendance
- Whether corrections require approval

The initial explicit requirement is:

- Setting of when clock-in time ends
- Setting of when clock-out should start

Do not silently invent additional business rules. Where additional rules are technically necessary, make them configurable and clearly document them.

---

# 16. Administrative Application

The administrative application must provide secure form-based authentication.

Features:

## 16.1 Authentication

- Login
- Logout
- Secure password handling
- Password hashing using a modern password-hashing algorithm
- Account lockout/rate limiting
- Session expiration
- Secure cookies
- CSRF protection
- Authorization
- Audit logging

Do not store administrator passwords in plaintext.

Do not implement password encryption as a substitute for password hashing.

---

# 17. Administrative User Management

Provide administration for:

- Mobile users
- Administrators
- Roles
- Permissions
- User activation/deactivation
- Device association
- MFA enrollment/reset
- User profile

Mobile user profile should support basic details such as:

- User ID
- Employee number where applicable
- First name
- Last name
- Email
- Phone
- Department where applicable
- Job title where applicable
- Status
- Assigned device(s)
- Created date
- Updated date

Do not invent organizational fields as mandatory requirements.

---

# 18. Device Management

Design a device registration/association mechanism.

A device record should be capable of storing:

- Device ID
- User ID
- Platform
- Device/app metadata where necessary
- Registration date
- Last-seen date
- Status
- Revoked date
- Revocation reason

Support device revocation.

A revoked device must not be able to perform attendance transactions.

Do not rely exclusively on a hardware identifier because modern mobile operating systems restrict access to permanent hardware identifiers.

The implementation must use identifiers permitted by Android and iOS and clearly document their lifecycle and limitations.

---

# 19. Office Location Administration

Administrators must be able to:

- Create office locations
- Edit office locations
- Enable/disable office locations
- Configure latitude
- Configure longitude
- Configure allowed radius
- View registered locations
- Audit changes

Validate latitude and longitude ranges.

Latitude:

```text
-90 to +90
```

Longitude:

```text
-180 to +180
```

The UI should clearly display the configured location and radius.

---

# 20. Reporting

Provide reports for:

- Daily attendance
- User attendance
- Department attendance where department exists
- Clock-in exceptions
- Missing clock-outs
- Late arrivals
- Early/late clock-outs where applicable
- Attendance duration
- Location validation failures
- Device validation failures
- Administrative activities
- Attendance corrections

Reports must support filtering by appropriate criteria such as:

- Date range
- User
- Department
- Office
- Status

Do not claim a report is required if it is not supported by the data model.

---

# 21. API Architecture

Use ASP.NET Core Web API.

Suggested endpoints:

```text
POST /api/v1/mobile/location/validate
POST /api/v1/mobile/user/status
POST /api/v1/mobile/attendance/clock-in
POST /api/v1/mobile/attendance/clock-out
```

Additional endpoints may be introduced where required for:

- Device registration
- MFA enrollment
- App configuration
- Health checks
- Version management

Do not expose unnecessary administrative endpoints through the mobile API.

---

# 22. API Security

Implement appropriate API security including:

- HTTPS/TLS
- HSTS where appropriate
- Authentication
- Authorization
- Rate limiting
- Request validation
- Response validation
- Anti-replay controls where required
- Request correlation IDs
- Structured security logging
- Secure headers
- Input size limits
- Timeout controls
- Abuse detection
- Error sanitization
- No sensitive information in logs

Do not rely solely on TLS.

---

# 23. Request/Response Encryption

The requirement states:

> All endpoints request and response should be encrypted.

HTTPS/TLS must be mandatory for transport encryption.

If application-layer payload encryption is additionally required, implement a well-defined envelope-encryption design.

Do not invent custom cryptography.

If application-level encryption is implemented:

- Use established cryptographic primitives.
- Use authenticated encryption such as AES-GCM where symmetric encryption is appropriate.
- Use secure key management.
- Never hard-code encryption keys.
- Do not store long-term secrets in the mobile application source code.
- Define key rotation.
- Define key provisioning.
- Define key compromise/revocation procedures.
- Protect against replay.
- Include protocol versioning.
- Include nonce/IV handling correctly.
- Include authentication tags.
- Do not encrypt passwords with reversible encryption merely to transmit them; TLS already protects transport.

Before implementing application-level encryption, explicitly document why TLS alone is insufficient for the organization's threat model.

---

# 24. Password Handling

For employee Clock-In:

The password is supplied to the API because the specified requirement requires password validation.

Do not:

- Log the password.
- Store the password.
- Return the password.
- Include it in query strings.
- Persist it in application state unnecessarily.
- Cache it locally.

The API should validate credentials against the organization's authoritative identity source where one exists.

Do not assume Active Directory is required unless confirmed as an actual requirement.

If no external identity provider is available, use a secure local credential model with proper password hashing.

---

# 25. Device Integrity

The mobile application must not operate on:

- Rooted Android devices
- Jailbroken iOS devices

Implement platform-appropriate integrity detection.

Important:

Client-side root/jailbreak detection is a security signal, not an absolute security boundary.

The server must also apply device registration and risk controls.

Do not claim that a mobile app can guarantee that a device is never compromised.

---

# 26. Location Security

Location must be treated as security-sensitive data.

Do not log exact coordinates unnecessarily.

Where coordinates must be audited:

- Define retention policy.
- Protect access.
- Encrypt data at rest where appropriate.
- Restrict access through authorization.
- Avoid exposing exact historical coordinates to unauthorized administrators.

Consider location accuracy.

If the device reports poor accuracy, define whether the request should be rejected or subjected to additional controls.

Do not assume the requirement permits bypassing the 5-metre rule when GPS accuracy is poor.

Make the behavior configurable only if approved by the business owner.

---

# 27. SQL Server Data Model

Design a normalized relational model.

At minimum consider entities for:

```text
MobileUser
Administrator
Role
Permission
RolePermission
AdministratorRole
Device
OfficeLocation
MfaCredential
Attendance
AttendanceEvent
ApplicationSetting
AuditLog
SecurityEvent
```

Do not create all tables merely because they are listed above.

Evaluate every entity against the actual requirements and explain why it exists.

Use:

- Primary keys
- Foreign keys
- Unique constraints
- Check constraints
- Appropriate indexes
- Rowversion where concurrency protection is required
- UTC timestamps for persisted system timestamps unless there is a documented reason otherwise

---

# 28. Attendance Data Integrity

Attendance operations must be transactional.

Prevent:

- Duplicate active clock-ins
- Duplicate clock-outs
- Concurrent clock-in race conditions
- Clock-out without clock-in
- Clock-in using revoked device
- Clock-in outside allowed location
- Unauthorized user/device combinations

Use database constraints and transactional stored procedures where appropriate.

Do not rely solely on C# checks for concurrency-sensitive rules.

---

# 29. Stored Procedure Requirements

Generate all required stored procedures.

Examples of likely procedures:

```text
usp_MobileUser_GetByUserId
usp_MobileUser_Create
usp_MobileUser_Update
usp_MobileUser_SetStatus

usp_Device_Get
usp_Device_Register
usp_Device_Revoke
usp_Device_Validate

usp_OfficeLocation_Create
usp_OfficeLocation_Update
usp_OfficeLocation_GetActive
usp_OfficeLocation_GetById
usp_OfficeLocation_SetStatus

usp_Attendance_GetCurrentStatus
usp_Attendance_ClockIn
usp_Attendance_ClockOut
usp_Attendance_GetByUser
usp_Attendance_GetReport

usp_ApplicationSetting_Get
usp_ApplicationSetting_Set

usp_MfaCredential_Get
usp_MfaCredential_Create
usp_MfaCredential_Revoke

usp_AuditLog_Create
usp_AuditLog_Get
```

These are examples, not a fixed list.

The final implementation must generate every stored procedure actually required by the application.

---

# 30. Transactions

Clock-In should execute as a transaction encompassing all operations that must succeed or fail together.

Conceptually:

```text
BEGIN TRANSACTION

Validate user
Validate device
Validate attendance state
Validate applicable business rules
Create attendance record
Create audit event

COMMIT
```

Clock-Out must similarly be transactional.

Do not create partial attendance records.

---

# 31. Time Handling

Use server-side authoritative time.

Prefer UTC for persistence:

```text
DateTimeOffset / UTC
```

The application should convert timestamps for presentation.

Do not use the mobile device's clock as the authoritative attendance timestamp.

The system must explicitly define the organization's attendance timezone.

For a Nigerian deployment, do not hard-code a timezone merely because the application is expected to operate in Nigeria. Make the configured business timezone explicit.

---

# 32. Audit Trail

Audit security-sensitive and administrative actions.

Examples:

- Login success
- Login failure
- Logout
- User creation
- User update
- User activation/deactivation
- Device registration
- Device revocation
- Office location creation/update
- Office location status change
- MFA enrollment/reset
- Clock-in
- Clock-out
- Location validation failure
- Device validation failure
- Administrative attendance correction

Audit records should include appropriate fields such as:

- Event ID
- Event type
- User/administrator
- Timestamp
- Correlation ID
- Result
- Reason
- Source/application
- Device identifier where appropriate

Never put passwords, OTP values, tokens, encryption keys, or other secrets in audit logs.

---

# 33. Logging

Use structured logging.

Every request should have a correlation identifier.

Log:

- Request start/end
- Duration
- Endpoint
- HTTP status
- Correlation ID
- Relevant business identifiers

Do not log:

- Passwords
- OTPs
- Access tokens
- Encryption keys
- Full authentication secrets
- Sensitive personal data unnecessarily
- Exact geolocation unless operationally justified

---

# 34. API Error Model

Use a consistent error response.

Example conceptual structure:

```json
{
  "success": false,
  "code": "LOCATION_NOT_ALLOWED",
  "message": "The current location is not within an approved office location.",
  "correlationId": "..."
}
```

Do not expose:

- Stack traces
- SQL errors
- Internal class names
- Connection strings
- Cryptographic details
- Infrastructure details

---

# 35. API Versioning

Version public mobile APIs.

Recommended:

```text
/api/v1/...
```

Avoid breaking existing mobile applications without a versioning strategy.

The server must be able to determine application version where appropriate.

---

# 36. Rate Limiting and Abuse Prevention

Apply rate limits particularly to:

- Login
- Clock-In
- Clock-Out
- MFA validation
- Device registration
- Location validation

Do not apply one arbitrary rate limit to all endpoints.

Design limits according to the endpoint's abuse characteristics.

---

# 37. Replay Protection

Attendance transactions must have replay protection.

Consider:

- Request ID
- Timestamp
- Nonce
- Server-side duplicate detection
- Short validity windows
- Idempotency keys

The exact mechanism must be selected based on the threat model.

Do not assume that TLS alone prevents replay of a previously captured valid request from a compromised client.

---

# 38. Mobile Secure Storage

Sensitive local values must use secure platform storage.

Do not store:

- Password
- TOTP secret
- API encryption key
- Access token
- Refresh token
- Other secrets

in ordinary preferences.

The user ID may be stored securely as explicitly required.

---

# 39. Network Security on Mobile

Use:

- HTTPS
- TLS certificate validation
- Secure HTTP client configuration
- Appropriate timeout
- Retry only where safe
- No credentials in URLs
- No sensitive information in query strings

Certificate pinning should not be added blindly.

If certificate pinning is required by the organization's threat model, design a certificate rotation/recovery process before implementing it.

---

# 40. API Authentication Design

Do not assume a particular authentication mechanism without evaluating the mobile threat model.

The solution must clearly distinguish:

1. Employee identity authentication.
2. Device authentication/registration.
3. Request integrity.
4. API authorization.
5. MFA/TOTP verification.

If bearer tokens are used, define:

- Token issuance
- Expiration
- Refresh
- Revocation
- Storage
- Rotation
- Scope
- Audience
- Issuer

If another mechanism is more appropriate, explain why.

---

# 41. Administrative Authentication

For the administrative application:

Prefer secure server-side authentication mechanisms appropriate to ASP.NET Core.

Use:

- Secure cookie configuration
- HttpOnly
- Secure
- SameSite
- Anti-forgery protection
- Session expiration
- Account lockout
- Password hashing
- Role/permission authorization

If Active Directory integration is considered, present it as an option unless explicitly required.

---

# 42. Authorization

Implement least privilege.

Examples:

```text
Super Administrator
Attendance Administrator
User Administrator
Location Administrator
Report Viewer
Auditor
```

Do not hard-code authorization checks throughout controllers.

Centralize authorization policies.

---

# 43. Security Headers

The administrative and API applications should use appropriate security headers, including where applicable:

- HSTS
- Content-Security-Policy
- X-Content-Type-Options
- Referrer-Policy
- Frame protections
- Permissions-Policy

Configure them according to the actual application behavior.

Do not blindly add headers that break required functionality.

---

# 44. Validation

Validate all external inputs.

Mobile:

- User ID
- Device ID
- Password
- TOTP
- Latitude
- Longitude
- Accuracy
- Application version

Admin:

- User profile
- Office coordinates
- Radius
- Settings
- Report filters

Validation must exist both:

- At the application boundary
- At the business/domain level where appropriate

---

# 45. Concurrency

Attendance is concurrency-sensitive.

Example:

Two clock-in requests arrive simultaneously.

Only one may successfully create the active attendance record.

Use:

- SQL transaction isolation appropriate to the operation
- Unique constraints/indexes
- Stored procedure checks
- Locking strategy where necessary

Do not assume application-level locking is sufficient in a multi-instance IIS deployment.

---

# 46. Deployment Architecture

Recommended on-premises architecture:

```text
                    ┌──────────────────────┐
                    │   Employee Mobile    │
                    │ Android / iOS        │
                    └──────────┬───────────┘
                               │ HTTPS
                               ▼
                    ┌──────────────────────┐
                    │ Reverse Proxy /      │
                    │ IIS / Load Balancer  │
                    └──────────┬───────────┘
                               │
                 ┌─────────────┴─────────────┐
                 ▼                           ▼
        ┌──────────────────┐        ┌──────────────────┐
        │ Attendance API   │        │ Admin Web App    │
        │ ASP.NET Core     │        │ ASP.NET Core MVC │
        └────────┬─────────┘        └────────┬─────────┘
                 │                           │
                 └─────────────┬─────────────┘
                               ▼
                    ┌──────────────────────┐
                    │ Microsoft SQL Server │
                    └──────────────────────┘
```

Do not assume a single server is sufficient for production.

Provide a deployment topology appropriate to the expected load and availability requirements.

---

# 47. Configuration

Configuration must not be hard-coded.

Use configuration for:

- Database connection string
- API URLs
- Attendance timezone
- Default location radius
- Time settings
- Security parameters
- Rate limits
- Token parameters
- Logging settings

Secrets must not be committed to source control.

For production, use an organization-controlled secret-management mechanism.

Do not introduce a cloud secret manager.

---

# 48. Database Security

Apply:

- Least-privilege database accounts
- Separate application identities where appropriate
- Encryption in transit
- Encryption at rest where required
- Backup encryption where supported
- Auditing
- Restricted SQL permissions
- No `db_owner` for application accounts

The application database account should execute only the required stored procedures and have the minimum necessary privileges.

---

# 49. SQL Injection Protection

Because the application uses stored procedures and Dapper:

- Never concatenate untrusted input into SQL.
- Use strongly typed parameters.
- Avoid dynamic SQL.
- If dynamic SQL becomes unavoidable, use strict validation and parameterization.

---

# 50. Testing

Provide:

## Backend

- Unit tests
- Application-layer tests
- Repository/integration tests
- API tests
- Security tests
- Concurrency tests
- Stored procedure tests where practical

## Flutter

- Unit tests
- Widget tests
- Integration tests
- Location validation flow tests
- Root/jailbreak behavior tests where platform test facilities permit
- Network failure tests
- Clock-in/clock-out state tests

## Security

Test:

- Brute-force protection
- Replay attacks
- Invalid device
- Revoked device
- Invalid location
- Location boundary
- Invalid OTP
- Duplicate clock-in
- Duplicate clock-out
- Unauthorized administrative access
- CSRF
- Session expiration
- Input validation
- Error disclosure

---

# 51. Location Boundary Testing

The 5-metre rule must be tested around the boundary.

Test points:

```text
0 m
1 m
3 m
4.9 m
5.0 m
5.1 m
10 m
```

Account for floating-point and GPS accuracy considerations.

Do not assume consumer GPS can reliably distinguish exactly 5.0 m in all environments.

The implementation must document this limitation.

---

# 52. Offline Behavior

The attendance application must not silently queue clock-in/clock-out transactions for later submission unless this behavior is explicitly approved.

Because attendance is location- and time-sensitive, offline submissions create significant integrity concerns.

If the API is unavailable:

- Inform the user.
- Do not falsely show the transaction as completed.
- Do not change the authoritative attendance state locally.

---

# 53. Idempotency

Clock-In and Clock-Out operations should be designed to handle retries safely.

For example:

If the client times out after the server successfully processes Clock-In, a retry must not create another attendance record.

Use an appropriate idempotency/request identifier.

---

# 54. API Documentation

Generate OpenAPI/Swagger documentation for the API.

Document:

- Endpoint
- HTTP method
- Authentication
- Request
- Response
- Error codes
- Validation rules
- Security requirements
- Example payloads

Never place real credentials or secrets in Swagger examples.

---

# 55. Database Deliverables

Generate:

1. Database creation script.
2. Schemas.
3. Tables.
4. Primary keys.
5. Foreign keys.
6. Unique constraints.
7. Check constraints.
8. Indexes.
9. Stored procedures.
10. Seed/reference data where required.
11. Security permissions.
12. Audit structures.
13. Deployment/upgrade scripts where applicable.

Do not provide partial database implementation.

---

# 56. API Deliverables

Generate:

- Project structure
- DTOs
- Controllers
- Application services/use cases
- Validation
- Authentication
- Authorization
- Encryption design
- Middleware
- Error handling
- Logging
- Correlation IDs
- Rate limiting
- API versioning
- Swagger
- Health checks
- Dapper repositories
- Stored procedure integration
- Configuration

---

# 57. Administrative Deliverables

Generate:

- MVC/Razor project
- Authentication
- Authorization
- User management
- Device management
- Office location management
- Attendance settings
- MFA management
- Reports
- Audit viewing
- Validation
- CSRF protection
- Secure session/cookie configuration

---

# 58. Flutter Deliverables

Generate:

- Project structure
- Feature-first Clean Architecture
- State management
- Secure storage
- Location service
- Device identity service
- API client
- Request/response handling
- Secure initialization flow
- Root/jailbreak detection
- Clock-in UI
- Clock-out UI
- Error handling
- Network handling
- App lifecycle handling
- Unit tests
- Widget tests
- Integration tests

---

# 59. Coding Standards

C#:

- Nullable reference types enabled.
- Async/await.
- CancellationToken propagation.
- Dependency injection.
- SOLID principles.
- No service locator.
- No static database access.
- No inline SQL.
- No EF Core.
- No hidden global state.
- No swallowed exceptions.

Flutter:

- Null safety.
- Immutable models where appropriate.
- Feature-first organization.
- Repository abstraction.
- Dependency injection.
- Testable services.
- No business logic inside widgets.

---

# 60. Exception Handling

Never expose raw exceptions to clients.

Use centralized exception handling.

Map exceptions to controlled API responses.

Examples:

```text
INVALID_REQUEST
UNAUTHORIZED
FORBIDDEN
INVALID_CREDENTIALS
INVALID_OTP
DEVICE_NOT_REGISTERED
DEVICE_REVOKED
LOCATION_NOT_ALLOWED
ALREADY_CLOCKED_IN
NOT_CLOCKED_IN
ATTENDANCE_WINDOW_CLOSED
ATTENDANCE_OPERATION_FAILED
INTERNAL_ERROR
```

Use codes consistently.

---

# 61. Observability

Provide:

- Structured logs
- Health endpoints
- Readiness checks
- Liveness checks
- Request duration
- Correlation IDs
- Security events

Do not introduce cloud monitoring.

Where monitoring is required, prefer on-premises solutions.

---

# 62. Data Retention

Do not invent retention periods.

The implementation must define configurable retention policies after the business/legal requirements are confirmed.

Potentially sensitive data includes:

- Attendance history
- Location data
- Device identifiers
- Audit records
- Security events

Retention and deletion must comply with applicable organizational policy and law.

For a Nigerian deployment, explicitly validate applicable Nigerian data-protection requirements before production release rather than assuming compliance.

---

# 63. Privacy

Location information and employee attendance data are sensitive operational data.

Implement:

- Data minimization
- Purpose limitation
- Access control
- Auditability
- Retention policy
- Secure transmission
- Secure storage

Do not collect information merely because it is technically available.

---

# 64. Anti-Hallucination / Fact-Checking Rules

This section is mandatory.

Before selecting a technology, API, Flutter package, Microsoft feature, security mechanism, or protocol:

1. Verify that it exists.
2. Verify that it supports the required platform.
3. Verify that it is compatible with the selected framework/version.
4. Prefer official documentation.
5. Do not fabricate package names.
6. Do not fabricate framework APIs.
7. Do not claim a platform capability that the platform does not provide.
8. Clearly identify assumptions.
9. Clearly identify requirements that remain unresolved.
10. If a requirement conflicts with platform/security realities, explain the conflict instead of pretending it is solved.

For security-sensitive decisions, prefer:

- OWASP guidance
- RFCs
- Microsoft official documentation
- Android official documentation
- Apple official documentation
- Flutter/Dart official documentation
- SQL Server official documentation

Do not invent security standards.

---

# 65. Important Security Reality Checks

The implementation must explicitly acknowledge:

### GPS is not a perfect security boundary

A device may report manipulated location information.

Design additional controls where the threat model requires them.

### Device IDs are not necessarily permanent hardware IDs

Android and iOS impose restrictions on hardware identifiers.

Use supported identifiers and device registration mechanisms.

### Root/jailbreak detection is bypassable

Treat it as defense-in-depth.

### Mobile applications are untrusted clients

Never trust the mobile application to enforce authorization.

### Encryption does not make secrets safe by itself

Keys must be securely managed.

### HTTPS is mandatory

Application-level encryption should only be added when the threat model justifies its complexity.

### Client timestamps are not authoritative

Use server time.

### A 5-metre radius is technically demanding

GPS accuracy can be worse than 5 metres, especially indoors or around buildings.

The system must communicate this operational limitation.

---

# 66. Implementation Sequence

Implement in this order:

1. Requirements clarification and assumptions register.
2. Threat model.
3. Solution architecture.
4. Database architecture.
5. SQL schema.
6. Stored procedures.
7. Domain model.
8. Application layer.
9. Infrastructure/Dapper.
10. API security.
11. API endpoints.
12. Administrative application.
13. Flutter core infrastructure.
14. Flutter location validation.
15. Flutter device security.
16. Flutter user status.
17. Flutter clock-in.
18. Flutter clock-out.
19. Reporting.
20. Audit.
21. Automated testing.
22. Deployment.
23. Security hardening.
24. Documentation.

---

# 67. Required Output Style

When generating the implementation:

- Explain architecture decisions.
- Show complete project structure.
- Generate complete files.
- Do not omit critical files.
- Do not use placeholders.
- Do not use pseudo-code where production code is expected.
- Do not use `TODO` for required functionality.
- Generate complete stored procedures.
- Include configuration examples without secrets.
- Include database deployment scripts.
- Include test projects.
- Explain how projects reference each other.
- Explain how to build.
- Explain how to run locally.
- Explain how to deploy to IIS.
- Explain how to deploy SQL Server objects.
- Explain mobile Android deployment.
- Explain iOS deployment.
- Explain security configuration.

If the response becomes too large, continue in logically ordered parts without replacing implementation with placeholders.

---

# 68. Requirements That Must Be Clarified Before Production

Do not silently decide these items:

1. Exact identity source for employee passwords.
2. Whether TOTP enrollment is performed by administrators or another process.
3. Whether one employee can use multiple devices.
4. Whether one device can be assigned to multiple employees.
5. Exact attendance timezone.
6. Exact clock-in closing behavior.
7. Exact clock-out opening behavior.
8. Whether late clock-in is permitted.
9. Whether early clock-out is permitted.
10. Whether attendance corrections are permitted.
11. Who can correct attendance.
12. Whether corrections require approval.
13. Required reports.
14. Data retention period.
15. Expected number of users.
16. Expected transaction volume.
17. Availability requirements.
18. Disaster recovery requirements.
19. Backup requirements.
20. Whether API access is internal-only or internet-facing.
21. Whether the organization has its own PKI/certificates.
22. Whether Active Directory integration is required.
23. Whether application-level payload encryption beyond HTTPS is mandatory.
24. Whether certificate pinning is required.
25. Whether location accuracy below/above the configured threshold should be accepted.
26. Whether GPS spoofing detection is required.
27. Whether an employee can clock in from multiple approved office locations.
28. Whether attendance can span midnight.
29. Whether weekends/public holidays are relevant.
30. Whether shift-based attendance is required.

Where answers are unavailable, explicitly mark them as:

```text
OPEN REQUIREMENT
```

Do not fabricate a business decision.

---

# 69. Definition of Done

The solution is considered complete only when:

- Mobile app runs on Android.
- Mobile app runs on iOS.
- Mobile app validates location before normal operation.
- Rooted/jailbroken devices are blocked as far as platform-supported controls allow.
- User ID is securely stored.
- User status is obtained from the server.
- Clock-in works.
- Clock-out works.
- MFA/TOTP works.
- Device validation works.
- Location validation works.
- 5-metre proximity calculation is correctly implemented.
- Attendance rules are configurable.
- Administrative authentication works.
- User administration works.
- Device management works.
- Office location management works.
- Reporting works.
- Audit logging works.
- API security is implemented.
- TLS is enforced.
- Database uses SQL Server.
- Database access uses Dapper.
- No EF/EF Core exists.
- No inline SQL exists in application code.
- All required stored procedures exist and are complete.
- Transactions protect attendance integrity.
- Duplicate operations are controlled.
- Concurrency is handled.
- Tests exist.
- Swagger/OpenAPI is available.
- IIS deployment is documented.
- SQL deployment is documented.
- Security configuration is documented.
- No cloud dependency exists.
- Known platform limitations are documented.
- No fabricated technology or unsupported capability is presented as fact.

---

# 70. Final Instruction

Act as an experienced enterprise solution architect.

Do not simply generate code from the requirements.

First reason about:

- Security
- Identity
- Device trust
- Location trust
- Attendance integrity
- Concurrency
- Data protection
- Mobile platform limitations
- API security
- Database integrity
- Deployment
- Operations
- Audit
- Maintainability

Where a requested approach is technically weak, explain the weakness and provide the safer production-grade alternative.

Where a requirement is ambiguous, do not silently invent the business rule.

Where a technology capability must be verified, fact-check it before relying on it.

The final implementation must be **production-oriented, secure, maintainable, testable, fully on-premises, and based on verifiable technology capabilities**.
