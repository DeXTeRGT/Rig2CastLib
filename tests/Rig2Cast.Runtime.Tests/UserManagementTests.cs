using Microsoft.Data.Sqlite;
using Rig2Cast.WebGui;
using Rig2Cast.WebGui.Security;

namespace Rig2Cast.Runtime.Tests;

public sealed class UserManagementTests
{
    [Fact]
    public void PermissionCatalogHasUniqueKeysAndSensitiveOverrides()
    {
        Assert.Equal(PermissionCatalog.All.Count,
            PermissionCatalog.All.Select(permission => permission.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.True(PermissionCatalog.IsKnown(PermissionCatalog.Keys.TransmitPower));
        Assert.True(PermissionCatalog.IsKnown(PermissionCatalog.Keys.TransmitPtt));
        Assert.True(PermissionCatalog.IsKnown(PermissionCatalog.Keys.RadioPowerOff));
        Assert.True(PermissionCatalog.IsKnown(PermissionCatalog.Keys.RadioPreempt));
        Assert.True(PermissionCatalog.Require(PermissionCatalog.Keys.RotatorMove).SupportsResource);
        Assert.Throws<ArgumentException>(() => PermissionCatalog.Require("radio.unknown"));
    }

    [Fact]
    public void StationSessionIsExclusiveAndTakeoverRevokesPreviousOwner()
    {
        using StationSessionCoordinator sessions = new();
        AuthenticatedUser operatorUser = new("operator-id", "operator", "Regular Operator", false, []);
        AuthenticatedUser administrator = new("admin-id", "admin", "Administrator", false,
            [new UserGrant(PermissionCatalog.Keys.RadioPreempt, null, true)]);

        CancellationToken operatorRevoked = sessions.Acquire(operatorUser, "client-one");
        StationSessionBusyException busy = Assert.Throws<StationSessionBusyException>(
            () => sessions.Acquire(administrator, "client-two"));
        Assert.Equal("Regular Operator", busy.DisplayName);
        Assert.False(operatorRevoked.IsCancellationRequested);

        DisplacedStationSession displaced = sessions.TakeOver(administrator, "client-two");
        Assert.Equal("client-one", displaced.ClientId);
        Assert.True(operatorRevoked.IsCancellationRequested);
        Assert.True(sessions.GetStatus(administrator, "client-two", true).OwnedByCurrentUser);
        Assert.Throws<UnauthorizedAccessException>(
            () => sessions.RequireOwner(operatorUser, "client-one"));
        Assert.True(sessions.Release(administrator, "client-two"));
        Assert.True(sessions.GetStatus(operatorUser, "client-one", false).Available);
    }

    [Fact]
    public async Task UserDatabaseCreatesVersionedSecuritySchema()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rig2cast-users-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "users.db");
        try
        {
            UserDatabase database = new(path);
            await database.InitializeAsync();
            await database.InitializeAsync();

            await using SqliteConnection connection = database.OpenConnection();
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
            Assert.Equal(UserDatabase.CurrentSchemaVersion, Convert.ToInt32(await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));

            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            HashSet<string> tables = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            Assert.Contains("users", tables);
            Assert.Contains("user_permissions", tables);
            Assert.Contains("sessions", tables);
            Assert.Contains("audit_log", tables);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticationCreatesAndRevokesHashedSessionWithDirectGrants()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rig2cast-auth-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "users.db");
        try
        {
            UserDatabase database = new(path);
            await database.InitializeAsync();
            StationConfiguration station = new();
            station.UserManagement.Enabled = true;
            station.UserManagement.SessionHours = 6;
            UserAuthenticationService authentication = new(database, station, TimeProvider.System);
            string passwordHash = authentication.HashPassword("operator", "Station operator", "correct horse battery staple");
            await database.CreateUserAsync("operator", "Station operator", passwordHash, false,
            [
                new UserGrant(PermissionCatalog.Keys.RadioView, null, true),
                new UserGrant(PermissionCatalog.Keys.TransmitPower, null, false)
            ]);

            Assert.Null(await authentication.LoginAsync("operator", "incorrect password", "127.0.0.1"));
            LoginResult? login = await authentication.LoginAsync(
                "OPERATOR", "correct horse battery staple", "127.0.0.1");
            Assert.NotNull(login);
            Assert.Equal("operator", login.User.Username);
            Assert.Contains(login.User.Permissions,
                grant => grant.Key == PermissionCatalog.Keys.RadioView && grant.Allowed);
            Assert.Contains(login.User.Permissions,
                grant => grant.Key == PermissionCatalog.Keys.TransmitPower && !grant.Allowed);

            Assert.NotNull(await authentication.ResolveAsync(login.SessionToken));
            AuthenticatedUser? changed = await authentication.ChangePasswordAsync(login.SessionToken,
                "correct horse battery staple", "a different horse battery staple", "127.0.0.1");
            Assert.NotNull(changed);
            Assert.False(changed.MustChangePassword);
            Assert.Null(await authentication.LoginAsync(
                "operator", "correct horse battery staple", "127.0.0.1"));
            Assert.NotNull(await authentication.LoginAsync(
                "operator", "a different horse battery staple", "127.0.0.1"));
            await authentication.LogoutAsync(login.SessionToken, "127.0.0.1");
            Assert.Null(await authentication.ResolveAsync(login.SessionToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResourcePermissionUsesExactGrantBeforeGlobalFallback()
    {
        UserGrant[] grants =
        [
            new(PermissionCatalog.Keys.RotatorMove, null, true),
            new(PermissionCatalog.Keys.RotatorMove, "tower", false),
            new(PermissionCatalog.Keys.RotatorPark, "tower", true),
            new(PermissionCatalog.Keys.TransmitPower, null, false)
        ];

        Assert.True(PermissionAuthorizationService.IsAllowed(
            grants, PermissionCatalog.Keys.RotatorMove, "roof"));
        Assert.False(PermissionAuthorizationService.IsAllowed(
            grants, PermissionCatalog.Keys.RotatorMove, "tower"));
        Assert.True(PermissionAuthorizationService.IsAllowed(
            grants, PermissionCatalog.Keys.RotatorPark, "tower"));
        Assert.False(PermissionAuthorizationService.IsAllowed(
            grants, PermissionCatalog.Keys.RotatorPark, "roof"));
        Assert.False(PermissionAuthorizationService.IsAllowed(
            grants, PermissionCatalog.Keys.TransmitPower));
    }

    [Fact]
    public async Task AdministrationCanManageUsersAndReplaceDirectGrants()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rig2cast-admin-{Guid.NewGuid():N}");
        try
        {
            UserDatabase database = new(Path.Combine(directory, "users.db"));
            await database.InitializeAsync();
            UserAuthenticationService authentication = new(database, new StationConfiguration(), TimeProvider.System);
            UserAccount account = await database.CreateUserAsync("guest", "Guest operator",
                authentication.HashPassword("guest", "Guest operator", "temporary password 123"), true,
                [new UserGrant(PermissionCatalog.Keys.RadioView, null, true)]);

            ManagedUser user = Assert.Single(await database.ListUsersAsync());
            Assert.Equal(account.Id, user.Id);
            Assert.True(user.MustChangePassword);
            await database.ReplacePermissionsAsync(user.Id,
                [new UserGrant(PermissionCatalog.Keys.RotatorView, "tower", true)]);
            user = Assert.Single(await database.ListUsersAsync());
            Assert.Single(user.Permissions);
            Assert.Equal("tower", user.Permissions[0].ResourceId);

            await database.SetUserEnabledAsync(user.Id, false);
            Assert.False(Assert.Single(await database.ListUsersAsync()).Enabled);
            await database.DeleteUserAsync(user.Id);
            Assert.Empty(await database.ListUsersAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
