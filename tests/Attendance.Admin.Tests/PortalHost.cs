using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Repositories;
using Attendance.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Attendance.Admin.Tests;

/// <summary>
/// The portal, hosted in process against the development database.
/// </summary>
/// <remarks>
/// The connection string, its timeouts and the client timeout come from
/// <see cref="TestEnvironment"/>, which explains why they are what they are.
/// The hosted portal is given the same connection settings as the tests
/// themselves: its sign-in path, its reports and its health probes all query
/// the database, and on a starved machine those calls time out and surface as
/// 500s that look nothing like the cause.
/// </remarks>
internal static class PortalHost
{
    /// <summary>
    /// Authenticator time steps the hosted portal accepts either side of the
    /// current one, in place of the seeded value of 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not 1.</b> A test computes a real authenticator code and
    /// then signs in with it. Verifying the password is PBKDF2 at 220,000
    /// iterations (TD-05) — about 0.2 seconds idle, but 49 seconds observed on a
    /// machine starved of CPU. By then the code was older than the 30-second
    /// tolerance, the sign-in was refused, and 27 of the 95 tests in this
    /// project failed on an unauthenticated redirect. Six steps gives the code
    /// three minutes, which is longer than any sign-in has taken.
    /// </para>
    /// <para>
    /// <b>What it does not weaken.</b> No test here asserts that a code outside
    /// the window is refused — <c>TotpVerifier</c>'s own tests cover the
    /// tolerance boundary, at instants they control, and still run at 1. What
    /// these tests assert is that a code is required at all, that a spent code
    /// cannot be used twice, and that enrolment activates on a working code.
    /// Widening the window leaves all three exactly as they were: replay
    /// protection consumes the matched time step in the database, which no
    /// tolerance can affect.
    /// </para>
    /// </remarks>
    public const int StepTolerance = 6;

    /// <summary>The portal, hosted against the development database.</summary>
    /// <param name="configure">Further settings for this test class.</param>
    public static WebApplicationFactory<Program> Create(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseEnvironment("Development")
                .UseSetting("SqlServer:ConnectionString", TestEnvironment.ConnectionString)

                // The validator's ceiling; the default is 15 seconds.
                .UseSetting(
                    "SqlServer:CommandTimeoutSeconds",
                    TestEnvironment.DatabaseTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Widen only the authenticator window, and only in this process. The
            // alternative — writing Security.TotpStepTolerance to the database —
            // would loosen a security setting for everything sharing that
            // database, and would leave it loosened if a run were killed.
            builder.ConfigureTestServices(services => services
                .AddSingleton<IAdministratorPolicyProvider>(provider =>
                    new WiderAuthenticatorWindow(
                        provider.GetRequiredService<AdministratorPolicyProvider>(), StepTolerance)));

            configure?.Invoke(builder);
        });

    /// <summary>
    /// A client for the hosted portal that waits as long as a starved machine
    /// needs. Every caller passes options, because the portal's tests depend on
    /// controlling redirects, cookies and the HTTPS base address.
    /// </summary>
    public static HttpClient Client(
        this WebApplicationFactory<Program> factory, WebApplicationFactoryClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);

        HttpClient client = factory.CreateClient(options);
        client.Timeout = TestEnvironment.RequestTimeout;
        return client;
    }

    /// <summary>
    /// The real policy, read from the real settings, with one field replaced.
    /// Lockout threshold, lockout duration and whether MFA is required at all
    /// still come from the database, so the tests that depend on them are
    /// unaffected.
    /// </summary>
    private sealed class WiderAuthenticatorWindow(
        IAdministratorPolicyProvider inner, int stepTolerance) : IAdministratorPolicyProvider
    {
        public async Task<AdministratorPolicy> GetAsync(CancellationToken cancellationToken) =>
            (await inner.GetAsync(cancellationToken).ConfigureAwait(false))
                with { TotpStepTolerance = stepTolerance };
    }
}
