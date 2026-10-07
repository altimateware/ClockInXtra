using System.Data;
using System.Text.RegularExpressions;
using Attendance.Tests;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Database.Tests;

/// <summary>
/// What each application account can actually do once the real security script
/// has been applied (Claude.md §48, decisions DB-01/DB-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> In development every application connects as the
/// database owner, so a procedure that needs a permission its production
/// account lacks works perfectly until it is deployed. Phase 20 found exactly
/// that: the audit trail and the validation-failure report read a ledger view
/// that ownership chaining does not cover, and both would have failed for the
/// portal account on the first day in production.
/// </para>
/// <para>
/// <b>How.</b> Each test opens a transaction, creates login-less users, applies
/// <c>database/security/10_security_users_grants.sql</c> itself to them — the
/// script accepts database users that already exist — and then impersonates
/// each account. Testing the script rather than a copy of its grants means a
/// change to the script is what gets tested. The transaction is always rolled
/// back: no user, grant or row survives.
/// </para>
/// </remarks>
public sealed partial class LeastPrivilegeTests
{
    /// <summary>SQL Server's "permission denied" error.</summary>
    private const int PermissionDenied = 229;

    private readonly string _connectionString = TestEnvironment.ConnectionString;

    // ---- The portal account ------------------------------------------------

    [Theory]
    [InlineData("admin.usp_AuditLog_Search")]
    [InlineData("admin.usp_Report_GetValidationFailures")]
    [InlineData("admin.usp_Attendance_GetDailyReport")]
    [InlineData("admin.usp_Report_GetFilterOptions")]
    public async Task ThePortalAccountCanRunEachReport(string procedure)
    {
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        DynamicParameters parameters = ReportParameters(procedure);

        await scope.AsAsync(scope.AdminUser, async connection =>
        {
            // Every result set is read: a permission failure in a later SELECT
            // would otherwise go unnoticed.
            await using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(new CommandDefinition(
                procedure, parameters, scope.Transaction, commandType: CommandType.StoredProcedure));

            while (!grid.IsConsumed)
            {
                _ = await grid.ReadAsync();
            }
        });

        Assert.Equal(0, parameters.Get<int>("@ResultCode"));
    }

    [Theory]
    [InlineData("SELECT TOP (1) 1 FROM audit.AuditLog")]
    [InlineData("SELECT TOP (1) 1 FROM audit.SecurityEvent")]
    [InlineData("SELECT TOP (1) 1 FROM core.Administrator")]
    [InlineData("SELECT TOP (1) 1 FROM core.MfaCredential")]
    public async Task ThePortalAccountCannotReadTablesDirectly(string sql)
    {
        // VIEW LEDGER CONTENT is granted to the portal for the ledger views; the
        // DENY on the audit schema must still hold.
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        SqlException refused = await Assert.ThrowsAsync<SqlException>(() =>
            scope.AsAsync(scope.AdminUser, connection =>
                connection.ExecuteAsync(new CommandDefinition(sql, transaction: scope.Transaction))));

        Assert.Equal(PermissionDenied, refused.Number);
    }

    [Fact]
    public async Task ThePortalAccountCannotRunMobileOrMaintenanceProcedures()
    {
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        await AssertRefusedAsync(scope, scope.AdminUser, "DECLARE @r INT; EXEC mobile.usp_OfficeLocation_GetActive @ResultCode = @r OUTPUT");
        await AssertRefusedAsync(scope, scope.AdminUser,
            "DECLARE @d NVARCHAR(MAX), @r INT; EXEC job.usp_Maintenance_GenerateLedgerDigest @Digest = @d OUTPUT, @ResultCode = @r OUTPUT");
    }

    // ---- The internet-facing API account -----------------------------------

