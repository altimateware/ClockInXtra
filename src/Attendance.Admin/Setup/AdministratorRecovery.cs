using System.Data;
using System.Security.Cryptography;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OtpNet;

namespace Attendance.Admin.Setup;

/// <summary>
/// Break-glass recovery: a new password and a new authenticator for a named
/// administrator, when nobody can sign in to issue them the normal way.
/// </summary>
/// <remarks>
/// <para>
/// Run on a server, as <c>Attendance.Admin --reset-administrator &lt;user name&gt;</c>.
/// Every other way to reset an administrator needs another administrator, so
/// without this, losing the last Super Administrator's password or phone locks
/// everyone out for good.
/// </para>
/// <para>
/// <b>Authority comes from the database, not from this program.</b> The command
/// connects as the Windows account running it — never the portal's configured
/// login — and the procedure it calls sits in the <c>[recovery]</c> schema, which
/// is denied to every application account. So it works only for someone who is
/// already a database administrator on that server: a person who could change the
/// tables directly anyway. It gives them nothing new; it gives the organisation a
/// proper hash, rotated sessions, and an audit entry naming the login that did it.
/// </para>
/// <para>
/// The new password is typed at the console, never passed as an argument, for
/// the same reasons as first-administrator setup. The account must change it at
/// the next sign-in, and the new authenticator replaces the old one at once.
/// </para>
/// </remarks>
public static class AdministratorRecovery
{
    /// <summary>The argument that selects this command.</summary>
    public const string Argument = "--reset-administrator";

    /// <summary>Allows a deactivated or locked account to be reactivated too.</summary>
    public const string ReactivateArgument = "--reactivate";

    private const int PermissionDenied = 229;

    /// <summary>Runs the recovery command.</summary>
    /// <returns>A process exit code: 0 on success.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        string? userName = UserNameFrom(args);
        bool reactivate = args.Contains(ReactivateArgument, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Error.WriteLine($"Usage: Attendance.Admin {Argument} <user name> [{ReactivateArgument}]");
            return 2;
        }

        SqlServerOptions sqlOptions = services.GetRequiredService<IOptions<SqlServerOptions>>().Value;
        SqlConnectionStringBuilder connection = AsOperator(sqlOptions.ConnectionString);

        Console.WriteLine("ClockInXtra — break-glass administrator recovery");
        Console.WriteLine();
        Console.WriteLine($"  Administrator : {userName}");
        Console.WriteLine($"  Server        : {connection.DataSource}");
        Console.WriteLine($"  Database      : {connection.InitialCatalog}");
        Console.WriteLine($"  Connecting as : {Environment.UserDomainName}\\{Environment.UserName} (Windows authentication)");
        Console.WriteLine();
        Console.WriteLine("This replaces the account's password AND its authenticator, ends every");
        Console.WriteLine("session it has open, and is recorded permanently in the audit trail");
        Console.WriteLine("under your login. It works only if you are a database administrator.");
        Console.WriteLine();

        // Typing the name again is the confirmation: it cannot be answered by
        // reflex the way "y" can, and it catches the wrong name on the command line.
        if (!string.Equals(ConsoleInput.Prompt("Type the user name again to confirm"), userName, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The names do not match. Nothing was changed.");
            return 2;
        }

        string password = ConsoleInput.PromptSecret(
            "New temporary password", "the account must change it at next sign-in");

        string confirmation = ConsoleInput.PromptSecret("Confirm the temporary password");

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The passwords do not match. Nothing was changed.");
            return 2;
        }

        if (password.Length < 12)
        {
            // The same floor as first-administrator setup: this is typically the
            // account that holds every permission in the system.
            Console.Error.WriteLine("Use at least 12 characters. Nothing was changed.");
            return 2;
        }

        IPasswordHasher hasher = services.GetRequiredService<IPasswordHasher>();
        ISecretProtector protector = services.GetRequiredService<ISecretProtector>();

        PasswordHash hash = hasher.Hash(password);
        byte[] secret = KeyGeneration.GenerateRandomKey(20);

        try
        {
            int resultCode = await RecoverAsync(
                connection.ConnectionString,
                sqlOptions.CommandTimeoutSeconds,
                userName,
                hash,
                protector.Protect(SecretPurposes.AdministratorTotpSecret, secret),
                reactivate,
                cancellationToken).ConfigureAwait(false);

            switch (resultCode)
            {
                case 0:
                    Console.WriteLine();
                    Console.WriteLine($"'{userName}' has a new password and a new authenticator.");
                    Console.WriteLine("Every session it had open has ended, and the password must be");
                    Console.WriteLine("changed at the next sign-in.");

                    ConsoleInput.ShowAuthenticator(userName, secret);
                    Console.WriteLine("The old authenticator no longer works. The new one activates on the");
                    Console.WriteLine("first code that works at sign-in.");
                    return 0;

                case 1014:
                    Console.Error.WriteLine(
                        $"'{userName}' is deactivated or locked. If restoring it is intended, run again with {ReactivateArgument}.");
                    return 3;

                case 1070:
                    Console.Error.WriteLine($"There is no administrator named '{userName}'. Nothing was changed.");
                    return 4;

                default:
                    Console.Error.WriteLine($"Recovery failed with result code {resultCode}. Nothing was changed.");
                    return 5;
            }
        }
        catch (SqlException error) when (error.Number == PermissionDenied)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Refused by SQL Server: this Windows account may not run account recovery.");
            Console.Error.WriteLine("Run it as a database administrator (db_owner on this database), from a");
            Console.Error.WriteLine("session signed in as that account. Nothing was changed.");
            return 6;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>The user name that follows the argument, if there is one.</summary>
    public static string? UserNameFrom(string[] args)
    {
        int index = Array.IndexOf(args, Argument);

        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1].Trim()
            : null;
    }

    /// <summary>
    /// The configured connection, but authenticating as whoever is running this.
    /// </summary>
    /// <remarks>
    /// The portal's own login is denied the recovery schema by design, so using
    /// it would always fail — and a command that quietly borrowed the portal's
    /// credentials would be exactly the bypass the schema exists to prevent.
    /// </remarks>
    public static SqlConnectionStringBuilder AsOperator(string configured)
    {
        SqlConnectionStringBuilder builder = new(configured);

        builder.Remove("User ID");
        builder.Remove("Password");
        builder.Remove("Authentication");
        builder.IntegratedSecurity = true;
        builder.ApplicationName = "ClockInXtra recovery";

        return builder;
    }

    private static async Task<int> RecoverAsync(
        string connectionString,
        int commandTimeoutSeconds,
        string userName,
        PasswordHash hash,
        byte[] protectedSecret,
        bool reactivate,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@UserName", userName, DbType.String, size: 64);
        parameters.Add("@HashFormat", hash.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", hash.Iterations, DbType.Int32);
        parameters.Add("@Salt", hash.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", hash.Hash, DbType.Binary, size: 64);
        parameters.Add("@MfaSecretProtected", protectedSecret, DbType.Binary, size: -1);
        parameters.Add("@Reactivate", reactivate, DbType.Boolean);
        parameters.Add("@CorrelationId", Guid.NewGuid(), DbType.Guid);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "recovery.usp_Administrator_RecoverAccess",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: commandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return parameters.Get<int?>("@ResultCode") ?? -1;
    }
}
