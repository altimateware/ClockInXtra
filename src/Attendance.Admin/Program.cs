using Attendance.Admin.Security;
using Attendance.Infrastructure.Deployment;
using Attendance.Infrastructure.DependencyInjection;
using Attendance.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddAttendanceAdministration(builder.Configuration, builder.Environment);

// The portal cannot enrol or verify an authenticator without the key ring, and
// can do nothing without the database: either one failing makes the node unready.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<KeyRingHealthCheck>("key-ring", tags: ["ready"]);
builder.Services.AddScoped<SecurityStampCookieValidator>();

// ---------------------------------------------------------------------------
// Cookie authentication (§41).
//
// The portal is internal-only (ASM-01), which lowers the exposure but does not
// change what the cookie has to be: HttpOnly so script cannot read it, Secure so
// it never travels in clear, SameSite=Strict because nothing legitimately links
// into this portal from another site, and a sliding expiry short enough that an
// unattended session closes itself.
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ClockInXtra.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.IsEssential = true;

        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Denied";

        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;

        // Revalidated on every request, so disabling an account or changing a
        // role takes effect immediately rather than at the end of a session.
        options.Events.OnValidatePrincipal = context =>
            context.HttpContext.RequestServices
                .GetRequiredService<SecurityStampCookieValidator>()
                .ValidateAsync(context);
    });

builder.Services.AddAuthorization(PermissionPolicies.AddAll);

// Anti-forgery on every unsafe verb by default (§41). Opting a form out has to
// be a deliberate act, not an omission.
//
// An issued password — from setup, account creation or a reset — must be
// changed before anything else. Enforced globally so a new controller cannot
// forget to apply it.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add(new MustChangePasswordFilter());
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "ClockInXtra.Admin.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

WebApplication app = builder.Build();

// Create the database and its objects if they are not there yet. Before the
// setup commands below, because creating the first administrator needs the
// schema just as much as serving a page does.
await app.DeployDatabaseAsync().ConfigureAwait(false);

// One-time setup: create the first administrator and exit without serving.
// Checked after the container is built so the command uses the same password
// hasher and connection the portal itself will.
if (args.Contains(Attendance.Admin.Setup.FirstAdministratorSetup.Argument, StringComparer.Ordinal))
{
    await using AsyncServiceScope setupScope = app.Services.CreateAsyncScope();

    return await Attendance.Admin.Setup.FirstAdministratorSetup
        .RunAsync(setupScope.ServiceProvider, CancellationToken.None)
        .ConfigureAwait(false);
}

// Break-glass recovery of a named administrator, then exit without serving. It
// uses the portal's hasher and key ring (so the new authenticator secret is
// readable at sign-in) but connects as the operator, never as the portal.
if (args.Contains(Attendance.Admin.Setup.AdministratorRecovery.Argument, StringComparer.Ordinal))
{
    await using AsyncServiceScope recoveryScope = app.Services.CreateAsyncScope();

    return await Attendance.Admin.Setup.AdministratorRecovery
        .RunAsync(recoveryScope.ServiceProvider, args, CancellationToken.None)
        .ConfigureAwait(false);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Security headers appropriate to a server-rendered portal. Unlike the JSON API,
// this one actually renders HTML, so the policy permits its own styles and
// scripts and nothing else.
app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;

    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'self'";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

    await next().ConfigureAwait(false);
});

// One fixed culture for every request, whatever the server's regional settings
// or the browser's language. Model binding parses numbers with the request
// culture, and browsers post number inputs with a decimal POINT: on a server
// set to a decimal-comma culture, an office at "6.465422" would otherwise bind
// wrongly or not at all. Nigerian English also gives day-first dates.
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("en-NG"),
    SupportedCultures = [new System.Globalization.CultureInfo("en-NG")],
    SupportedUICultures = [new System.Globalization.CultureInfo("en-NG")],
    RequestCultureProviders = [],
});

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// Health probes for the load balancer. Anonymous, because a probe has no session;
// they reveal only the status of the database connection and the key ring, never
// an exception. Like the API's, they are for the internal network only.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthReportWriter.WriteAsync,
}).AllowAnonymous();

await app.RunAsync().ConfigureAwait(false);

return 0;

#pragma warning disable CA1050 // The generated entry point lives in the global namespace.

/// <summary>Exposed so the portal's test project can host the application.</summary>
public partial class Program;

#pragma warning restore CA1050
