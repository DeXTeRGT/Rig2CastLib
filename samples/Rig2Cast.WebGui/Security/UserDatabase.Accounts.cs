using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Rig2Cast.WebGui.Security;

public sealed record UserAccount(
    string Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    bool Enabled,
    bool MustChangePassword);

public sealed record UserGrant(string Key, string? ResourceId, bool Allowed);

public sealed record AuthenticatedUser(
    string Id,
    string Username,
    string DisplayName,
    bool MustChangePassword,
    IReadOnlyList<UserGrant> Permissions);

public sealed partial class UserDatabase
{
    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    public async Task<UserAccount> CreateUserAsync(string username, string displayName, string passwordHash,
        bool mustChangePassword, IEnumerable<UserGrant>? permissions = null,
        CancellationToken cancellationToken = default)
    {
        username = ValidateUsername(username);
        displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO users(id, username, normalized_username, display_name, password_hash,
                    enabled, must_change_password, created_utc, updated_utc)
                VALUES ($id, $username, $normalized, $display, $password, 1, $mustChange, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$normalized", NormalizeUsername(username));
            command.Parameters.AddWithValue("$display", displayName);
            command.Parameters.AddWithValue("$password", passwordHash);
            command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (permissions is not null)
        {
            foreach (UserGrant permission in permissions)
                await UpsertPermissionAsync(connection, transaction, id, permission, cancellationToken)
                    .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserAccount(id, username, displayName, passwordHash, true, mustChangePassword);
    }

    public async Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, display_name, password_hash, enabled, must_change_password
            FROM users WHERE normalized_username = $normalized;
            """;
        command.Parameters.AddWithValue("$normalized", NormalizeUsername(username));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    public async Task UpdatePasswordAsync(string userId, string passwordHash, bool mustChangePassword = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET password_hash = $password, must_change_password = $mustChange, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$password", passwordHash);
        command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException($"User '{userId}' was not found.");
    }

    public async Task CreateSessionAsync(string id, string userId, byte[] tokenHash,
        DateTimeOffset created, DateTimeOffset expires, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, user_id, token_hash, created_utc, expires_utc, last_seen_utc)
            VALUES ($id, $userId, $hash, $created, $expires, $created);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$created", created.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expires", expires.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthenticatedUser?> FindSessionAsync(byte[] tokenHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? userId = null;
        UserAccount? account = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT u.id, u.username, u.display_name, u.password_hash, u.enabled, u.must_change_password
                FROM sessions s JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = $hash AND s.revoked_utc IS NULL AND s.expires_utc > $now AND u.enabled = 1;
                """;
            command.Parameters.AddWithValue("$hash", tokenHash);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                account = ReadUser(reader);
                userId = account.Id;
            }
        }
        if (account is null || userId is null) return null;

        IReadOnlyList<UserGrant> permissions = await ReadPermissionsAsync(connection, userId, cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand touch = connection.CreateCommand();
        touch.CommandText = "UPDATE sessions SET last_seen_utc = $now WHERE token_hash = $hash;";
        touch.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        touch.Parameters.AddWithValue("$hash", tokenHash);
        await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new AuthenticatedUser(account.Id, account.Username, account.DisplayName,
            account.MustChangePassword, permissions);
    }

    public async Task RevokeSessionAsync(byte[] tokenHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET revoked_utc = $now WHERE token_hash = $hash AND revoked_utc IS NULL;";
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hash", tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAuditAsync(string action, string outcome, string? userId = null,
        string? username = null, string? resourceId = null, string? remoteAddress = null,
        string? details = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_log(occurred_utc, user_id, username, action, resource_id, outcome, remote_address, details)
            VALUES ($now, $userId, $username, $action, $resource, $outcome, $remote, $details);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$resource", (object?)resourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$remote", (object?)remoteAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<UserGrant>> ReadPermissionsAsync(SqliteConnection connection,
        string userId, CancellationToken cancellationToken)
    {
        List<UserGrant> result = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT permission_key, resource_id, effect FROM user_permissions
            WHERE user_id = $userId ORDER BY permission_key, resource_id;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new UserGrant(reader.GetString(0), NullIfEmpty(reader.GetString(1)), reader.GetInt32(2) == 1));
        return result;
    }

    private static async Task UpsertPermissionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string userId, UserGrant permission, CancellationToken cancellationToken)
    {
        PermissionDefinition definition = PermissionCatalog.Require(permission.Key);
        string resource = permission.ResourceId?.Trim() ?? "";
        if (!definition.SupportsResource && resource.Length != 0)
            throw new ArgumentException($"Permission '{permission.Key}' does not support a resource ID.");
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_permissions(user_id, permission_key, resource_id, effect)
            VALUES ($userId, $key, $resource, $effect)
            ON CONFLICT(user_id, permission_key, resource_id) DO UPDATE SET effect = excluded.effect;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$key", permission.Key);
        command.Parameters.AddWithValue("$resource", resource);
        command.Parameters.AddWithValue("$effect", permission.Allowed ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UserAccount ReadUser(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt32(4) == 1, reader.GetInt32(5) == 1);

    private static string ValidateUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        username = username.Trim();
        if (username.Length is < 3 or > 64)
            throw new ArgumentException("Username must contain between 3 and 64 characters.", nameof(username));
        if (username.Any(character => char.IsControl(character)))
            throw new ArgumentException("Username cannot contain control characters.", nameof(username));
        return username;
    }

    private static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
