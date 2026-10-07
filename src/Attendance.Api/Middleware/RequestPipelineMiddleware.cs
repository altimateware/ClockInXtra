using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Attendance.Domain.Enums;

namespace Attendance.Api.Middleware;

/// <summary>
/// Assigns a correlation identifier to every request.
/// </summary>
/// <remarks>
/// The identifier is echoed in the response and included in every log entry,
/// audit row and error body. It is the only thing that lets support join "the app
/// said something went wrong at 08:12" to the server's record of why, without the
/// error body itself carrying any internal detail (§33, §34).
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Assigns or adopts the correlation identifier.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A client-supplied value is adopted only when it is a well-formed GUID.
        // Echoing arbitrary client text into logs is how log injection starts.
        Guid correlationId =
            Guid.TryParse(context.Request.Headers[CorrelationContext.HeaderName], out Guid supplied)
                ? supplied
                : Guid.NewGuid();

        context.Items[CorrelationContext.ContextKey] = correlationId;

        // Applied as the response starts, not now: an error path that calls
        // Response.Clear() removes headers already set, and an error response is
        // exactly where the caller most needs an identifier to quote.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.HeaderName] = correlationId.ToString();
            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Adds the response headers appropriate to a JSON API (§43).
/// </summary>
/// <remarks>
/// Chosen for what this application actually serves. A JSON API renders nothing,
/// so the content security policy denies everything rather than listing sources,
/// and framing is refused outright. Headers that exist to constrain a browser
/// rendering HTML are omitted rather than copied in — §43 warns against adding
/// headers blindly.
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Adds the headers.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Applied as the response starts, so they survive the Response.Clear()
        // the error paths perform (found in Phase 23: every 500 and 413 was sent
        // without them, including Cache-Control: no-store).
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
            headers["Cache-Control"] = "no-store";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}

/// <summary>
/// Converts any unhandled exception into the standard error body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing about the failure reaches the client.</b> The exception is logged
/// against the correlation identifier; the caller receives INTERNAL_ERROR and
/// that identifier. Stack traces, SQL text and constraint names stay on the
/// server, where §34 requires them to stay.
/// </para>
/// <para>
/// This sits outermost so that a failure inside any later middleware — including
/// signature verification — still produces a well-formed response rather than a
/// bare connection reset.
/// </para>
/// </remarks>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>Runs the pipeline and contains any failure.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away. Not a fault, and not worth an error log entry
            // on a mobile network where this is routine.
            _logger.RequestAborted();
        }
        catch (Exception exception)
        {
            Guid correlationId =
                context.Items[CorrelationContext.ContextKey] is Guid existing ? existing : Guid.NewGuid();

            _logger.UnhandledException(correlationId, exception);

            if (context.Response.HasStarted)
            {
                // The response is already on the wire; there is nothing safe to
                // add. Letting it fail is better than emitting a second body.
                throw;
            }

            (int status, string code, string message) =
                ApiErrorCatalogue.Map(AttendanceResultCode.InternalError);

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";

            await context.Response
                .WriteAsJsonAsync(ApiError.Create(code, message, correlationId), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>Source-generated log messages for the pipeline.</summary>
internal static partial class PipelineLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Error,
        Message = "Unhandled exception processing request {CorrelationId}.")]
    public static partial void UnhandledException(this ILogger logger, Guid correlationId, Exception exception);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Debug,
        Message = "Request aborted by the client.")]
    public static partial void RequestAborted(this ILogger logger);
}
