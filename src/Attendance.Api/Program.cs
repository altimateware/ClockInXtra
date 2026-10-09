using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Attendance.Api.Configuration;
using Attendance.Api.Diagnostics;
using Attendance.Api.Middleware;
using Attendance.Api.Security;
using Attendance.Infrastructure.Deployment;
using Attendance.Infrastructure.DependencyInjection;
using Attendance.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging. Structured, with the correlation identifier on every entry (§33).
// Sinks are configured in appsettings so a deployment can direct logs at the
// organisation's own collector — never a cloud service (§2.1, §61).
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName));

builder.Services.AddAttendanceInfrastructure(builder.Configuration, builder.Environment);

// ---------------------------------------------------------------------------
// Forwarded headers, restricted to the configured proxies.
//
// This matters more here than in most applications: the signature base includes
// the authority and path the CLIENT saw. If any caller could set
// X-Forwarded-Host, a signature could be made to verify against a value the
// client never signed. Never wildcard (§6.2).
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    ApiOptions api = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

    foreach (string proxy in api.KnownProxies)
    {
        if (IPAddress.TryParse(proxy, out IPAddress? address))
        {
            options.KnownProxies.Add(address);
        }
    }
});

// ---------------------------------------------------------------------------
// Rate limiting, per endpoint class (§36). First-line throttling only: the
// authoritative brute-force control is the database lockout counter, because
// this limiter counts per process (decision TD-10).
// ---------------------------------------------------------------------------
// The window is one minute, which is what the "...PerMinute" limits mean. It is
// configurable for one reason: a test that expects exactly N requests allowed
// must not have the window roll over halfway through on a slow machine, so the
// tests lengthen it. Deployments should leave it at 60.
TimeSpan rateLimitWindow = TimeSpan.FromSeconds(
    Math.Max(1, builder.Configuration.GetValue("Api:RateLimits:WindowSeconds", 60)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    AddFixedWindow(options, RateLimitPolicies.Attendance,
        builder.Configuration.GetValue("Api:RateLimits:AttendancePerMinute", 10), rateLimitWindow);
    AddFixedWindow(options, RateLimitPolicies.Registration,
        builder.Configuration.GetValue("Api:RateLimits:RegistrationPerMinute", 5), rateLimitWindow);
    AddFixedWindow(options, RateLimitPolicies.Read,
        builder.Configuration.GetValue("Api:RateLimits:ReadPerMinute", 60), rateLimitWindow);

    static void AddFixedWindow(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string policy, int permitPerWindow, TimeSpan window) =>
        options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
            // The verified device when the signature has been checked; otherwise
            // (unsigned endpoints: challenge, startup location check, config) the
            // client address, which behind the proxies is the forwarded one.
            context.Items[AuthenticatedDevice.ContextKey] is AuthenticatedDevice device
                ? "device:" + device.DevicePublicId.ToString()
                : "address:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitPerWindow,
                Window = window,
                QueueLimit = 0,
            }));
});

// ---------------------------------------------------------------------------
// Abuse guard for requests that never verify. The limiter above runs after
// signature verification, on purpose, so a caller presenting a bad signature is
// refused before meeting it — and each refusal writes to an append-only ledger
// that is never purged. This bounds what one address can make the ledger carry
// (Phase 24 review; see ISignatureFailureBudget).
// ---------------------------------------------------------------------------
// One minute, configurable for the same reason as the rate-limit window above.
builder.Services.AddSingleton<ISignatureFailureBudget>(_ => new SignatureFailureBudget(
    builder.Configuration.GetValue("Api:Abuse:SignatureFailuresPerAddressPerMinute", 60),
    TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("Api:Abuse:WindowSeconds", 60)))));

// Versioned in the URL segment (/api/v1/...), so a released mobile client keeps
// working when a v2 appears beside it (§35). Every endpoint declares its version
// explicitly; there is no unversioned surface to assume a default for.
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddMvc();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<KeyRingHealthCheck>("key-ring", tags: ["ready"])
    .AddCheck<AttendanceConfigurationHealthCheck>("attendance-configuration", tags: ["ready"]);

// Request bodies are small and fixed in shape. A cap here is one of the
// cheapest abuse controls there is (§22).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = 64 * 1024);

WebApplication app = builder.Build();

// Create the database and its objects if they are not there yet. Safe to run
// on every start and when the portal is starting at the same moment: see
// DatabaseDeployer for how the two hosts are serialised.
await app.DeployDatabaseAsync().ConfigureAwait(false);

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();

if (!app.Environment.IsDevelopment())
{
    // HSTS is asserted only outside development, where a developer certificate
    // would otherwise pin a browser to https on localhost.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

// After routing, so endpoint metadata decides which endpoints are exempt.
app.UseMiddleware<SignatureVerificationMiddleware>();

// After routing, because [EnableRateLimiting] policies are read from the
// matched endpoint: registered before routing, as it once was, every policy was
// silently inert (found in Phase 23; RateLimitTests). And after signature
// verification, so a signed request is counted against its verified device
// rather than its network address — employees in one office share a NAT address,
// and a per-address limit would refuse the morning rush.
app.UseRateLimiter();

app.MapControllers();

// Health probes carry the signature exemption as endpoint metadata. Note that
// AllowAnonymous is NOT sufficient: it speaks to ASP.NET Core authorization,
// which this API does not use — authentication here is the request signature, and
// its middleware reads its own marker. A probe without this marker is refused as
// unsigned, and every node reads as unhealthy to the load balancer.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
}).WithMetadata(new AllowUnsignedRequestAttribute());

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthReportWriter.WriteAsync,
}).WithMetadata(new AllowUnsignedRequestAttribute());

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithMetadata(new AllowUnsignedRequestAttribute());
}

await app.RunAsync().ConfigureAwait(false);

// The generated entry point lives in the global namespace, so this partial must
// too — it cannot be moved into one without ceasing to be the same class.
#pragma warning disable CA1050 // Declare types in namespaces

/// <summary>Exposed so the API test project can host the application.</summary>
public partial class Program;

#pragma warning restore CA1050
