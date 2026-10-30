using System.Net.WebSockets;
using System.Text.Json.Serialization;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Rotators;
using Rig2Cast.WebGui;
using Rig2Cast.WebGui.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:8080");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
StationConfiguration station = StationConfiguration.Load(builder.Configuration);
builder.Services.AddSingleton(station);
builder.Services.AddSingleton(new UserDatabase(
    station.UserManagement.ResolveDatabasePath(builder.Environment.ContentRootPath)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UserAuthenticationService>();
builder.Services.AddSingleton<PermissionAuthorizationService>();
builder.Services.AddSingleton<StationSessionCoordinator>();
builder.Services.AddHostedService<UserDatabaseStartupService>();
builder.Services.AddSingleton<RadioWebHost>();
builder.Services.AddHostedService<StationStartupService>();
builder.Services.AddSingleton<RotatorWebHost>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RotatorWebHost>());
builder.Services.AddSingleton<GhostLinkAudioCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<GhostLinkAudioCoordinator>());
builder.Services.AddSingleton<SpectrumStreamService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SpectrumStreamService>());

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    Exception? error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch
    {
        AuthenticationRequiredException => StatusCodes.Status401Unauthorized,
        PermissionDeniedException => StatusCodes.Status403Forbidden,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Request failed." });
}));

RouteGroupBuilder api = app.MapGroup("/api/v1");
api.MapGet("/auth/status", async (StationConfiguration config, UserDatabase database, CancellationToken ct) =>
    Results.Ok(new AuthenticationStatus(config.UserManagement.Enabled,
        config.UserManagement.Enabled && !await database.HasUsersAsync(ct))));
