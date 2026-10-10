using System.Data;
using System.Security.Cryptography;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;
using OtpNet;

namespace Attendance.Admin.Setup;

/// <summary>
/// The one-time command that creates the first administrator.
/// </summary>
/// <remarks>
/// <para>
/// Run as <c>Attendance.Admin --create-first-administrator</c>. Without it a
/// fresh deployment cannot be signed into: creating an administrator normally
/// requires naming the administrator who created them, and at the start there is
/// nobody to name.
/// </para>
/// <para>
/// <b>The password is typed at the console, never passed as an argument.</b> A
/// command-line argument lands in shell history, in the process list where any
/// local user can read it, and in whatever deployment script invoked it. Console
/// input with echo suppressed avoids all three.
/// </para>
/// <para>
/// The account is created with <c>MustChangePassword</c> set, because a password
/// typed during a deployment has been seen by whoever performed the deployment.
/// </para>
/// </remarks>
public static class FirstAdministratorSetup
{
    /// <summary>The argument that selects this command.</summary>
    public const string Argument = "--create-first-administrator";

    /// <summary>
    /// The shortest password accepted for this account. Stated at the prompt as
    /// well as enforced, so the rule is known before it is typed twice.
    /// </summary>
    private const int MinimumPasswordLength = 12;

    /// <summary>Runs the setup command.</summary>
    /// <returns>A process exit code: 0 on success.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        Console.WriteLine("ClockInXtra — create the first administrator");
        Console.WriteLine();
        Console.WriteLine("This works only while no administrator exists. Once one does,");
        Console.WriteLine("every further account is created from inside the portal, which");
        Console.WriteLine("records who created it.");
        Console.WriteLine();

        // Numbered, and each rule stated where it is asked rather than only
        // enforced afterwards: being told the password was too short after
        // typing it twice is a poor way to learn the rule.
        string userName = ConsoleInput.Prompt(
            "Step 1 of 5 — user name", "what you will sign in with, up to 64 characters");

        string displayName = ConsoleInput.Prompt(
            "Step 2 of 5 — display name", "shown in the portal, up to 160 characters");

        string email = ConsoleInput.Prompt(
            "Step 3 of 5 — email", "optional; press Enter to leave it unset");

        string password = ConsoleInput.PromptSecret(
            "Step 4 of 5 — password", $"at least {MinimumPasswordLength} characters");

        string confirmation = ConsoleInput.PromptSecret("Step 5 of 5 — confirm the password");

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The passwords do not match. Nothing was created.");
            return 2;
        }

        if (password.Length < MinimumPasswordLength)
        {
            // A deliberate floor rather than a policy engine: this account holds
            // every permission in the system from the moment it exists.
            Console.Error.WriteLine(
                $"Use at least {MinimumPasswordLength} characters for the first administrator. "
                + "Nothing was created.");
            return 2;
        }

        IPasswordHasher hasher = services.GetRequiredService<IPasswordHasher>();
        ISqlConnectionFactory connections = services.GetRequiredService<ISqlConnectionFactory>();
        ISecretProtector protector = services.GetRequiredService<ISecretProtector>();

        PasswordHash hash = hasher.Hash(password);

        // 160 bits, the RFC 4226 §4 recommendation and what authenticator
        // applications expect. Generated before the call so that the account and
        // its authenticator are created in one statement — see the procedure's
        // header for why they must not be two.
        byte[] secret = KeyGeneration.GenerateRandomKey(20);

        DynamicParameters parameters = new();
        parameters.Add("@UserName", userName, DbType.String, size: 64);
        parameters.Add("@DisplayName", displayName, DbType.String, size: 160);
        parameters.Add("@Email", string.IsNullOrWhiteSpace(email) ? null : email, DbType.String, size: 256);
        parameters.Add("@HashFormat", hash.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", hash.Iterations, DbType.Int32);
        parameters.Add("@Salt", hash.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", hash.Hash, DbType.Binary, size: 64);
        parameters.Add(
            "@MfaSecretProtected",
            protector.Protect(SecretPurposes.AdministratorTotpSecret, secret),
            DbType.Binary,
            size: -1);
        parameters.Add("@CorrelationId", Guid.NewGuid(), DbType.Guid);
        parameters.Add("@AdministratorId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@AdministratorPublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease = await connections.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                "admin.usp_Administrator_Bootstrap", parameters, connections.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int resultCode = parameters.Get<int?>("@ResultCode") ?? -1;

        switch (resultCode)
        {
            case 0:
                Console.WriteLine();
                Console.WriteLine($"Created '{userName}' as a Super Administrator.");
                Console.WriteLine("The password must be changed at first sign-in.");

                ConsoleInput.ShowAuthenticator(userName, secret);
                Console.WriteLine("It activates on the first code that works at sign-in. Until then the");
                Console.WriteLine("account is enrolled but unproven, so re-running setup on an empty");
                Console.WriteLine("system is still an option if the scan did not take.");

                CryptographicOperations.ZeroMemory(secret);
                return 0;

            case 1001:
                Console.Error.WriteLine("An administrator already exists. Setup has already been done.");
                return 3;

            case 1070:
                Console.Error.WriteLine("The 'Super Administrator' role is missing. Apply the seed scripts first.");
                return 4;

            default:
                Console.Error.WriteLine($"Setup failed with result code {resultCode}.");
                return 5;
        }
    }
}
