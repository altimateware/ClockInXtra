using System.Data;
using System.Net;
using Attendance.Admin.Setup;
using Attendance.Application.Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Break-glass recovery (<c>--reset-administrator</c>): after it, the old
/// credentials and sessions are dead and the new ones work in the real portal.
/// </summary>
/// <remarks>
/// The procedure is called directly, as the command does, over this test's
/// trusted connection — which, like the operator the command expects, is a
/// database owner. That the portal's own login is refused is proven separately
/// in <c>LeastPrivilegeTests</c>.
/// </remarks>
public sealed partial class AuthenticatedPortalTests
{
    private const string RecoveredPassword = "recovered via break glass";

    [Fact]
    public async Task RecoveryReplacesBothFactorsAndEndsExistingSessions()
    {
        string userName = $"itest.recover.{Guid.NewGuid():N}"[..40];

        await CreateAdministratorAsync(userName, "Super Administrator");

        try
        {
            // An existing session, as a stolen cookie would be.
            using HttpClient before = NewClient();
            await SignInAsync(before, userName, Password, CurrentCode());
            Assert.Equal(HttpStatusCode.OK,
                (await before.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

            byte[] newSecret = KeyGeneration.GenerateRandomKey(20);
            Assert.Equal(0, await RecoverAsync(userName, RecoveredPassword, newSecret, reactivate: false));

            // 1. The old session is over.
            HttpResponseMessage stale = await before.GetAsync("/Dashboard", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
            Assert.Contains("/Account/Login", stale.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

            // 2. The old password, with the old authenticator, no longer signs in.
            using HttpClient oldCredentials = NewClient();
            await SignInAsync(oldCredentials, userName, Password, CurrentCode());
            await AssertNotSignedInAsync(oldCredentials);

            // 3. The new password with the NEW authenticator does — and the key
            //    ring the portal decrypts with can read what recovery wrote.
            using HttpClient after = NewClient();
            HttpResponseMessage signedIn = await SignInAsync(
                after, userName, RecoveredPassword, new Totp(newSecret).ComputeTotp(DateTime.UtcNow));
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

            // 4. ...straight to changing the password nobody but the operator knows.
            HttpResponseMessage dashboard = await after.GetAsync("/Dashboard", TestContext.Current.CancellationToken);
            Assert.Contains("/Account/ChangePassword", dashboard.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

            // 5. It is on the record, with the login that did it.
            string? details = await QueryAsync<string?>("""
                SELECT TOP (1) l.Details
                FROM audit.AuditLog AS l
                INNER JOIN core.Administrator AS a ON l.SubjectId = CAST(a.AdministratorPublicId AS NVARCHAR(64))
                WHERE a.UserName = @userName AND l.EventType = 'Administrator.AccessRecovered'
                ORDER BY l.AuditLogId DESC
                """, new { userName });

            Assert.NotNull(details);
            Assert.Contains("performedByLogin", details, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync("""
                DELETE FROM core.AdministratorRole
                WHERE AdministratorId IN (SELECT AdministratorId FROM core.Administrator WHERE UserName = @userName);
                DELETE FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND SubjectKey = @userName;
                DELETE FROM core.Administrator WHERE UserName = @userName;
                """, new { userName });
        }
    }

    [Fact]
    public async Task RecoveryWillNotQuietlyReactivateADeactivatedAccount()
    {
        string userName = $"itest.recover.{Guid.NewGuid():N}"[..40];

        await CreateAdministratorAsync(userName, null);

        try
        {
            await ExecuteAsync("UPDATE core.Administrator SET Status = 0 WHERE UserName = @userName;", new { userName });

            byte[] secret = KeyGeneration.GenerateRandomKey(20);

            // A deactivation may have been deliberate: without --reactivate, refused.
            Assert.Equal(1014, await RecoverAsync(userName, RecoveredPassword, secret, reactivate: false));
            Assert.Equal(0, await QueryAsync<int>("SELECT Status FROM core.Administrator WHERE UserName = @userName", new { userName }));

            // With it, restored.
            Assert.Equal(0, await RecoverAsync(userName, RecoveredPassword, secret, reactivate: true));
            Assert.Equal(1, await QueryAsync<int>("SELECT Status FROM core.Administrator WHERE UserName = @userName", new { userName }));

            // And an unknown name changes nothing.
            Assert.Equal(1070, await RecoverAsync($"itest.nobody.{Guid.NewGuid():N}"[..40], RecoveredPassword, secret, reactivate: true));
        }
        finally
        {
            await ExecuteAsync("""
                DELETE FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND SubjectKey = @userName;
                DELETE FROM core.Administrator WHERE UserName = @userName;
                """, new { userName });
        }
    }

    [Fact]
    public void RecoveryConnectsAsTheOperatorNeverAsThePortal()
    {
        // The portal's login is denied the recovery schema. A command that quietly
        // borrowed it would fail — or, worse, be the bypass the schema exists to stop.
        SqlConnectionStringBuilder builder = AdministratorRecovery.AsOperator(
            "Server=sql01;Database=ClockInXtra;User ID=app_admin;Password=not-a-real-one;Encrypt=True");

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(string.Empty, builder.UserID);
        Assert.Equal(string.Empty, builder.Password);
        Assert.Equal("sql01", builder.DataSource);
        Assert.Equal("ClockInXtra", builder.InitialCatalog);
    }

    [Theory]
    [InlineData(new[] { "--reset-administrator", "devadmin" }, "devadmin")]
    [InlineData(new[] { "--reset-administrator", "devadmin", "--reactivate" }, "devadmin")]
    [InlineData(new[] { "--reactivate", "--reset-administrator", " devadmin " }, "devadmin")]
    [InlineData(new[] { "--reset-administrator" }, null)]
    [InlineData(new[] { "--reset-administrator", "--reactivate" }, null)]
    public void RecoveryReadsTheUserNameThatFollowsTheArgument(string[] args, string? expected) =>
        Assert.Equal(expected, AdministratorRecovery.UserNameFrom(args));

    private async Task<int> RecoverAsync(string userName, string password, byte[] secret, bool reactivate)
    {
        IPasswordHasher hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        ISecretProtector protector = _factory.Services.GetRequiredService<ISecretProtector>();

        PasswordHash hash = hasher.Hash(password);

        DynamicParameters parameters = new();
        parameters.Add("@UserName", userName, DbType.String, size: 64);
        parameters.Add("@HashFormat", hash.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", hash.Iterations, DbType.Int32);
        parameters.Add("@Salt", hash.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", hash.Hash, DbType.Binary, size: 64);
        parameters.Add("@MfaSecretProtected", protector.Protect(SecretPurposes.AdministratorTotpSecret, secret), DbType.Binary, size: -1);
        parameters.Add("@Reactivate", reactivate, DbType.Boolean);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "recovery.usp_Administrator_RecoverAccess", parameters,
            commandType: CommandType.StoredProcedure, cancellationToken: TestContext.Current.CancellationToken));

        return parameters.Get<int>("@ResultCode");
    }
}
