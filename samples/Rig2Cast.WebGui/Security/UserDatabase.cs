using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Rig2Cast.WebGui.Security;

public sealed partial class UserDatabase(string databasePath)
{
    public const int CurrentSchemaVersion = 1;
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, SchemaSql, cancellationToken, transaction).ConfigureAwait(false);

        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        object? stored = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            await ExecuteAsync(connection,
                $"INSERT INTO schema_info(id, version) VALUES (1, {CurrentSchemaVersion});",
                cancellationToken, transaction).ConfigureAwait(false);
        }
        else if (Convert.ToInt32(stored, CultureInfo.InvariantCulture) > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"User database schema version {stored} is newer than supported version {CurrentSchemaVersion}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true
    }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            normalized_username TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
            must_change_password INTEGER NOT NULL DEFAULT 1 CHECK (must_change_password IN (0, 1)),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_permissions (
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            permission_key TEXT NOT NULL,
            resource_id TEXT NOT NULL DEFAULT '',
            effect INTEGER NOT NULL DEFAULT 1 CHECK (effect IN (0, 1)),
            PRIMARY KEY (user_id, permission_key, resource_id)
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            token_hash BLOB NOT NULL UNIQUE,
            created_utc TEXT NOT NULL,
            expires_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            revoked_utc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_user_id ON sessions(user_id);
        CREATE INDEX IF NOT EXISTS ix_sessions_expires_utc ON sessions(expires_utc);

        CREATE TABLE IF NOT EXISTS audit_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            user_id TEXT NULL REFERENCES users(id) ON DELETE SET NULL,
            username TEXT NULL,
            action TEXT NOT NULL,
            resource_id TEXT NULL,
            outcome TEXT NOT NULL,
            remote_address TEXT NULL,
            details TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_audit_log_occurred_utc ON audit_log(occurred_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_audit_log_user_id ON audit_log(user_id);
        """;
}

public sealed class UserDatabaseStartupService(
    StationConfiguration station,
    UserDatabase database,
    UserAuthenticationService authentication,
    ILogger<UserDatabaseStartupService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> DatabaseInitialized =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1001, nameof(DatabaseInitialized)),
            "User database initialized at {DatabasePath}");
    private static readonly Action<ILogger, Exception?> BootstrapRequired =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1002, nameof(BootstrapRequired)),
            "User management is enabled but no users exist. Configure BootstrapAdminUsername and set the RIG2CAST_BOOTSTRAP_ADMIN_PASSWORD environment variable for one startup.");
    private static readonly Action<ILogger, string, Exception?> AdministratorCreated =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1003, nameof(AdministratorCreated)),
            "Created bootstrap administrator {Username}. Remove the bootstrap username and password configuration, then change the password after login.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!station.UserManagement.Enabled) return;
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        DatabaseInitialized(logger, database.DatabasePath, null);
        if (await database.HasUsersAsync(cancellationToken).ConfigureAwait(false)) return;

        string? username = station.UserManagement.BootstrapAdminUsername;
        string? password = Environment.GetEnvironmentVariable("RIG2CAST_BOOTSTRAP_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            BootstrapRequired(logger, null);
            return;
        }

        string passwordHash = authentication.HashPassword(username, username, password);
        UserGrant[] permissions = PermissionCatalog.All
            .Select(permission => new UserGrant(permission.Key, null, true))
            .ToArray();
        UserAccount administrator = await database.CreateUserAsync(
            username, username, passwordHash, mustChangePassword: true, permissions, cancellationToken)
            .ConfigureAwait(false);
        await database.WriteAuditAsync("users.bootstrap", "allowed", administrator.Id, administrator.Username,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Environment.SetEnvironmentVariable("RIG2CAST_BOOTSTRAP_ADMIN_PASSWORD", null);
        AdministratorCreated(logger, administrator.Username, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
