namespace Rig2Cast.WebGui.Security;

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException() : base("Authentication is required.") { }
}

public sealed class PermissionDeniedException(string permission, string? resourceId = null) : Exception(
    resourceId is null
        ? $"Permission '{permission}' is required."
        : $"Permission '{permission}' is required for resource '{resourceId}'.")
{
    public string Permission { get; } = permission;
    public string? ResourceId { get; } = resourceId;
}

public sealed class PermissionAuthorizationService(
    StationConfiguration station,
    UserAuthenticationService authentication,
    UserDatabase database)
{
    public async Task<AuthenticatedUser?> AuthenticateAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (!station.UserManagement.Enabled) return null;
        context.Request.Cookies.TryGetValue(UserAuthenticationService.CookieName, out string? token);
        AuthenticatedUser? user = await authentication.ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        return user ?? throw new AuthenticationRequiredException();
    }

    public async Task<AuthenticatedUser?> RequireAsync(HttpContext context, string permission,
        string? resourceId = null, CancellationToken cancellationToken = default)
    {
        PermissionCatalog.Require(permission);
        AuthenticatedUser? user = await AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        if (user is null) return null;

        if (IsAllowed(user.Permissions, permission, resourceId)) return user;

        await database.WriteAuditAsync("permission.check", "denied", user.Id, user.Username,
            resourceId, context.Connection.RemoteIpAddress?.ToString(), permission,
            cancellationToken).ConfigureAwait(false);
        throw new PermissionDeniedException(permission, resourceId);
    }

    public static bool IsAllowed(IReadOnlyList<UserGrant> grants, string permission, string? resourceId = null)
    {
        IEnumerable<UserGrant> matching = grants.Where(grant =>
            StringComparer.Ordinal.Equals(grant.Key, permission));

        if (resourceId is not null)
        {
            UserGrant? exact = matching.FirstOrDefault(grant =>
                StringComparer.Ordinal.Equals(grant.ResourceId, resourceId));
            if (exact is not null) return exact.Allowed;
        }

        UserGrant? global = matching.FirstOrDefault(grant => grant.ResourceId is null);
        return global?.Allowed == true;
    }
}

public static class PermissionEndpointExtensions
{
    public static RouteHandlerBuilder RequireStationOwner(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            PermissionAuthorizationService authorization =
                http.RequestServices.GetRequiredService<PermissionAuthorizationService>();
            AuthenticatedUser? user = await authorization.AuthenticateAsync(http, http.RequestAborted)
                .ConfigureAwait(false);
            StationSessionCoordinator sessions =
                http.RequestServices.GetRequiredService<StationSessionCoordinator>();
            sessions.RequireOwner(user, ApiClientIdentity.Require(http));
            return await next(context);
        });
    }

    public static RouteHandlerBuilder RequireRigAuthentication(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            StationConfiguration station = http.RequestServices.GetRequiredService<StationConfiguration>();
            if (station.UserManagement.Enabled)
            {
                UserAuthenticationService authentication =
                    http.RequestServices.GetRequiredService<UserAuthenticationService>();
                http.Request.Cookies.TryGetValue(UserAuthenticationService.CookieName, out string? token);
                if (await authentication.ResolveAsync(token, http.RequestAborted).ConfigureAwait(false) is null)
                    throw new AuthenticationRequiredException();
            }
            return await next(context);
        });
    }

    public static RouteHandlerBuilder RequireRigPermission(this RouteHandlerBuilder builder, string permission,
        string? resourceRouteValue = null)
    {
        PermissionCatalog.Require(permission);
        return builder.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            string? resourceId = resourceRouteValue is null
                ? null
                : http.Request.RouteValues[resourceRouteValue]?.ToString();
            PermissionAuthorizationService authorization =
                http.RequestServices.GetRequiredService<PermissionAuthorizationService>();
            await authorization.RequireAsync(http, permission, resourceId, http.RequestAborted);
            return await next(context);
        });
    }
}
