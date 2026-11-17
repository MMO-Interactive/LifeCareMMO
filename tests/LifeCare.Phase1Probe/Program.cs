using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LifeCare.Construction;
using StormChase.Networking;
using StormChase.Protocol;

// Phase 1 gate: build a room, a tornado damages it, and two players see the same lot.
// Every role runs as its own process; players use only the Gateway. The room is built with the same client controllers
// the DX12 client uses (LotBuildController through a LotSession on ConstructionClient), and the damage is observed on
// each player's own game connection as released debris bodies.
string repo = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(repo, "artifacts", "phase1-probe"));
string Exe(string project) => Path.Combine(repo, "src", "Servers", project, "bin", "Release", "net10.0", project + ".dll");
string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "mmoi-dotnet", "10.0.401", "dotnet.exe");
Directory.CreateDirectory(output);
string data = Path.Combine(output, "data-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
int FreeUdp() { using var u = new UdpClient(0, AddressFamily.InterNetwork); return ((IPEndPoint)u.Client.LocalEndPoint!).Port; }
int accountsPort = Free(), hostHttp = Free(), relayPort = Free(), climatePort = Free(), constructionPort = Free(), gatewayHttp = Free(), gatewayGame = FreeUdp();
const int Seed = 73019;
const string LotId = "lot-phase1";
// A 10x10 deed (40 m square), placed on a site the host reports clear of authored buildings.
var deedTiles = (MinX: 0, MinZ: 0, MaxX: 0, MaxZ: 0);

var checks = new List<object>(); int failed = 0;
void Check(bool ok, string name, object? detail = null)
{ checks.Add(new { name, ok, detail }); if (!ok) failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail is null ? "" : "  " + JsonSerializer.Serialize(detail))); }
var processes = new List<(string Name, Process Process, StringWriter Log)>();
void Start(string name, string dll, params string[] arguments)
{
    var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(dll)! };
    info.ArgumentList.Add(dll); foreach (var a in arguments) info.ArgumentList.Add(a);
    info.Environment["LIFECARE_MASTER_KEY"] = Environment.GetEnvironmentVariable("LIFECARE_MASTER_KEY") ?? "";
    var log = new StringWriter(); var p = Process.Start(info)!;
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine("ERR " + e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine(); processes.Add((name, p, log));
}
async Task<bool> Until(Func<Task<bool>> condition, TimeSpan timeout)
{ var w = Stopwatch.StartNew(); while (w.Elapsed < timeout) { try { if (await condition()) return true; } catch { } await Task.Delay(250); } return false; }

