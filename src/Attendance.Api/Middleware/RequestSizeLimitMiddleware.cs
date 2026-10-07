using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Attendance.Api.Middleware;

/// <summary>
/// Caps every request body at <see cref="MaxBodyBytes"/>, before anything reads it
/// (§22 input size limits, §36).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed although signed requests are already capped.</b> The
/// signature middleware refuses to buffer more than 64 KB, but the endpoints
/// that accept unsigned requests — the registration challenge, the startup
/// location check, the configuration read — never pass through that buffer.
/// Their bodies were read by model binding under the server's default limit of
/// about 30 MB, which on an internet-facing endpoint turns a free request into
/// 30 MB of parsing.
/// </para>
/// <para>
/// A declared <c>Content-Length</c> over the limit is refused here without
/// reading a byte. A chunked body declares nothing, so the server's own
/// per-request limit is lowered as well, and the server stops reading once the
/// limit is passed; that surfaces as a 413 too, never as an internal error.
/// </para>
/// <para>
/// The largest legitimate body is a device registration carrying a key
/// attestation certificate chain — a few kilobytes, base64-encoded. 64 KB leaves
/// ample room and matches the signature middleware's buffer.
/// </para>
/// </remarks>
public sealed class RequestSizeLimitMiddleware
{
    /// <summary>Largest request body the API accepts.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    private const string Code = "INVALID_REQUEST";
    private const string Message = "The request is too large.";

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public RequestSizeLimitMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Applies the limit.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            await RefuseAsync(context).ConfigureAwait(false);
            return;
        }

        IHttpMaxRequestBodySizeFeature? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await RefuseAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        Guid correlationId = context.Items.TryGetValue(CorrelationContext.ContextKey, out object? value) && value is Guid id
            ? id
            : Guid.NewGuid();

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json";

        await context.Response
            .WriteAsJsonAsync(ApiError.Create(Code, Message, correlationId), CancellationToken.None)
            .ConfigureAwait(false);
    }
}
