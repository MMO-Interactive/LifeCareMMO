using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LifeCare.Construction;
using LifeCare.Construction.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

// LifeCare Construction service: the authority for every lot's floors, walls, roofs and furniture.
// Clients reach it only through the Gateway at /construction/…; district hosts and other services use /api/internal with the service key.
string Read(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
try
{
    int port = int.Parse(Read("--http-port", "5400"), CultureInfo.InvariantCulture);
    var listen = IPAddress.Parse(Read("--listen", "127.0.0.1"));
    string dataRoot = Path.GetFullPath(Read("--data-root", Path.Combine(AppContext.BaseDirectory, "construction-data")));
    string accounts = Read("--accounts", "http://127.0.0.1:5080").TrimEnd('/');
    string keyEnvironment = Read("--accounts-key-env", "LIFECARE_MASTER_KEY");
    // The district host that knows where characters stand; door interactions are checked against it (Life Beyond's 1-tile reach).
    string? district = args.Contains("--district") ? Read("--district", "").TrimEnd('/') : null;
    string serviceKey = Environment.GetEnvironmentVariable(keyEnvironment) is { Length: 64 } k ? k : throw new ArgumentException($"Set {keyEnvironment} to the accounts service key.");
    if (!IPAddress.IsLoopback(listen)) throw new ArgumentException("Phase 1 runs the Construction service on loopback, behind the Gateway.");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning).AddFilter("System.Net.Http", LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 64 * 1024; k.Listen(listen, port); });
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton(new LotStore(dataRoot));
    builder.Services.AddHttpClient("accounts", c => { c.BaseAddress = new(accounts); c.Timeout = TimeSpan.FromSeconds(10); c.DefaultRequestHeaders.Add("X-StormChase-Server", serviceKey); });
    if (district is not null)
        builder.Services.AddHttpClient("district", c => { c.BaseAddress = new(district); c.Timeout = TimeSpan.FromSeconds(5); c.DefaultRequestHeaders.Add("X-StormChase-Server", serviceKey); });
    builder.Services.AddAuthentication("Accounts").AddScheme<AuthenticationSchemeOptions, AccountsBearerHandler>("Accounts", _ => { });
    builder.Services.AddAuthorization();
    var app = builder.Build();
    app.UseAuthentication(); app.UseAuthorization();
    app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (KeyNotFoundException) { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { error = "There is no such lot." }); }
        catch (ArgumentException e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    });

    app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

    // ---- internal: deeds are issued by trusted services (housing purchases later), never by clients
    var internalApi = app.MapGroup("/api/internal").AddEndpointFilter(async (ctx, next) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.HttpContext.Request.Headers["X-LifeCare-Service"].ToString()), Encoding.UTF8.GetBytes(serviceKey))
            ? await next(ctx) : Results.Unauthorized());
    internalApi.MapPost("/lots", (LotDeed deed, LotStore store) => Results.Ok(store.Create(deed)));
    internalApi.MapGet("/lots/{lotId}", (string lotId, LotStore store) => store.Get(lotId) is { } lot ? Results.Ok(lot) : Results.NotFound());
    internalApi.MapGet("/lots", (LotStore store) => Results.Ok(store.List()));

    // ---- players (through the Gateway)
    var lots = app.MapGroup("/api/lots").RequireAuthorization();
    lots.MapGet("", (LotStore store) => Results.Ok(store.List()));
    // Play-testing only (--dev-starter-lots): a player claims a 10x10 lot where they stand. Real deeds come from housing purchases.
    if (args.Contains("--dev-starter-lots"))
    {
        Console.WriteLine("Dev starter lots are ON: any signed-in player can claim one 10x10 lot. Never enable this on a public server.");
        lots.MapPost("/claim-starter", async (StarterLotRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        {
            if (!await Owns(user, request.CharacterId, http)) return Results.Forbid();
            if (store.List().FirstOrDefault(l => l.Lot.OwnerCharacterId == request.CharacterId) is { } existing) return Results.Ok(store.Get(existing.Lot.LotId));
            var deed = new LotDeed("starter-" + new string(request.CharacterId.Where(char.IsAsciiLetterOrDigit).Take(24).ToArray()), request.CharacterId,
                request.TileX - 5, request.TileZ + 1, request.TileX + 4, request.TileZ + 10);   // just ahead (+Z) of where the player stands
            if (store.List().Any(l => l.Lot.MinimumTileX <= deed.MaximumTileX && deed.MinimumTileX <= l.Lot.MaximumTileX && l.Lot.MinimumTileZ <= deed.MaximumTileZ && deed.MinimumTileZ <= l.Lot.MaximumTileZ))
                return Results.Conflict(new { error = "Another lot is already here. Walk somewhere open and try again." });
            return Results.Ok(store.Create(deed));
        });
    }
    lots.MapGet("/{lotId}", (string lotId, LotStore store) => store.Get(lotId) is { } lot ? Results.Ok(lot) : Results.NotFound());
    lots.MapPost("/{lotId}/build", async (string lotId, BuildRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        await Owns(user, request.CharacterId, http) ? Reply(store.Change(lotId, request.ExpectedRevision, plan => BuildingEditor.Apply(plan, request.CharacterId, request.Edit))) : Results.Forbid());
    lots.MapPost("/{lotId}/furnishings/preview", async (string lotId, FurnishingRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        await Owns(user, request.CharacterId, http) && store.Get(lotId) is { } lot ? Results.Ok(FurnishingEditor.Preview(lot.ToPlan(), request.CharacterId, request.Piece!)) : Results.Forbid());
    lots.MapPost("/{lotId}/furnishings/place", async (string lotId, FurnishingRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        await Owns(user, request.CharacterId, http) ? Reply(store.Change(lotId, request.ExpectedRevision, plan => FurnishingEditor.Place(plan, request.CharacterId, request.Piece!))) : Results.Forbid());
    lots.MapPost("/{lotId}/furnishings/move", async (string lotId, FurnishingRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        await Owns(user, request.CharacterId, http) ? Reply(store.Change(lotId, request.ExpectedRevision, plan => FurnishingEditor.Move(plan, request.CharacterId, request.Piece!))) : Results.Forbid());
    lots.MapPost("/{lotId}/furnishings/pickup", async (string lotId, FurnishingRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
        await Owns(user, request.CharacterId, http) ? Reply(store.Change(lotId, request.ExpectedRevision, plan => FurnishingEditor.PickUp(plan, request.CharacterId, request.InstanceId ?? ""))) : Results.Forbid());

    // Doors are an interaction, not a construction edit: any signed-in character near the door may set it open or closed.
    var doorReplies = new System.Collections.Concurrent.ConcurrentDictionary<string, ChangeResult>(StringComparer.Ordinal);
    lots.MapPost("/{lotId}/doors", async (string lotId, DoorRequest request, ClaimsPrincipal user, LotStore store, IHttpClientFactory http) =>
    {
        if (!await Owns(user, request.CharacterId, http)) return Results.Forbid();
        string? replayKey = request.OperationId is { Length: > 0 and <= 64 } op ? $"{request.CharacterId}/{op}" : null;
        if (replayKey is not null && doorReplies.TryGetValue(replayKey, out var replayed)) return Reply(replayed);
        if (store.Get(lotId) is not { } lot) return Results.NotFound();
        if (LotDoors.All(lot.ToPlan()).FirstOrDefault(d => d.DoorId == request.DoorId) is not { } door)
            return Reply(new ChangeResult(false, lot, "door_not_found", "There is no door there."));
        if (district is not null)
        {
            using var reply = await http.CreateClient("district").GetAsync($"/api/internal/characters/{Uri.EscapeDataString(request.CharacterId)}/position?lotId={Uri.EscapeDataString(lotId)}");
            var at = reply.IsSuccessStatusCode ? await reply.Content.ReadFromJsonAsync<DoorReach>() : null;
            if (at?.LotBase is not { } lotBase || !LotDoors.InReach(door, new(at.X, at.Y, at.Z), lotBase))
                return Reply(new ChangeResult(false, lot, "door_out_of_range", $"Walk within {LotDoors.InteractionRangeMeters:0} m of the door to use it."));
        }
        var result = store.Change(lotId, null, plan => LotDoors.SetOpen(plan, request.CharacterId, request.DoorId, request.Open));
        if (replayKey is not null) { if (doorReplies.Count > 4096) doorReplies.Clear(); doorReplies[replayKey] = result; }
        return Reply(result);
    });

    app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"LifeCare Construction ready on {listen}:{port}; lots in {dataRoot}"));
    await app.RunAsync();
    return 0;

    static IResult Reply(ChangeResult result) => result.Accepted ? Results.Ok(result) : Results.Json(result, statusCode: result.ErrorCode == "stale_revision" ? 409 : 422);

    // A character acts only for the account that owns it; accounts is asked each time.
    static async Task<bool> Owns(ClaimsPrincipal user, string characterId, IHttpClientFactory http)
    {
        string? account = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (account is null || string.IsNullOrWhiteSpace(characterId) || characterId.Length > 64) return false;
        var reply = await http.CreateClient("accounts").GetStringAsync($"/api/internal/ownership?accountId={Uri.EscapeDataString(account)}&characterId={Uri.EscapeDataString(characterId)}");
        return reply.Trim() == "true";
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("LifeCare Construction stopped: " + error.Message);
    return 1;
}

internal sealed record DoorReach(float X, float Y, float Z, float? LotBase);

/// <summary>Accepts the player's accounts bearer token by asking the accounts service who it belongs to.</summary>
internal sealed class AccountsBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHttpClientFactory http)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var auth) || auth.Scheme != "Bearer" || auth.Parameter is not { Length: <= 8192 }) return AuthenticateResult.NoResult();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/introspect"); request.Headers.Authorization = auth;
        using var response = await http.CreateClient("accounts").SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode) return AuthenticateResult.Fail("Accounts rejected the token.");
        var account = await response.Content.ReadFromJsonAsync<AccountReply>(Context.RequestAborted);
        if (account is null) return AuthenticateResult.Fail("No account.");
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, account.AccountId), new(ClaimTypes.Name, account.Username)], Scheme.Name);
        return AuthenticateResult.Success(new(new(identity), Scheme.Name));
    }
    private sealed record AccountReply(string AccountId, string Username);
}