string gateway = $"http://127.0.0.1:{gatewayHttp}";
var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var report = new Dictionary<string, object?>();
var peers = new List<Peer>();
try
{
    Start("climate", Exe("LifeCare.Climate"), "--port", climatePort.ToString(), "--world-seed", Seed.ToString(), "--data-root", Path.Combine(data, "climate"));
    Start("accounts", Exe("StormChase.Server"), "--accounts-only", "--http-port", accountsPort.ToString(), "--data-root", Path.Combine(data, "accounts"));
    Check(await Until(async () => (await plain.GetAsync($"http://127.0.0.1:{accountsPort}/health")).IsSuccessStatusCode, TimeSpan.FromSeconds(60)), "Accounts process is up");
    string keyFile = Path.Combine(data, "accounts", "master-server.key");
    await Until(() => Task.FromResult(File.Exists(keyFile)), TimeSpan.FromSeconds(10));
    string key = File.ReadAllText(keyFile).Trim(); Environment.SetEnvironmentVariable("LIFECARE_MASTER_KEY", key);
    Start("construction", Exe("LifeCare.Construction.Service"), "--http-port", constructionPort.ToString(), "--accounts", $"http://127.0.0.1:{accountsPort}", "--data-root", Path.Combine(data, "construction"));
    Start("district", Exe("StormChase.Server"), "--http-port", hostHttp.ToString(), "--relay-port", relayPort.ToString(), "--climate", $"127.0.0.1:{climatePort}",
        "--master-url", $"http://127.0.0.1:{accountsPort}", "--master-key-env", "LIFECARE_MASTER_KEY", "--world-seed", Seed.ToString(), "--server-name", "Phase 1 district",
        "--construction", $"http://127.0.0.1:{constructionPort}", "--lot-poll-seconds", "0.5", "--storm-test-routes", "--data-root", Path.Combine(data, "district"));
    Start("gateway", Exe("LifeCare.Gateway"), "--http-port", gatewayHttp.ToString(), "--game-port", gatewayGame.ToString(), "--accounts", $"http://127.0.0.1:{accountsPort}",
        "--construction", $"http://127.0.0.1:{constructionPort}", "--data-root", Path.Combine(data, "gateway"));
    Check(await Until(async () => (await plain.GetAsync(gateway + "/construction/health")).IsSuccessStatusCode, TimeSpan.FromSeconds(60)), "Gateway and Construction service are up");

    // ---- two players, through the Gateway only
    async Task<(HttpClient Http, string Token, string Character)> Player(string name)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string user = name + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant(), password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        (await http.PostAsJsonAsync(gateway + "/api/auth/register", new { username = user, password })).EnsureSuccessStatusCode();
        var login = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/auth/login", new { username = user, password })).Content.ReadAsStringAsync())!;
        string token = login["accessToken"]!.GetValue<string>(); http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var character = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/characters", new { name = name + " Lee" })).Content.ReadAsStringAsync())!;
        return (http, token, character["id"]!.GetValue<string>());
    }
    var (ownerHttp, ownerToken, owner) = await Player("builder"); var (visitorHttp, visitorToken, visitor) = await Player("neighbour");

    // ---- the district host comes up and names a clear site (test seam), so the storm meets the lot rather than a town
    var hostInternal = new HttpClient(); hostInternal.DefaultRequestHeaders.Add("X-StormChase-Server", key);
    JsonObject? server = null;
    Check(await Until(async () => (server = (JsonNode.Parse(await ownerHttp.GetStringAsync(gateway + "/api/servers")) as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => s["ready"]?.GetValue<bool>() == true)) is not null, TimeSpan.FromMinutes(4)),
        "District host is ready behind the Gateway");
    if (server is null) throw new InvalidOperationException("District host never became ready.");
    string serverId = server["serverId"]!.GetValue<string>();
    var site = JsonNode.Parse(await hostInternal.GetStringAsync($"http://127.0.0.1:{hostHttp}/api/internal/test/clear-site?clearMeters=1500"))!;
    int siteX = site["tileX"]!.GetValue<int>(), siteZ = site["tileZ"]!.GetValue<int>();
    deedTiles = (siteX - 5, siteZ - 5, siteX + 4, siteZ + 4);
    report["site"] = new { x = site["x"]!.GetValue<float>(), z = site["z"]!.GetValue<float>() };

    // ---- a trusted service deeds the lot; the owner builds a room with the client's own build controller
    var deedClient = new HttpClient(); deedClient.DefaultRequestHeaders.Add("X-LifeCare-Service", key);
    var deeded = await deedClient.PostAsJsonAsync($"http://127.0.0.1:{constructionPort}/api/internal/lots", new LotDeed(LotId, owner, deedTiles.MinX, deedTiles.MinZ, deedTiles.MaxX, deedTiles.MaxZ), ConstructionClient.Json);
    Check(deeded.IsSuccessStatusCode, "A trusted service deeds a 10x10 lot to the builder");
    using var ownerLots = new ConstructionClient(gateway, ownerToken); using var visitorLots = new ConstructionClient(gateway, visitorToken);
    var session = new LotSession(ownerLots, owner, (await ownerLots.GetAsync(LotId))!);
    var build = new LotBuildController(session);
    float X(int tile) => tile * BuildGrid.TileMeters; float Z(int tile) => tile * BuildGrid.TileMeters;
    int r0x = deedTiles.MinX + 3, r0z = deedTiles.MinZ + 3, r1x = r0x + 2, r1z = r0z + 2;   // a 3x3 room, 12 m square
    async Task Drag(LotBuildTool tool, float x0, float z0, float x1, float z1, string name)
    {
        build.SelectTool(tool); build.SetPointer(x0, z0); build.BeginGesture(); build.SetPointer(x1, z1);
        bool valid = build.Preview.IsValid; string status = build.Preview.Status; build.CompleteGesture(); await session.SettleAsync();
        Check(valid && !session.HasPendingEdit && session.Status.EndsWith("done."), name, new { status, result = session.Status, revision = session.Lot.Revision });
    }
    await Drag(LotBuildTool.Floor, X(r0x) + 1, Z(r0z) + 1, X(r1x) + 3, Z(r1z) + 3, "Build mode: drag a 3x3 floor");
    await Drag(LotBuildTool.Wall, X(r0x), Z(r0z), X(r1x + 1), Z(r0z), "Build mode: drag the south wall");
    await Drag(LotBuildTool.Wall, X(r0x), Z(r1z + 1), X(r1x + 1), Z(r1z + 1), "Build mode: drag the north wall");
    await Drag(LotBuildTool.Wall, X(r0x), Z(r0z), X(r0x), Z(r1z + 1), "Build mode: drag the west wall");
    await Drag(LotBuildTool.Wall, X(r1x + 1), Z(r0z), X(r1x + 1), Z(r1z + 1), "Build mode: drag the east wall");
    await Drag(LotBuildTool.Roof, X(r0x) + 1, Z(r0z) + 1, X(r1x) + 3, Z(r1z) + 3, "Build mode: drag a roof over the room");
    var furnish = new LotFurnishingController(session);
    furnish.BeginCarry(new("pkg-phase1-bed", FurnishingCatalog.Find("exam-bed")!.Definition)); furnish.SetPointer(X(r0x + 1) + 2, Z(r0z + 1) + 2);
    bool ghostValid = furnish.Preview?.IsValid == true; furnish.SetDown(); await session.SettleAsync();
    Check(ghostValid && session.Lot.Furnishings.Length == 1, "A carried package is set down inside with a green ghost", new { session.Status });
    var built = session.Lot;
    Check(RoomFinder.Find(built.ToPlan(), 0).Count == 1 && built.Floors.Length == 9 && built.Walls.Length == 12 && built.Roofs.Length == 1, "The lot holds one enclosed, roofed room",
        new { built.Revision, floors = built.Floors.Length, walls = built.Walls.Length, roofs = built.Roofs.Length });

    // ---- the second player sees the same lot, and cannot change it
    var seenByVisitor = await visitorLots.GetAsync(LotId);
    string Canonical(LotDocument d) => JsonSerializer.Serialize(d, ConstructionClient.Json);
    Check(seenByVisitor is not null && Canonical(seenByVisitor) == Canonical(built), "Both players read the identical lot document (same revision, same pieces)", new { owner = built.Revision, visitor = seenByVisitor?.Revision });
    var visitorSession = new LotSession(visitorLots, visitor, seenByVisitor!); var visitorBuild = new LotBuildController(visitorSession);
    visitorBuild.SetPointer(X(deedTiles.MinX) + 1, Z(deedTiles.MinZ) + 1); visitorBuild.BeginGesture();
    Check(!visitorBuild.IsDragging && visitorBuild.Status.Contains("owner"), "The neighbour's build mode refuses someone else's lot");
    var forced = await visitorLots.BuildAsync(LotId, new(visitor, new(BuildEditKind.RemoveRoof, 0, RoofId: built.Roofs[0].RoofId)));
    Check(!forced.Accepted && forced.ErrorCode == "not_owner", "The authority refuses the neighbour's edit too", new { forced.ErrorCode });

    // ---- the district host picks the lot up into its physics
    bool hostHasLot = await Until(async () => JsonNode.Parse(await hostInternal.GetStringAsync($"http://127.0.0.1:{hostHttp}/api/internal/lots"))?[LotId]?.GetValue<long>() == built.Revision, TimeSpan.FromSeconds(30));
    Check(hostHasLot, "The district host has the lot's latest revision in its PhysX world", new { revision = built.Revision });

    // ---- both players join the district through the Gateway's game port
    var info = JsonNode.Parse(await ownerHttp.GetStringAsync(gateway + "/s/" + serverId + "/api/server"))!;
    foreach (var (http, character) in new[] { (ownerHttp, owner), (visitorHttp, visitor) })
    {
        var ticket = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/matches/join", new { characterId = character, serverId })).Content.ReadAsStringAsync())!;
        peers.Add(new Peer(info, ticket, "127.0.0.1", gatewayGame));
    }
    Check(await Until(() => Task.FromResult(peers.All(p => p.Accepted)), TimeSpan.FromSeconds(30)), "Both players are admitted to the district through the Gateway",
        peers.Select(p => p.Rejected).Where(r => r is not null).ToArray() is { Length: > 0 } why ? new { why } : null);
    await Task.Delay(2000);
    string prefix = "lot/" + LotId + "/";
    Check(peers.All(p => p.LotPieces(prefix).Count == 0), "Before the storm no lot piece is debris");

    // ---- a tornado crosses the lot
    var storm = await hostInternal.PostAsJsonAsync($"http://127.0.0.1:{hostHttp}/api/internal/test/lot-tornado", new { lotId = LotId, seconds = 12 });
    var held = storm.IsSuccessStatusCode ? JsonNode.Parse(await storm.Content.ReadAsStringAsync()) : null; report["storm"] = held;
    Check(held?["tornado"]?.GetValue<bool>() == true && held["windMphAtLot"]!.GetValue<float>() >= 110, "A test tornado is held over the lot on the district host", held);
    var pieces = LotGeometry.Build(built, 0).ToDictionary(p => p.PieceId);
    bool damaged = await Until(() => Task.FromResult(peers.All(p => p.LotPieces(prefix).Count >= 3)), TimeSpan.FromSeconds(45));
    await Task.Delay(3000);   // let late releases reach both players
    var seenA = peers[0].LotPieces(prefix); var seenB = peers[1].LotPieces(prefix);
    report["releasedPieces"] = seenA.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    Check(damaged, "The tornado tears pieces off the built room", new { builder = seenA.Count, neighbour = seenB.Count });
    Check(seenA.SetEquals(seenB) && seenA.Count > 0, "Both players see exactly the same damaged pieces", new { onlyBuilder = seenA.Except(seenB).Take(5), onlyNeighbour = seenB.Except(seenA).Take(5) });
    Check(seenA.All(pieces.ContainsKey), "Every released piece is a real piece of the built lot revision");
    Check(seenA.Any(id => pieces.TryGetValue(id, out var p) && p.Kind == LotPieceKind.Roof), "The roof goes");
    Check(!seenA.Any(id => pieces.TryGetValue(id, out var p) && p.Kind == LotPieceKind.Floor && p.PieceId.Contains("/floor/0/")), "Ground floors stay on their foundation");
    var poseA = peers[0].Pose(seenA.FirstOrDefault() ?? ""); var poseB = peers[1].Pose(seenA.FirstOrDefault() ?? "");
    Check(poseA is not null && poseB is not null && poseA.BodyId == poseB.BodyId, "A torn-off piece has the same server body on both players", new { a = poseA?.BodyId, b = poseB?.BodyId });
}
catch (Exception error) { Check(false, "Probe ran to completion", error.Message); }
finally
{
    foreach (var peer in peers) peer.Dispose();
    foreach (var (name, process, log) in processes.AsEnumerable().Reverse())
    {
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } } catch { }
        string text; lock (log) text = log.ToString();
        File.WriteAllText(Path.Combine(output, name + ".log"), text);
    }
    report["checks"] = checks; report["passed"] = checks.Count - failed; report["failed"] = failed;
    File.WriteAllText(Path.Combine(output, "phase1-gate.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed; report in {output}");
}
return failed == 0 ? 0 : 1;

