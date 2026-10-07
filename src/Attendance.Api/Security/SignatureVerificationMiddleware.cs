using System.Buffers;
using System.Text.Json;
using Attendance.Api.Contracts;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;

namespace Attendance.Api.Security;

/// <summary>
/// Verifies the RFC 9421 signature on every mobile request.
/// </summary>
/// <remarks>
/// <para>
/// This is the API's authentication. There is no bearer token: the device signs
/// each request with a key held in its secure element, which gives proof of
/// origin, integrity after TLS terminates at the reverse proxy, and — with the
/// nonce — protection against replay of a captured request. TLS alone provides
/// none of the three (DEC-02, §22).
/// </para>
/// <para>
/// <b>Order of checks, and why.</b> Cheap and local first, database last:
/// </para>
/// <list type="number">
///   <item>Parse the headers. Malformed is refused before anything else.</item>
///   <item>Algorithm and tag against a fixed allow-list — never taken from the
///   client beyond that.</item>
///   <item>Covered components exactly as the profile requires. RFC 9421 §7.2.1
///   warns that a signature covering too little is worse than none, because it
///   looks like protection.</item>
///   <item>Freshness, against the configured skew.</item>
///   <item>Body digest recomputed from the received bytes.</item>
///   <item>Device resolved, and its status checked.</item>
///   <item>Nonce claimed — <b>before</b> the signature is verified, so two
///   identical requests arriving together cannot both proceed.</item>
///   <item>Signature verified.</item>
/// </list>
/// <para>
/// The published order in §6.2 lists the nonce before the device lookup. In
/// practice the nonce table is keyed by the integer device id, which only the
/// lookup provides, so the lookup comes first. Nothing is weakened: the lookup is
/// a read, and the nonce is still claimed before the signature is trusted.
/// </para>
/// </remarks>
public sealed class SignatureVerificationMiddleware
{
    private const string SignatureInputHeader = "Signature-Input";
    private const string SignatureHeader = "Signature";
    private const string ContentDigestHeader = "Content-Digest";

    /// <summary>Largest body this middleware will buffer in order to digest it.</summary>
    private const int MaxBufferedBodyBytes = 64 * 1024;