    [Fact]
    public async Task TheMobileAccountCannotReachAdministrationOrTables()
    {
        // TH-28: a compromised internet-facing API process must not be able to
        // call an administrative procedure or read a table.
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        await AssertRefusedAsync(scope, scope.MobileUser,
            "DECLARE @r INT; EXEC admin.usp_AuditLog_Search @FromUtc = '2000-01-01', @ToUtc = '2100-01-01', @ResultCode = @r OUTPUT");
        await AssertRefusedAsync(scope, scope.MobileUser, "SELECT TOP (1) 1 FROM core.MobileUser");
        await AssertRefusedAsync(scope, scope.MobileUser, "SELECT TOP (1) 1 FROM audit.AuditLog");
        await AssertRefusedAsync(scope, scope.MobileUser,
            "DECLARE @d NVARCHAR(MAX), @r INT; EXEC job.usp_Maintenance_GenerateLedgerDigest @Digest = @d OUTPUT, @ResultCode = @r OUTPUT");
    }

    [Fact]
    public async Task TheMobileAccountCanRunItsOwnProcedures()
    {
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        DynamicParameters parameters = new();
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await scope.AsAsync(scope.MobileUser, connection =>
            connection.QueryAsync(new CommandDefinition(
                "mobile.usp_OfficeLocation_GetActive", parameters, scope.Transaction,
                commandType: CommandType.StoredProcedure)));

        Assert.Equal(0, parameters.Get<int>("@ResultCode"));
    }

    // ---- The maintenance account -------------------------------------------

    [Fact]
    public async Task TheMaintenanceAccountCanExportALedgerDigestButNotReadTheAudit()
    {
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        DynamicParameters parameters = new();
        parameters.Add("@Digest", dbType: DbType.String, size: -1, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await scope.AsAsync(scope.JobUser, connection =>
            connection.ExecuteAsync(new CommandDefinition(
                "job.usp_Maintenance_GenerateLedgerDigest", parameters, scope.Transaction,
                commandType: CommandType.StoredProcedure)));

        Assert.Equal(0, parameters.Get<int>("@ResultCode"));
        Assert.Contains("\"block_id\"", parameters.Get<string?>("@Digest") ?? string.Empty, StringComparison.Ordinal);

        await AssertRefusedAsync(scope, scope.JobUser, "SELECT TOP (1) 1 FROM audit.AuditLog");
        await AssertRefusedAsync(scope, scope.JobUser,
            "DECLARE @r INT; EXEC admin.usp_AuditLog_Search @FromUtc = '2000-01-01', @ToUtc = '2100-01-01', @ResultCode = @r OUTPUT");
    }

    // ---- Break-glass recovery ----------------------------------------------

    [Fact]
    public async Task NoApplicationAccountCanRunAccountRecovery()
    {
        // recovery.usp_Administrator_RecoverAccess resets any administrator's
        // password and authenticator without an acting administrator. If the
        // portal's account could run it, a compromised web server could take over
        // a Super Administrator in one call. Only a database administrator may.
        await using Scope scope = await Scope.BeginAsync(_connectionString);

        const string recover = """
            DECLARE @r INT;
            EXEC recovery.usp_Administrator_RecoverAccess
                 @UserName = N'nobody', @HashFormat = 'pbkdf2-sha512', @Iterations = 220000,
                 @Salt = 0x00, @PasswordHash = 0x00, @MfaSecretProtected = 0x00,
                 @ResultCode = @r OUTPUT;
            """;

        await AssertRefusedAsync(scope, scope.AdminUser, recover);
        await AssertRefusedAsync(scope, scope.MobileUser, recover);
        await AssertRefusedAsync(scope, scope.JobUser, recover);
    }

    // ---- Helpers -----------------------------------------------------------

    private static DynamicParameters ReportParameters(string procedure)
    {
        DynamicParameters parameters = new();

        if (procedure == "admin.usp_Report_GetFilterOptions")
        {
            // Event types included: that result set reads the audit ledger.
            parameters.Add("@IncludeEventTypes", true, DbType.Boolean);
        }
        else if (procedure == "admin.usp_Attendance_GetDailyReport")
        {
            parameters.Add("@FromDate", DateTime.UtcNow.Date.AddDays(-7), DbType.Date);
            parameters.Add("@ToDate", DateTime.UtcNow.Date, DbType.Date);
        }
        else
        {
            parameters.Add("@FromUtc", DateTime.UtcNow.AddDays(-7), DbType.DateTime2);
            parameters.Add("@ToUtc", DateTime.UtcNow, DbType.DateTime2);
            parameters.Add("@PageSize", 5, DbType.Int32);
        }

        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);
        return parameters;
    }

