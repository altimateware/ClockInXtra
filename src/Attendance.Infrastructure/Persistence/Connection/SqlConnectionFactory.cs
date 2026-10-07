using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Attendance.Infrastructure.Persistence.Connection;

/// <summary>
/// Supplies connections to the attendance database.
/// </summary>
/// <remarks>
/// Deliberately an Infrastructure concern only: it is not exposed through
/// <c>Attendance.Application</c>. The application layer asks for repositories,
/// not for connections, so nothing above Infrastructure can reach the database
/// directly (Claude.md §5).
/// </remarks>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Borrows a connection for the duration of one operation.
    /// </summary>
    /// <remarks>
    /// Returns a lease rather than a bare connection because the caller must not
    /// have to know whether it owns what it was given. A repository called on its
    /// own gets a connection it should dispose; the same repository called inside
    /// a unit of work gets someone else's connection and transaction, which it
    /// must leave open.
    /// </remarks>
    Task<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken);

    /// <summary>The command timeout to apply, in seconds.</summary>
    int CommandTimeoutSeconds { get; }
}

/// <summary>
/// A connection borrowed for one operation, with any ambient transaction.
/// </summary>
/// <remarks>
/// <para>
/// Two things travel together here for a reason. ADO.NET refuses to execute a
/// command against a connection that has a pending local transaction unless that
/// transaction is passed on the command — so a repository that ignored the
/// ambient transaction would fail the moment it was composed with anything else.
/// </para>
/// <para>
/// Ownership travels with it too: disposing a borrowed connection would close the
/// caller's transaction underneath them.
/// </para>
/// </remarks>
public sealed class SqlConnectionLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    private SqlConnectionLease(SqlConnection connection, SqlTransaction? transaction, bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    /// <summary>The open connection.</summary>
    public SqlConnection Connection { get; }

    /// <summary>The ambient transaction, when the caller supplied one.</summary>
    public SqlTransaction? Transaction { get; }

    /// <summary>A connection this lease created and must dispose.</summary>
    public static SqlConnectionLease Owned(SqlConnection connection) =>
        new(connection, transaction: null, ownsConnection: true);

    /// <summary>
    /// A connection owned by someone else — typically a unit of work or a test
    /// that wraps the operation in a transaction it will roll back.
    /// </summary>
    public static SqlConnectionLease Borrowed(SqlConnection connection, SqlTransaction? transaction) =>
        new(connection, transaction, ownsConnection: false);

    /// <summary>
    /// Builds a stored-procedure command bound to this lease's transaction.
    /// </summary>
    public CommandDefinition StoredProcedure(
        string procedureName,
        object parameters,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        new(procedureName,
            parameters,
            transaction: Transaction,
            commandType: CommandType.StoredProcedure,
            commandTimeout: commandTimeoutSeconds,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        _ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// Configuration for the database connection.
/// </summary>
public sealed class SqlServerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SqlServer";

    /// <summary>
    /// The connection string.
    /// </summary>
    /// <remarks>
    /// Never committed to source control (§47). In production it comes from the
    /// organisation's secret mechanism or, preferably, from Windows
    /// authentication with a gMSA — which removes the password entirely rather
    /// than protecting it.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    /// <remarks>
    /// Kept short on purpose. The attendance procedures take row locks, and a
    /// request that is going to fail should fail while the employee is still
    /// looking at the screen — not hold locks that block the morning queue
    /// behind it (§22: timeout controls).
    /// </remarks>
    public int CommandTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Validates <see cref="SqlServerOptions"/> at startup.
/// </summary>
/// <remarks>
/// Registered with <c>ValidateOnStart</c>, so a misconfigured deployment fails
/// immediately and visibly instead of at the first clock-in of the morning.
/// </remarks>
public sealed class SqlServerOptionsValidator : IValidateOptions<SqlServerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SqlServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail(
                $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.ConnectionString)} is required.");
        }

        if (options.CommandTimeoutSeconds is < 1 or > 120)
        {
            return ValidateOptionsResult.Fail(
                $"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.CommandTimeoutSeconds)} must be between 1 and 120 seconds.");
        }

        SqlConnectionStringBuilder builder;

        try
        {
            builder = new SqlConnectionStringBuilder(options.ConnectionString);
        }
        catch (ArgumentException ex)
        {
            return ValidateOptionsResult.Fail($"The connection string is not valid: {ex.Message}");
        }

        // Encryption in transit is required (§48). Microsoft.Data.SqlClient 7
        // defaults Encrypt to Mandatory, so this catches a deployment that
        // deliberately turned it off rather than one that simply omitted it.
        if (builder.Encrypt == SqlConnectionEncryptOption.Optional)
        {
            return ValidateOptionsResult.Fail(
                "Encrypt=false is not permitted: connections to SQL Server must be encrypted.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Default <see cref="ISqlConnectionFactory"/>: one new connection per operation.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly SqlServerOptions _options;

    /// <summary>Creates the factory.</summary>
    public SqlConnectionFactory(IOptions<SqlServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public int CommandTimeoutSeconds => _options.CommandTimeoutSeconds;

    /// <inheritdoc />
    public async Task<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = new(_options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return SqlConnectionLease.Owned(connection);
        }
        catch
        {
            // Dispose rather than leak a half-opened connection back to the pool
            // on a cancellation or a failed login.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Conversions shared by the Dapper repositories.
/// </summary>
internal static class SqlConversions
{
    /// <summary>
    /// Converts a <c>datetime2</c> read from SQL Server into a
    /// <see cref="DateTimeOffset"/> in UTC.
    /// </summary>
    /// <remarks>
    /// This conversion is explicit for a reason. SQL Server returns
    /// <c>datetime2</c> as a <see cref="DateTime"/> with
    /// <see cref="DateTimeKind.Unspecified"/>, and .NET will happily treat an
    /// unspecified value as local time. On a server configured for any zone
    /// other than UTC that silently shifts every attendance timestamp by the
    /// offset — the kind of defect that looks like nothing until someone
    /// disputes an hour of pay. Every timestamp column in this schema is UTC by
    /// convention (decision DB-04), so the kind is stated rather than guessed.
    /// </remarks>
    public static DateTimeOffset ToUtcOffset(this DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Nullable overload of <see cref="ToUtcOffset(DateTime)"/>.</summary>
    public static DateTimeOffset? ToUtcOffset(this DateTime? value) =>
        value.HasValue ? value.Value.ToUtcOffset() : null;

    /// <summary>Converts a SQL <c>date</c> to a <see cref="DateOnly"/>.</summary>
    public static DateOnly? ToDateOnly(this DateTime? value) =>
        value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    /// <summary>Converts a SQL <c>time(0)</c> to a <see cref="TimeOnly"/>.</summary>
    public static TimeOnly? ToTimeOnly(this TimeSpan? value) =>
        value.HasValue ? TimeOnly.FromTimeSpan(value.Value) : null;
}