api.MapPost("/auth/login", async (LoginRequest request, HttpContext context,
    StationConfiguration config, UserAuthenticationService authentication, CancellationToken ct) =>
{
    if (!config.UserManagement.Enabled) return Results.NotFound();
    LoginResult? result = await authentication.LoginAsync(request.Username, request.Password,
        context.Connection.RemoteIpAddress?.ToString(), ct);
    if (result is null) return Results.Unauthorized();
    context.Response.Cookies.Append(UserAuthenticationService.CookieName, result.SessionToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = result.ExpiresAt
    });
    return Results.Ok(result.User);
});
api.MapPost("/auth/logout", async (HttpContext context, StationConfiguration config,
    UserAuthenticationService authentication, CancellationToken ct) =>
{
    if (!config.UserManagement.Enabled) return Results.NotFound();
    context.Request.Cookies.TryGetValue(UserAuthenticationService.CookieName, out string? token);
    await authentication.LogoutAsync(token, context.Connection.RemoteIpAddress?.ToString(), ct);
    context.Response.Cookies.Delete(UserAuthenticationService.CookieName, new CookieOptions
    { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/" });
    return Results.NoContent();
});
api.MapGet("/auth/me", async (HttpContext context, StationConfiguration config,
    UserAuthenticationService authentication, CancellationToken ct) =>
{
    if (!config.UserManagement.Enabled) return Results.NotFound();
    context.Request.Cookies.TryGetValue(UserAuthenticationService.CookieName, out string? token);
    AuthenticatedUser? user = await authentication.ResolveAsync(token, ct);
    return user is null ? Results.Unauthorized() : Results.Ok(user);
});
api.MapGet("/station-session", async (HttpContext context,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions,
    CancellationToken ct) =>
{
    AuthenticatedUser? user = await authorization.AuthenticateAsync(context, ct);
    bool canTakeOver = user is null || PermissionAuthorizationService.IsAllowed(
        user.Permissions, PermissionCatalog.Keys.RadioPreempt);
    return Results.Ok(sessions.GetStatus(user, ApiClientIdentity.Require(context), canTakeOver));
}).RequireRigAuthentication();
api.MapPost("/station-session/takeover", async (HttpContext context,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions,
    RadioWebHost host, UserDatabase database, CancellationToken ct) =>
{
    AuthenticatedUser? user = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.RadioPreempt, cancellationToken: ct);
    string clientId = ApiClientIdentity.Require(context);
    DisplacedStationSession displaced = sessions.TakeOver(user, clientId);
    if (!string.IsNullOrEmpty(displaced.ClientId)) await host.DetachClientAsync(displaced.ClientId);
    await database.WriteAuditAsync("station.takeover", "allowed", user?.Id, user?.Username,
        displaced.ClientId, context.Connection.RemoteIpAddress?.ToString(), displaced.DisplayName, ct);
    return Results.Ok(sessions.GetStatus(user, clientId, true));
});
api.MapPost("/auth/change-password", async (ChangePasswordRequest request, HttpContext context,
    StationConfiguration config, UserAuthenticationService authentication, CancellationToken ct) =>
{
    if (!config.UserManagement.Enabled) return Results.NotFound();
    context.Request.Cookies.TryGetValue(UserAuthenticationService.CookieName, out string? token);
    AuthenticatedUser? user = await authentication.ChangePasswordAsync(token, request.CurrentPassword,
        request.NewPassword, context.Connection.RemoteIpAddress?.ToString(), ct);
    return user is null ? Results.Unauthorized() : Results.Ok(user);
});
api.MapGet("/admin/permissions", () => Results.Ok(PermissionCatalog.All))
    .RequireRigPermission(PermissionCatalog.Keys.UsersManage);
api.MapGet("/admin/users", async (UserDatabase database, CancellationToken ct) =>
    Results.Ok(await database.ListUsersAsync(ct)))
    .RequireRigPermission(PermissionCatalog.Keys.UsersManage);
api.MapPost("/admin/users", async (CreateManagedUserRequest request, HttpContext context,
    UserDatabase database, UserAuthenticationService authentication,
    PermissionAuthorizationService authorization, CancellationToken ct) =>
{
    AuthenticatedUser? actor = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.UsersManage, cancellationToken: ct);
    string hash = authentication.HashPassword(request.Username, request.DisplayName, request.Password);
    UserAccount user = await database.CreateUserAsync(request.Username, request.DisplayName, hash,
        request.MustChangePassword, request.Permissions, ct);
    await database.WriteAuditAsync("user.create", "allowed", actor?.Id, actor?.Username, user.Id,
        context.Connection.RemoteIpAddress?.ToString(), user.Username, ct);
    return Results.Created($"/api/v1/admin/users/{user.Id}", user with { PasswordHash = "" });
});
api.MapPut("/admin/users/{id}/enabled", async (string id, SetUserEnabledRequest request,
    HttpContext context, UserDatabase database, PermissionAuthorizationService authorization,
    CancellationToken ct) =>
{
    AuthenticatedUser? actor = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.UsersManage, cancellationToken: ct);
    if (actor?.Id == id && !request.Enabled)
        throw new InvalidOperationException("You cannot disable your own account.");
    await database.SetUserEnabledAsync(id, request.Enabled, ct);
    await database.WriteAuditAsync("user.enabled", "allowed", actor?.Id, actor?.Username, id,
        context.Connection.RemoteIpAddress?.ToString(), request.Enabled.ToString(), ct);
    return Results.NoContent();
});
api.MapPut("/admin/users/{id}/permissions", async (string id, ReplaceUserPermissionsRequest request,
    HttpContext context, UserDatabase database, PermissionAuthorizationService authorization,
    CancellationToken ct) =>
{
    AuthenticatedUser? actor = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.UsersManage, cancellationToken: ct);
    if (actor?.Id == id && !request.Permissions.Any(grant =>
            grant.Key == PermissionCatalog.Keys.UsersManage && grant.ResourceId is null && grant.Allowed))
        throw new InvalidOperationException("You cannot remove your own user-management permission.");
    await database.ReplacePermissionsAsync(id, request.Permissions, ct);
    await database.WriteAuditAsync("user.permissions", "allowed", actor?.Id, actor?.Username, id,
        context.Connection.RemoteIpAddress?.ToString(), cancellationToken: ct);
    return Results.NoContent();
});
api.MapPut("/admin/users/{id}/password", async (string id, ResetUserPasswordRequest request,
    HttpContext context, UserDatabase database, UserAuthenticationService authentication,
    PermissionAuthorizationService authorization, CancellationToken ct) =>
{
    AuthenticatedUser? actor = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.UsersManage, cancellationToken: ct);
    ManagedUser user = (await database.ListUsersAsync(ct)).FirstOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException($"User '{id}' was not found.");
    string hash = authentication.HashPassword(user.Username, user.DisplayName, request.Password);
    await database.UpdatePasswordAsync(id, hash, request.MustChangePassword, ct);
    await database.WriteAuditAsync("user.password.reset", "allowed", actor?.Id, actor?.Username, id,
        context.Connection.RemoteIpAddress?.ToString(), cancellationToken: ct);
    return Results.NoContent();
});
api.MapDelete("/admin/users/{id}", async (string id, HttpContext context, UserDatabase database,
    PermissionAuthorizationService authorization, CancellationToken ct) =>
{
    AuthenticatedUser? actor = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.UsersManage, cancellationToken: ct);
    if (actor?.Id == id) throw new InvalidOperationException("You cannot delete your own account.");
    await database.DeleteUserAsync(id, ct);
    await database.WriteAuditAsync("user.delete", "allowed", actor?.Id, actor?.Username, id,
        context.Connection.RemoteIpAddress?.ToString(), cancellationToken: ct);
    return Results.NoContent();
});
api.MapGet("/status", (RadioWebHost host) => host.GetStatus()).RequireRigAuthentication();
api.MapGet("/radio-power", async (RadioWebHost host, CancellationToken ct) => Results.Ok(await host.GetPowerStateAsync(ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPut("/radio-power", async (BoolValue body, HttpContext context, RadioWebHost host,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions, CancellationToken ct) =>
{
    AuthenticatedUser? user = await authorization.RequireAsync(context, body.Value
        ? PermissionCatalog.Keys.RadioPowerOn
        : PermissionCatalog.Keys.RadioPowerOff, cancellationToken: ct);
    sessions.RequireAvailableOrOwner(user, ApiClientIdentity.Require(context));
    return Results.Ok(await host.SetPowerStateAsync(body.Value, ct));
});
api.MapGet("/spectrum/status", (SpectrumStreamService spectrum) => spectrum.GetStatus())
    .RequireRigPermission(PermissionCatalog.Keys.SpectrumView);
api.MapGet("/spectrum/levels", (SpectrumStreamService spectrum) => spectrum.GetLevels())
    .RequireRigPermission(PermissionCatalog.Keys.SpectrumView);
api.MapPut("/spectrum/levels", (SpectrumLevels body, SpectrumStreamService spectrum) => Results.Ok(spectrum.SetLevels(body.MinimumLevel, body.MaximumLevel)))
    .RequireRigPermission(PermissionCatalog.Keys.SpectrumSettings)
    .RequireStationOwner();
api.MapGet("/models", (RadioWebHost host) => host.GetModels()).RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapGet("/serial-ports", (RadioWebHost host) => host.GetSerialPorts()).RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapGet("/radios", (RadioWebHost host) => host.GetRadios()).RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapGet("/rotators/configuration", (RotatorWebHost host) => host.GetConfiguration())
    .RequireRigPermission(PermissionCatalog.Keys.RotatorView);
api.MapGet("/rotators", (RotatorWebHost host) => host.GetRotators())
    .RequireRigPermission(PermissionCatalog.Keys.RotatorView);
api.MapGet("/rotators/{id}", (string id, RotatorWebHost host) => host.GetRotator(id))
    .RequireRigPermission(PermissionCatalog.Keys.RotatorView, "id");
api.MapPut("/rotators/{id}/azimuth", async (string id, DoubleValue body, RotatorWebHost host, CancellationToken ct) =>
{ await host.MoveToAsync(id, body.Value, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RotatorMove, "id").RequireStationOwner();
api.MapPost("/rotators/{id}/clockwise", async (string id, RotatorWebHost host, CancellationToken ct) =>
{ await host.StartAsync(id, RotatorDirection.Clockwise, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RotatorMove, "id").RequireStationOwner();
api.MapPost("/rotators/{id}/counterclockwise", async (string id, RotatorWebHost host, CancellationToken ct) =>
{ await host.StartAsync(id, RotatorDirection.Counterclockwise, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RotatorMove, "id").RequireStationOwner();
api.MapPost("/rotators/{id}/stop", async (string id, RotatorWebHost host, CancellationToken ct) =>
{ await host.StopAsync(id, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RotatorStop, "id").RequireStationOwner();
api.MapPost("/rotators/{id}/park", async (string id, RotatorWebHost host, CancellationToken ct) =>
{ await host.ParkAsync(id, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RotatorPark, "id").RequireStationOwner();
api.MapPost("/radios/connect", async (ConnectRequest request, HttpContext context, RadioWebHost host,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions, CancellationToken ct) =>
{
    AuthenticatedUser? user = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.RadioConnect, cancellationToken: ct);
    string clientId = ApiClientIdentity.Require(context);
    sessions.Acquire(user, clientId);
    try { return Results.Ok(await host.ConnectOrAttachAsync(request with { ClientId = clientId }, ct)); }
    catch
    {
        sessions.Release(user, clientId);
        throw;
    }
});
api.MapPost("/radios/{radioId}/detach", async (string radioId, HttpContext context, RadioWebHost host,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions) =>
{
    AuthenticatedUser? user = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.RadioConnect, cancellationToken: context.RequestAborted);
    string clientId = ApiClientIdentity.Require(context);
    await host.DetachAsync(radioId, clientId);
    sessions.Release(user, clientId);
    return Results.NoContent();
});
api.MapDelete("/radios/{radioId}", async (string radioId, HttpContext context, RadioWebHost host,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions) =>
{
    AuthenticatedUser? user = await authorization.RequireAsync(
        context, PermissionCatalog.Keys.RadioConnect, cancellationToken: context.RequestAborted);
    string clientId = ApiClientIdentity.Require(context);
    await host.CloseRadioAsync(radioId, clientId);
    sessions.Release(user, clientId);
    return Results.NoContent();
});
api.MapGet("/radios/{radioId}/snapshot", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.GetSnapshotAsync(radioId, ApiClientIdentity.Require(context), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPost("/radios/{radioId}/refresh", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.RefreshAsync(radioId, ApiClientIdentity.Require(context), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPut("/radios/{radioId}/frequency/{vfo}", async (string radioId, string vfo, LongValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetFrequencyAsync(radioId, clientId, Enum.Parse<VfoId>(vfo, true), body.Value, ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
}).RequireRigPermission(PermissionCatalog.Keys.TuningControl);
api.MapPut("/radios/{radioId}/active-vfo", async (string radioId, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetActiveVfoAsync(radioId, clientId, Enum.Parse<VfoId>(body.Value, true), ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
}).RequireRigPermission(PermissionCatalog.Keys.TuningControl);
api.MapPut("/radios/{radioId}/mode", async (string radioId, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetModeAsync(radioId, clientId, Enum.Parse<RadioMode>(body.Value, true), ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
}).RequireRigPermission(PermissionCatalog.Keys.ModeControl);
api.MapPut("/radios/{radioId}/split", async (string radioId, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetSplitAsync(radioId, clientId, body.Value, ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
}).RequireRigPermission(PermissionCatalog.Keys.TransmitControl);
api.MapPut("/radios/{radioId}/ptt", async (string radioId, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.SetPttAsync(radioId, ApiClientIdentity.Require(context), body.Value, ct)))
    .RequireRigPermission(PermissionCatalog.Keys.TransmitPtt);
api.MapPost("/radios/{radioId}/ptt/renew", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.RenewPttAsync(radioId, ApiClientIdentity.Require(context), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.TransmitPtt);
api.MapPost("/radios/{radioId}/controls/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadControlAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioControlId>(id, true), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPut("/radios/{radioId}/controls/{id}", async (string radioId, string id, IntValue body,
    HttpContext context, RadioWebHost host, PermissionAuthorizationService authorization, CancellationToken ct) =>
{
    RadioControlId control = Enum.Parse<RadioControlId>(id, true);
    await authorization.RequireAsync(context, control == RadioControlId.TransmitPower
        ? PermissionCatalog.Keys.TransmitPower
        : PermissionCatalog.Keys.RadioControl, cancellationToken: ct);
    await host.WriteControlAsync(radioId, ApiClientIdentity.Require(context), control, body.Value, ct);
    return Results.NoContent();
});
api.MapPost("/radios/{radioId}/switches/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadSwitchAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioSwitchId>(id, true), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPut("/radios/{radioId}/switches/{id}", async (string radioId, string id, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WriteSwitchAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioSwitchId>(id, true), body.Value, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RadioControl);
api.MapPost("/radios/{radioId}/choices/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadChoiceAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioChoiceId>(id, true), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPut("/radios/{radioId}/choices/{id}", async (string radioId, string id, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WriteChoiceAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioChoiceId>(id, true), body.Value, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.RadioControl);
api.MapPost("/radios/{radioId}/actions/{id}", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    await host.ExecuteActionAsync(
        radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioActionId>(id, true), ct);
    return Results.NoContent();
}).RequireRigPermission(PermissionCatalog.Keys.RadioControl);
api.MapPost("/radios/{radioId}/meters/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadMeterAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioMeterId>(id, true), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.RadioView);
api.MapPost("/radios/{radioId}/passband/read", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadPassbandAsync(radioId, ApiClientIdentity.Require(context), ct)))
    .RequireRigPermission(PermissionCatalog.Keys.ReceiverView);
api.MapPut("/radios/{radioId}/passband", async (string radioId, IntValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WritePassbandAsync(radioId, ApiClientIdentity.Require(context), body.Value, ct); return Results.NoContent(); })
    .RequireRigPermission(PermissionCatalog.Keys.ReceiverControl);

app.Map("/api/v1/radios/{radioId}/events", async (HttpContext context, string radioId, RadioWebHost host,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions,
    IHostApplicationLifetime lifetime) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    AuthenticatedUser? user = await authorization.RequireAsync(context, PermissionCatalog.Keys.RadioView,
        cancellationToken: context.RequestAborted);
    string clientId = context.Request.Query["clientId"].ToString();
    if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("The WebSocket clientId query parameter is required.");
    StationConnectionLease ownership = sessions.BeginPrimaryConnection(user, clientId);
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, lifetime.ApplicationStopping, ownership.Revoked);
    using CancellationTokenRegistration abort = lifetime.ApplicationStopping.Register(socket.Abort);
    try { await host.StreamSnapshotsAsync(radioId, clientId, socket, shutdown.Token); }
    finally
    {
        sessions.ReleaseAfterDisconnect(user, clientId, ownership.Generation, TimeSpan.FromSeconds(16));
    }
});

app.Map("/api/v1/audio/stream", async (HttpContext context, StationConfiguration stationConfig,
    GhostLinkAudioCoordinator audio, PermissionAuthorizationService authorization,
    StationSessionCoordinator sessions, IHostApplicationLifetime lifetime) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    AuthenticatedUser? user = await authorization.RequireAsync(context, PermissionCatalog.Keys.AudioView,
        cancellationToken: context.RequestAborted);
    string clientId = context.Request.Query["clientId"].ToString();
    if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("The WebSocket clientId query parameter is required.");
    CancellationToken ownership = sessions.RequireOwner(user, clientId);
    bool allowTransmit = !stationConfig.UserManagement.Enabled ||
        (user is not null && PermissionAuthorizationService.IsAllowed(
            user.Permissions, PermissionCatalog.Keys.AudioTransmit));
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, lifetime.ApplicationStopping, ownership);
    using CancellationTokenRegistration abort = lifetime.ApplicationStopping.Register(socket.Abort);
    if (stationConfig.IsLocked)
        await audio.AttachBrowserAsync(socket, allowTransmit, shutdown.Token);
    else
    {
        if (!allowTransmit)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "Audio transmit permission is required for this audio mode.", CancellationToken.None);
            return;
        }
        await AudioStreamBridge.RunAsync(socket, shutdown.Token);
    }
});

app.Map("/api/v1/spectrum/stream", async (HttpContext context, SpectrumStreamService spectrum,
    PermissionAuthorizationService authorization, StationSessionCoordinator sessions,
    IHostApplicationLifetime lifetime) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    AuthenticatedUser? user = await authorization.RequireAsync(context, PermissionCatalog.Keys.SpectrumView,
        cancellationToken: context.RequestAborted);
    string clientId = context.Request.Query["clientId"].ToString();
    if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("The WebSocket clientId query parameter is required.");
    CancellationToken ownership = sessions.RequireOwner(user, clientId);
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, lifetime.ApplicationStopping, ownership);
    using CancellationTokenRegistration abort = lifetime.ApplicationStopping.Register(socket.Abort);
    await spectrum.StreamAsync(socket, shutdown.Token);
});

app.MapFallbackToFile("index.html");
await app.RunAsync();
