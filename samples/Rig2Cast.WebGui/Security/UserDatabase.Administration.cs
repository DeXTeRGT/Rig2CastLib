using Microsoft.Data.Sqlite;

namespace Rig2Cast.WebGui.Security;

public sealed record ManagedUser(
    string Id,
    string Username,
    string DisplayName,
    bool Enabled,
    bool MustChangePassword,
    IReadOnlyList<UserGrant> Permissions);

public sealed partial class UserDatabase
{
    public async Task<IReadOnlyList<ManagedUser>> ListUsersAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        List<UserAccount> accounts = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, username, display_name, password_hash, enabled, must_change_password
                FROM users ORDER BY normalized_username;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                accounts.Add(ReadUser(reader));
        }

        List<ManagedUser> users = [];
        foreach (UserAccount account in accounts)
            users.Add(new ManagedUser(account.Id, account.Username, account.DisplayName, account.Enabled,
                account.MustChangePassword,
                await ReadPermissionsAsync(connection, account.Id, cancellationToken).ConfigureAwait(false)));
        return users;
    }

    public async Task SetUserEnabledAsync(string userId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users SET enabled = $enabled, updated_utc = $now WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O",
            System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException($"User '{userId}' was not found.");
    }

    public async Task ReplacePermissionsAsync(string userId, IEnumerable<UserGrant> permissions,
        CancellationToken cancellationToken = default)
    {
        UserGrant[] validated = permissions.ToArray();
        foreach (UserGrant grant in validated) PermissionCatalog.Require(grant.Key);
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM user_permissions WHERE user_id = $id;";
            delete.Parameters.AddWithValue("$id", userId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (UserGrant grant in validated)
            await UpsertPermissionAsync(connection, transaction, userId, grant, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE id = $id;";
        command.Parameters.AddWithValue("$id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException($"User '{userId}' was not found.");
    }
}

public sealed record CreateManagedUserRequest(
    string Username,
    string DisplayName,
    string Password,
    bool MustChangePassword,
    IReadOnlyList<UserGrant>? Permissions);
public sealed record SetUserEnabledRequest(bool Enabled);
public sealed record ResetUserPasswordRequest(string Password, bool MustChangePassword = true);
public sealed record ReplaceUserPermissionsRequest(IReadOnlyList<UserGrant> Permissions);
