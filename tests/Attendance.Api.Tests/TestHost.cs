using Attendance.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Attendance.Api.Tests;

/// <summary>
/// The API, hosted in process against the development database.
/// </summary>
/// <remarks>
/// <para>
/// The connection string and the waiting limits come from
/// <see cref="TestEnvironment"/>, which explains why they are what they are.
/// The hosted API is given the same connection settings as the tests
/// themselves, because its own database calls were failing for the same reasons
/// and surfacing as 500s.
/// </para>
/// <para>
/// Time windows are this project's own sensitivity: a test expecting exactly N
/// requests to be allowed in a one-minute window fails if a slow run crosses a
/// window boundary and is granted a second allowance. <see cref="Create"/>
/// lengthens the rate-limit and failure-budget windows to an hour, which a test
/// run cannot straddle. The limits themselves — which are what the tests check
/// — are untouched, and deployments keep the one-minute default.
/// </para>
/// </remarks>
internal static class TestHost
{
    /// <inheritdoc cref="TestEnvironment.ConnectionString"/>
    public static readonly string ConnectionString = TestEnvironment.ConnectionString;

    /// <inheritdoc cref="TestEnvironment.RequestTimeout"/>
    public static readonly TimeSpan RequestTimeout = TestEnvironment.RequestTimeout;

    /// <summary>
    /// The API, hosted against the development database, with windows long
    /// enough not to roll over during a test.
    /// </summary>
    /// <param name="configure">Further settings for this test class.</param>
    public static WebApplicationFactory<Program> Create(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseEnvironment("Development")
                .UseSetting("SqlServer:ConnectionString", ConnectionString)

                // The validator's ceiling; the default is 15 seconds.
                .UseSetting(
                    "SqlServer:CommandTimeoutSeconds",
                    TestEnvironment.DatabaseTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting("Api:RateLimits:WindowSeconds", "3600")
                .UseSetting("Api:Abuse:WindowSeconds", "3600");

            configure?.Invoke(builder);
        });

    /// <summary>A client for the hosted API that waits as long as a starved machine needs.</summary>
    public static HttpClient Client(this WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        HttpClient client = factory.CreateClient();
        client.Timeout = RequestTimeout;
        return client;
    }
}
