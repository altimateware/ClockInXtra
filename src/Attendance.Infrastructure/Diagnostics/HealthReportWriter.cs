using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Attendance.Infrastructure.Diagnostics;

/// <summary>
/// Writes a health report as JSON: overall status and each check's description.
/// </summary>
/// <remarks>
/// The default writer returns a single word, which tells an operator that the
/// API is Degraded but not why. Exception details are never written: the health
/// endpoints are internal, but an exception message can still carry a path or a
/// server name that has no business in a response.
/// </remarks>
public static class HealthReportWriter
{
    /// <summary>Writes the report.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json";

        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted);
    }
}
