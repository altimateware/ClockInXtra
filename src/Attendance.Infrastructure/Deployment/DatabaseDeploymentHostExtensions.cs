using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Attendance.Infrastructure.Deployment;

/// <summary>
/// Brings the database up to date before a host serves anything.
/// </summary>
/// <remarks>
/// Called by both the API and the portal, from the same place in each: after
/// the container is built, before any middleware or command runs. Before,
/// because a host whose database is missing has nothing useful to do, and
/// because the portal's own setup commands — the first administrator, and
/// break-glass recovery — need the schema as much as a request does.
/// </remarks>
public static class DatabaseDeploymentHostExtensions
{
    /// <summary>
    /// Creates the database and its objects if they are not already there.
    /// Throws if deployment fails, so a host does not start half-working.
    /// </summary>
    public static async Task DeployDatabaseAsync(
        this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<DatabaseDeployer>()
            .DeployAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