    /// <summary>Where this request's address is kept for the refusal path.</summary>
    private const string FailureBudgetAddressKey = "signature.failure.address";

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public SignatureVerificationMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Verifies the signature, or refuses the request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IDeviceRepository devices,
        INonceStore nonces,
        IRequestSignatureVerifier signatures,
        IAttendancePolicyProvider policyProvider,
        ISecurityEventRecorder securityEvents,
        ISignatureFailureBudget failureBudget,
        ILogger<SignatureVerificationMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failureBudget);

        Endpoint? endpoint = context.GetEndpoint();

        bool allowsUnsigned = endpoint?.Metadata.GetMetadata<AllowUnsignedRequestAttribute>() is not null;
        bool hasSignature = context.Request.Headers.ContainsKey(SignatureInputHeader);

        // "Optional", not "ignored". An endpoint reachable before registration
        // still verifies a signature when one is offered — otherwise a signed
        // request to it would skip the body-digest check, and a registered device
        // could never be recognised on the one endpoint it uses before enrolling.
        if (allowsUnsigned && !hasSignature)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // An address that has already failed its way through the budget is
        // refused here: before the device lookup, and before anything is written
        // to the audit ledger. Verification runs ahead of the rate limiter by
        // design, so without this a caller who never presents a valid signature
        // meets no limit at all (Phase 24 review; see ISignatureFailureBudget).
        string address = ClientAddress(context);

        if (failureBudget.IsExhausted(address))
        {
            await RefuseWithoutRecordingAsync(context, AttendanceResultCode.RateLimited).ConfigureAwait(false);
            return;
        }

        context.Items[FailureBudgetAddressKey] = address;

        CancellationToken cancellationToken = context.RequestAborted;

        if (!SignatureInput.TryParse(context.Request.Headers[SignatureInputHeader], out SignatureInput? input))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "SIGNATURE_INPUT_MALFORMED", securityEvents, null).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(input.Algorithm, SignatureInput.ExpectedAlgorithm, StringComparison.Ordinal)
            || !string.Equals(input.Tag, SignatureInput.ExpectedTag, StringComparison.Ordinal))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "SIGNATURE_PROFILE_MISMATCH", securityEvents, null).ConfigureAwait(false);
            return;
        }

        if (!TryParseSignature(context.Request.Headers[SignatureHeader], input.Label, out byte[] signature))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "SIGNATURE_MALFORMED", securityEvents, null).ConfigureAwait(false);
            return;
        }

        byte[] body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        bool hasBody = body.Length > 0;

        if (!input.HasExpectedComponents(hasBody))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "SIGNATURE_COVERAGE_INSUFFICIENT", securityEvents, null).ConfigureAwait(false);
            return;
        }

        AttendancePolicy policy = await policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset created = DateTimeOffset.FromUnixTimeSeconds(input.Created);

        if ((DateTimeOffset.UtcNow - created).Duration() > TimeSpan.FromSeconds(policy.SignatureSkewSeconds))
        {
            await RefuseAsync(context, AttendanceResultCode.ClockSkew, "SIGNATURE_CLOCK_SKEW", securityEvents, null).ConfigureAwait(false);
            return;
        }

        string? contentDigest = context.Request.Headers[ContentDigestHeader];

        if (input.CoversContentDigest && !ContentDigest.Matches(contentDigest, body))
        {
            await RefuseAsync(context, AttendanceResultCode.InvalidRequest, "CONTENT_DIGEST_MISMATCH", securityEvents, null).ConfigureAwait(false);
            return;
        }

        byte[] signatureBase = SignatureBase.Build(
            input,
            context.Request.Method,
            context.Request.Host.Value ?? string.Empty,
            context.Request.Path.Value ?? "/",
            contentDigest);

        // Registration: the key is in the body and no device exists yet.
        if (string.Equals(input.KeyId, SignatureInput.UnregisteredKeyId, StringComparison.Ordinal))
        {
            if (endpoint?.Metadata.GetMetadata<AllowUnregisteredDeviceAttribute>() is null)
            {
                await RefuseAsync(context, AttendanceResultCode.Unauthorized, "UNREGISTERED_KEY_NOT_PERMITTED", securityEvents, null).ConfigureAwait(false);
                return;
            }

            context.Items[UnregisteredSignature.ContextKey] = new UnregisteredSignature(signatureBase, signature);

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!Guid.TryParse(input.KeyId, out Guid devicePublicId))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "KEY_ID_MALFORMED", securityEvents, null).ConfigureAwait(false);
            return;
        }

        DeviceVerificationRecord? record =
            await devices.GetForSignatureVerificationAsync(devicePublicId, cancellationToken).ConfigureAwait(false);

        if (record is not { } device)
        {
            await RefuseAsync(context, AttendanceResultCode.DeviceNotRegistered, "DEVICE_NOT_REGISTERED", securityEvents, devicePublicId).ConfigureAwait(false);
            return;
        }

        // The result code is the authority, not the presence of the row. A device
        // that cannot act is refused here — except on an endpoint that exists to
        // tell it so, where the signature is still verified in full below.
        bool reportsOwnStatus =
            endpoint?.Metadata.GetMetadata<AllowInactiveDeviceAttribute>() is not null
            && device.ResultCode is AttendanceResultCode.DeviceNotApproved
                or AttendanceResultCode.DeviceRevoked
                or AttendanceResultCode.UserInactive;

        if (device.ResultCode != AttendanceResultCode.Success && !reportsOwnStatus)
        {
            await RefuseAsync(context, device.ResultCode, device.ResultCode.ToString(), securityEvents, devicePublicId).ConfigureAwait(false);
            return;
        }

        if (!TryDecodeNonce(input.Nonce, out byte[] nonce))
        {
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "NONCE_MALFORMED", securityEvents, devicePublicId).ConfigureAwait(false);
            return;
        }

        AttendanceResultCode nonceResult =
            await nonces.TryClaimAsync(device.DeviceId, nonce, created, cancellationToken).ConfigureAwait(false);

        if (nonceResult != AttendanceResultCode.Success)
        {
            await RefuseAsync(context, AttendanceResultCode.ReplayedRequest, "SIGNATURE_NONCE_REPLAYED", securityEvents, devicePublicId).ConfigureAwait(false);
            return;
        }

        if (!signatures.Verify(signatureBase, signature, device.PublicKey))
        {
            // A well-formed request from a registered device whose signature does
            // not verify is not a mistake anyone makes by accident.
            await RefuseAsync(context, AttendanceResultCode.Unauthorized, "SIGNATURE_INVALID", securityEvents, devicePublicId, SecurityEventSeverity.Critical).ConfigureAwait(false);
            return;
        }

        context.Items[AuthenticatedDevice.ContextKey] = new AuthenticatedDevice(
            device.DeviceId, device.DevicePublicId, device.MobileUserId, device.UserId, device.Platform, device.AppVersion);

        logger.SignatureVerified(device.DevicePublicId);

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers the body so it can be digested and still read by the endpoint.
    /// </summary>
    /// <remarks>
    /// Bounded deliberately. An unbounded read here would let anyone who can
    /// reach the endpoint exhaust server memory before a single check has run
    /// (§22, input size limits).
    /// </remarks>
    private static async Task<byte[]> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0 or null && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return [];
        }

        context.Request.EnableBuffering(bufferThreshold: MaxBufferedBodyBytes, bufferLimit: MaxBufferedBodyBytes);

        using MemoryStream buffer = new();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        context.Request.Body.Position = 0;

        return buffer.ToArray();
    }

    /// <summary>Parses <c>Signature: label=:base64:</c>.</summary>
    private static bool TryParseSignature(string? headerValue, string label, out byte[] signature)
    {
        signature = [];

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        string prefix = $"{label}=:";

        if (!headerValue.StartsWith(prefix, StringComparison.Ordinal)
            || !headerValue.EndsWith(':'))
        {
            return false;
        }

        try
        {
            signature = Convert.FromBase64String(headerValue[prefix.Length..^1]);
        }
        catch (FormatException)
        {
            return false;
        }

        // ecdsa-p256-sha256 is raw r‖s: two 32-byte integers (RFC 9421 §3.3.4).
        return signature.Length == 64;
    }

    /// <summary>Decodes a base64url nonce.</summary>
    private static bool TryDecodeNonce(string nonce, out byte[] decoded)
    {
        decoded = [];

        try
        {
            string padded = nonce.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

            decoded = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        // The profile specifies 128 bits; the column holds up to 32 bytes.
        return decoded.Length is >= 16 and <= 32;
    }

    private static async Task RefuseAsync(
        HttpContext context,
        AttendanceResultCode resultCode,
        string reasonCode,
        ISecurityEventRecorder securityEvents,
        Guid? devicePublicId,
        SecurityEventSeverity severity = SecurityEventSeverity.Warning)
    {
        Guid correlationId = context.Items[CorrelationContext.ContextKey] is Guid existing ? existing : Guid.NewGuid();

        // Spend from this address's budget. The failure that exhausts it is
        // recorded as such, so the trail shows where individual recording
        // stopped rather than simply going quiet.
        bool exhausted = false;

        if (context.Items[FailureBudgetAddressKey] is string address
            && context.RequestServices.GetService<ISignatureFailureBudget>() is { } budget)
        {
            exhausted = budget.RecordFailure(address);
        }

        await securityEvents.RecordAsync(
            new SecurityEvent(
                "Signature.Rejected",
                severity,
                devicePublicId is null ? SecurityEventSubject.Unknown : SecurityEventSubject.Device,
                devicePublicId?.ToString(),
                reasonCode,
                SourceApplication: "Attendance.Api",
                DevicePublicId: devicePublicId,
                SourceAddressHash: null,
                CorrelationId: correlationId),
            context.RequestAborted).ConfigureAwait(false);

        if (exhausted)
        {
            await securityEvents.RecordAsync(
                new SecurityEvent(
                    "Signature.FailureBudgetExhausted",
                    SecurityEventSeverity.Warning,
                    SecurityEventSubject.Unknown,
                    SubjectKey: null,
                    "SIGNATURE_FAILURE_BUDGET_EXHAUSTED",
                    SourceApplication: "Attendance.Api",
                    DevicePublicId: null,
                    SourceAddressHash: null,
                    CorrelationId: correlationId),
                context.RequestAborted).ConfigureAwait(false);
        }

        (int status, string code, string message) = ApiErrorCatalogue.Map(resultCode);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(
            ApiError.Create(code, message, correlationId), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses without touching the database: the point of the budget is that a
    /// caller past it costs nothing but a response.
    /// </summary>
    private static async Task RefuseWithoutRecordingAsync(HttpContext context, AttendanceResultCode resultCode)
    {
        Guid correlationId = context.Items[CorrelationContext.ContextKey] is Guid existing ? existing : Guid.NewGuid();

        (int status, string code, string message) = ApiErrorCatalogue.Map(resultCode);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(
            ApiError.Create(code, message, correlationId), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The caller's address as the rate limiter sees it: forwarded headers have
    /// already been applied by the time this middleware runs.
    /// </summary>
    private static string ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Source-generated log messages for the signature middleware.</summary>
internal static partial class SignatureVerificationLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Debug,
        Message = "Request signature verified for device {DevicePublicId}.")]
    public static partial void SignatureVerified(this ILogger logger, Guid devicePublicId);
}