/// <summary>One player's game connection, pumped on its own thread: sends input and records every debris body it is told about.</summary>
sealed class Peer : IDisposable
{
    private readonly StormTransportHost client;
    private readonly uint connection;
    private readonly Thread pump;
    private readonly ConcurrentDictionary<int, DebrisBodyState> bodies = new();
    private volatile bool stop;
    public bool Accepted { get; private set; }
    public string? Rejected { get; private set; }

    public Peer(JsonNode info, JsonNode ticket, string host, int port)
    {
        client = StormTransportHost.CreateClient(new(info["transportAuthority"]!.GetValue<string>(), ExpectedPeerIdentity: info["transportIdentity"]!.GetValue<string>()));
        connection = client.Connect(host, (ushort)port);
        string fingerprint = info["worldContentFingerprint"]!.GetValue<string>(), admission = ticket["ticket"]!.GetValue<string>();
        pump = new(() =>
        {
            bool connected = false; long n = 0; var clock = Stopwatch.StartNew(); var nextInput = TimeSpan.Zero;
            while (!stop)
            {
                foreach (var e in client.Poll())
                {
                    if (e.Kind == TransportEventKind.Connected && !connected) { connected = true; client.Send(connection, MessageKind.JoinRequest, new JoinRequest(admission, fingerprint)); }
                    if (e.Message is not { } m) continue;
                    switch (m.Kind)
                    {
                        case MessageKind.JoinAccepted: Accepted = true; break;
                        case MessageKind.JoinRejected: Rejected = m.Get<JoinRejected>().Reason; break;
                        case MessageKind.DebrisEvent: Record(m.Get<DebrisEvent>().Body); break;
                        case MessageKind.DebrisSnapshot: foreach (var body in m.Get<DebrisSnapshot>().Bodies) Record(body); break;
                    }
                }
                if (Accepted && clock.Elapsed >= nextInput) { client.Send(connection, MessageKind.ClientInput, new ClientInput(++n, default, 0, 0, PlayerMovementMode.OnFoot, 0, false), false); nextInput += TimeSpan.FromMilliseconds(50); }
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "phase1 peer" };
        pump.Start();
    }

    private void Record(DebrisBodyState body)
    {
        // Motion packets omit the name; keep the name the release or baseline gave this body.
        if (body.StablePartId.Length == 0 && bodies.TryGetValue(body.BodyId, out var known)) body = body with { StablePartId = known.StablePartId };
        if (body.StablePartId.Length > 0) bodies[body.BodyId] = body;
    }

    public HashSet<string> LotPieces(string prefix) => bodies.Values.Select(b => b.StablePartId).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    public DebrisBodyState? Pose(string partId) => bodies.Values.FirstOrDefault(b => b.StablePartId == partId);
    public void Dispose() { stop = true; pump.Join(2000); client.Dispose(); }
}
