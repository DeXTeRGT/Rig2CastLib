using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;

namespace Rig2Cast.WebGui.Security;

public sealed record LoginResult(string SessionToken, DateTimeOffset ExpiresAt, AuthenticatedUser User);
public sealed record LoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AuthenticationStatus(bool Enabled, bool RequiresBootstrap);

public sealed class UserAuthenticationService(
    UserDatabase database,
    StationConfiguration station,
    TimeProvider timeProvider)
{
    public const string CookieName = "rig2cast_session";
    private readonly PasswordHasher<UserAccount> _passwordHasher = new();

    public string HashPassword(string username, string displayName, string password)
    {
        ValidatePassword(password);
        UserAccount placeholder = new("", username, displayName, "", true, true);
        return _passwordHasher.HashPassword(placeholder, password);
    }

    public async Task<LoginResult?> LoginAsync(string username, string password,
        string? remoteAddress, CancellationToken cancellationToken = default)
    {
        UserAccount? account = await database.FindUserAsync(username, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.Enabled ||
            _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            await database.WriteAuditAsync("auth.login", "denied", username: username,
                remoteAddress: remoteAddress, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = WebEncoders.Base64UrlEncode(tokenBytes);
        byte[] tokenHash = SHA256.HashData(tokenBytes);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expires = now.AddHours(station.UserManagement.SessionHours);
        await database.CreateSessionAsync(Guid.NewGuid().ToString("N"), account.Id, tokenHash, now, expires,
            cancellationToken).ConfigureAwait(false);
        AuthenticatedUser user = (await database.FindSessionAsync(tokenHash, now, cancellationToken)
            .ConfigureAwait(false))!;
        await database.WriteAuditAsync("auth.login", "allowed", account.Id, account.Username,
            remoteAddress: remoteAddress, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LoginResult(token, expires, user);
    }

    public async Task<AuthenticatedUser?> ResolveAsync(string? token,
        CancellationToken cancellationToken = default)
    {
        byte[]? hash = HashToken(token);
        return hash is null ? null : await database.FindSessionAsync(hash, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task LogoutAsync(string? token, string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        byte[]? hash = HashToken(token);
        if (hash is null) return;
        AuthenticatedUser? user = await database.FindSessionAsync(hash, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        await database.RevokeSessionAsync(hash, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await database.WriteAuditAsync("auth.logout", "allowed", user?.Id, user?.Username,
            remoteAddress: remoteAddress, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthenticatedUser?> ChangePasswordAsync(string? token, string currentPassword,
        string newPassword, string? remoteAddress, CancellationToken cancellationToken = default)
    {
        AuthenticatedUser? authenticated = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (authenticated is null) return null;
        UserAccount? account = await database.FindUserAsync(authenticated.Username, cancellationToken)
            .ConfigureAwait(false);
        if (account is null || _passwordHasher.VerifyHashedPassword(
                account, account.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            await database.WriteAuditAsync("auth.password.change", "denied", authenticated.Id,
                authenticated.Username, remoteAddress: remoteAddress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        ValidatePassword(newPassword);
        string passwordHash = _passwordHasher.HashPassword(account, newPassword);
        await database.UpdatePasswordAsync(account.Id, passwordHash, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await database.WriteAuditAsync("auth.password.change", "allowed", account.Id, account.Username,
            remoteAddress: remoteAddress, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private static byte[]? HashToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try { return SHA256.HashData(WebEncoders.Base64UrlDecode(token)); }
        catch (FormatException) { return null; }
    }

    private static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.", nameof(password));
        if (password.Length > 256) throw new ArgumentException("Password cannot exceed 256 characters.", nameof(password));
    }
}