    private static async Task AssertRefusedAsync(Scope scope, string user, string sql)
    {
        SqlException refused = await Assert.ThrowsAsync<SqlException>(() =>
            scope.AsAsync(user, connection =>
                connection.ExecuteAsync(new CommandDefinition(sql, transaction: scope.Transaction))));

        Assert.Equal(PermissionDenied, refused.Number);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    [GeneratedRegex(@"^\s*:setvar\b.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SetVarLine();

    /// <summary>
    /// One transaction holding three login-less users with the real grants.
    /// </summary>
    private sealed class Scope : IAsyncDisposable
    {
        private readonly SqlConnection _connection;

        private Scope(SqlConnection connection, SqlTransaction transaction, string suffix)
        {
            _connection = connection;
            Transaction = transaction;
            MobileUser = $"zz_lp_mobile_{suffix}";
            AdminUser = $"zz_lp_admin_{suffix}";
            JobUser = $"zz_lp_jobs_{suffix}";
        }

        public SqlTransaction Transaction { get; }

        public string MobileUser { get; }

        public string AdminUser { get; }

        public string JobUser { get; }

        public static async Task<Scope> BeginAsync(string connectionString)
        {
            SqlConnection connection = new(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                TestContext.Current.CancellationToken);

            Scope scope = new(connection, transaction, Guid.NewGuid().ToString("N")[..8]);

            await scope.ExecuteAsync(
                $"CREATE USER [{scope.MobileUser}] WITHOUT LOGIN; " +
                $"CREATE USER [{scope.AdminUser}] WITHOUT LOGIN; " +
                $"CREATE USER [{scope.JobUser}] WITHOUT LOGIN;");

            await scope.ApplySecurityScriptAsync();

            // The script sets XACT_ABORT ON. Turned off again so that the
            // permission refusals these tests provoke end the statement, not the
            // whole transaction the remaining assertions run in.
            await scope.ExecuteAsync("SET XACT_ABORT OFF;");

            return scope;
        }

        public async Task AsAsync(string user, Func<SqlConnection, Task> action)
        {
            await ExecuteAsync($"EXECUTE AS USER = N'{user}';");

            try
            {
                await action(_connection);
            }
            finally
            {
                await ExecuteAsync("REVERT;");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Transaction.RollbackAsync();
            await Transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private Task<int> ExecuteAsync(string sql) =>
            _connection.ExecuteAsync(new CommandDefinition(
                sql, transaction: Transaction, cancellationToken: TestContext.Current.CancellationToken));

        private async Task ApplySecurityScriptAsync()
        {
            string script = await File.ReadAllTextAsync(
                FindSecurityScript(), TestContext.Current.CancellationToken);

            script = SetVarLine().Replace(script, string.Empty)
                .Replace("$(MobileUser)", MobileUser, StringComparison.Ordinal)
                .Replace("$(AdminUser)", AdminUser, StringComparison.Ordinal)
                .Replace("$(JobUser)", JobUser, StringComparison.Ordinal);

            foreach (string batch in BatchSeparator().Split(script))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await ExecuteAsync(batch);
                }
            }
        }

        private static string FindSecurityScript()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "database", "security", "10_security_users_grants.sql");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("database/security/10_security_users_grants.sql was not found above the test output directory.");
        }
    }
}
