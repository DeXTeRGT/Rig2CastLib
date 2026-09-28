using System.Net.WebSockets;
using System.Text.Json.Serialization;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.WebGui;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:8080");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<RadioWebHost>();

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    Exception? error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch
    {
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Request failed." });
}));

RouteGroupBuilder api = app.MapGroup("/api/v1");
api.MapGet("/status", (RadioWebHost host) => host.GetStatus());
api.MapGet("/models", (RadioWebHost host) => host.GetModels());
api.MapGet("/serial-ports", (RadioWebHost host) => host.GetSerialPorts());
api.MapGet("/radios", (RadioWebHost host) => host.GetRadios());
api.MapPost("/radios/connect", async (ConnectRequest request, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.ConnectOrAttachAsync(request, ct)));
api.MapPost("/radios/{radioId}/detach", async (string radioId, HttpContext context, RadioWebHost host) =>
{
    await host.DetachAsync(radioId, ApiClientIdentity.Require(context));
    return Results.NoContent();
});
api.MapDelete("/radios/{radioId}", async (string radioId, HttpContext context, RadioWebHost host) =>
{
    await host.CloseRadioAsync(radioId, ApiClientIdentity.Require(context));
    return Results.NoContent();
});
api.MapGet("/radios/{radioId}/snapshot", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.GetSnapshotAsync(radioId, ApiClientIdentity.Require(context), ct)));
api.MapPost("/radios/{radioId}/refresh", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.RefreshAsync(radioId, ApiClientIdentity.Require(context), ct)));
api.MapPut("/radios/{radioId}/frequency/{vfo}", async (string radioId, string vfo, LongValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetFrequencyAsync(radioId, clientId, Enum.Parse<VfoId>(vfo, true), body.Value, ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
});
api.MapPut("/radios/{radioId}/active-vfo", async (string radioId, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetActiveVfoAsync(radioId, clientId, Enum.Parse<VfoId>(body.Value, true), ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
});
api.MapPut("/radios/{radioId}/mode", async (string radioId, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetModeAsync(radioId, clientId, Enum.Parse<RadioMode>(body.Value, true), ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
});
api.MapPut("/radios/{radioId}/split", async (string radioId, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
{
    string clientId = ApiClientIdentity.Require(context);
    await host.SetSplitAsync(radioId, clientId, body.Value, ct);
    return Results.Ok(await host.GetSnapshotAsync(radioId, clientId, ct));
});
api.MapPut("/radios/{radioId}/ptt", async (string radioId, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.SetPttAsync(radioId, ApiClientIdentity.Require(context), body.Value, ct)));
api.MapPost("/radios/{radioId}/ptt/renew", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) =>
    Results.Ok(await host.RenewPttAsync(radioId, ApiClientIdentity.Require(context), ct)));
api.MapPost("/radios/{radioId}/controls/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadControlAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioControlId>(id, true), ct)));
api.MapPut("/radios/{radioId}/controls/{id}", async (string radioId, string id, IntValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WriteControlAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioControlId>(id, true), body.Value, ct); return Results.NoContent(); });
api.MapPost("/radios/{radioId}/switches/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadSwitchAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioSwitchId>(id, true), ct)));
api.MapPut("/radios/{radioId}/switches/{id}", async (string radioId, string id, BoolValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WriteSwitchAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioSwitchId>(id, true), body.Value, ct); return Results.NoContent(); });
api.MapPost("/radios/{radioId}/choices/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadChoiceAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioChoiceId>(id, true), ct)));
api.MapPut("/radios/{radioId}/choices/{id}", async (string radioId, string id, EnumValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WriteChoiceAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioChoiceId>(id, true), body.Value, ct); return Results.NoContent(); });
api.MapPost("/radios/{radioId}/meters/{id}/read", async (string radioId, string id, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadMeterAsync(radioId, ApiClientIdentity.Require(context), Enum.Parse<RadioMeterId>(id, true), ct)));
api.MapPost("/radios/{radioId}/passband/read", async (string radioId, HttpContext context, RadioWebHost host, CancellationToken ct) => Results.Ok(await host.ReadPassbandAsync(radioId, ApiClientIdentity.Require(context), ct)));
api.MapPut("/radios/{radioId}/passband", async (string radioId, IntValue body, HttpContext context, RadioWebHost host, CancellationToken ct) => { await host.WritePassbandAsync(radioId, ApiClientIdentity.Require(context), body.Value, ct); return Results.NoContent(); });

app.Map("/api/v1/radios/{radioId}/events", async (HttpContext context, string radioId, RadioWebHost host) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    string clientId = context.Request.Query["clientId"].ToString();
    if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("The WebSocket clientId query parameter is required.");
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await host.StreamSnapshotsAsync(radioId, clientId, socket, context.RequestAborted);
});

app.Map("/api/v1/audio/stream", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await AudioStreamBridge.RunAsync(socket, context.RequestAborted);
});

app.MapFallbackToFile("index.html");
await app.RunAsync();
