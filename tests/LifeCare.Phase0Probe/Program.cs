using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StormChase.Networking;
using StormChase.Protocol;

// Phase 0 gate: the login game runs entirely through the Gateway, with every role in its own process on one machine.
// Starts climate, accounts, one district host (relay mode, remote climate) and the Gateway from their build output,
// then plays the client's part using only the Gateway's two public ports.
string repo = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(repo, "artifacts", "phase0-probe"));
string Exe(string project, string folder) => Path.Combine(repo, "src", "Servers", project, "bin", "Release", "net10.0", folder + ".dll");
string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "mmoi-dotnet", "10.0.401", "dotnet.exe");
Directory.CreateDirectory(output);
string data = Path.Combine(output, "data-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
int FreeUdp() { using var u = new UdpClient(0, AddressFamily.InterNetwork); return ((IPEndPoint)u.Client.LocalEndPoint!).Port; }
int accountsPort = Free(), hostHttp = Free(), relayPort = Free(), climatePort = Free(), gatewayHttp = Free(), gatewayGame = FreeUdp();
const int Seed = 73019;

var checks = new List<object>(); int failed = 0;
void Check(bool ok, string name, object? detail = null)
{ checks.Add(new { name, ok, detail }); if (!ok) failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail is null ? "" : "  " + JsonSerializer.Serialize(detail))); }

var processes = new List<(string Name, Process Process, StringWriter Log)>();
Process Start(string name, string dll, params string[] arguments)
{
    var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(dll)! };
    info.ArgumentList.Add(dll); foreach (var a in arguments) info.ArgumentList.Add(a);
    info.Environment["LIFECARE_MASTER_KEY"] = Environment.GetEnvironmentVariable("LIFECARE_MASTER_KEY") ?? "";
    var log = new StringWriter(); var p = Process.Start(info)!;
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine("ERR " + e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine(); processes.Add((name, p, log)); return p;
}
async Task<bool> Until(Func<Task<bool>> condition, TimeSpan timeout)
{ var w = Stopwatch.StartNew(); while (w.Elapsed < timeout) { try { if (await condition()) return true; } catch { } await Task.Delay(250); } return false; }

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
string gateway = $"http://127.0.0.1:{gatewayHttp}";
JsonNode? playingHealth = null;
var report = new Dictionary<string, object?> { ["ports"] = new { accountsPort, hostHttp, relayPort, climatePort, gatewayHttp, gatewayGame } };
try
{
    // ---- roles, each its own process
    Start("climate", Exe("LifeCare.Climate", "LifeCare.Climate"), "--port", climatePort.ToString(), "--world-seed", Seed.ToString(), "--data-root", Path.Combine(data, "climate"));
    Start("accounts", Exe("StormChase.Server", "StormChase.Server"), "--accounts-only", "--http-port", accountsPort.ToString(), "--data-root", Path.Combine(data, "accounts"));
    Check(await Until(async () => (await http.GetAsync($"http://127.0.0.1:{accountsPort}/health")).IsSuccessStatusCode, TimeSpan.FromSeconds(60)), "Accounts process is up");
    string keyFile = Path.Combine(data, "accounts", "master-server.key");
    Check(await Until(() => Task.FromResult(File.Exists(keyFile)), TimeSpan.FromSeconds(10)), "Accounts created its server registration key");
    Environment.SetEnvironmentVariable("LIFECARE_MASTER_KEY", File.ReadAllText(keyFile).Trim());
    var clock = Stopwatch.StartNew();
    Start("district", Exe("StormChase.Server", "StormChase.Server"), "--http-port", hostHttp.ToString(), "--relay-port", relayPort.ToString(), "--climate", $"127.0.0.1:{climatePort}",
        "--master-url", $"http://127.0.0.1:{accountsPort}", "--master-key-env", "LIFECARE_MASTER_KEY", "--world-seed", Seed.ToString(), "--server-name", "Phase 0 district", "--data-root", Path.Combine(data, "district"));
    Start("gateway", Exe("LifeCare.Gateway", "LifeCare.Gateway"), "--http-port", gatewayHttp.ToString(), "--game-port", gatewayGame.ToString(), "--accounts", $"http://127.0.0.1:{accountsPort}", "--data-root", Path.Combine(data, "gateway"));
    Check(await Until(async () => (await http.GetAsync(gateway + "/gateway/health")).IsSuccessStatusCode, TimeSpan.FromSeconds(60)), "Gateway process is up");

    // ---- accounts through the Gateway only
    string user = "probe_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    var register = await http.PostAsJsonAsync(gateway + "/api/auth/register", new { username = user, password });
    Check(register.StatusCode == HttpStatusCode.Created, "Register through the Gateway", (int)register.StatusCode);
    var login = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/auth/login", new { username = user, password })).Content.ReadAsStringAsync())!;
    string token = login["accessToken"]!.GetValue<string>();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var character = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/characters", new { name = "Probe Medic" })).Content.ReadAsStringAsync())!;
    string characterId = character["id"]!.GetValue<string>();
    Check(characterId.Length > 0, "Create a character through the Gateway");
    Check((await http.PostAsJsonAsync(gateway + "/api/internal/servers/stop", new { })).StatusCode == HttpStatusCode.NotFound, "Internal routes are not reachable through the Gateway");

    // ---- the district host registers, visible only with Gateway addresses
    JsonObject? server = null;
    bool ready = await Until(async () =>
    {
        var list = JsonNode.Parse(await http.GetStringAsync(gateway + "/api/servers")) as JsonArray;
        server = list?.OfType<JsonObject>().FirstOrDefault(s => s["ready"]?.GetValue<bool>() == true);
        return server is not null;
    }, TimeSpan.FromMinutes(4));
    report["districtReadySeconds"] = clock.Elapsed.TotalSeconds;
    Check(ready, "District host is ready and listed through the Gateway", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
    if (!ready) throw new InvalidOperationException("District host never became ready.");
    string serverId = server!["serverId"]!.GetValue<string>();
    Check(server["host"]!.GetValue<string>() == "127.0.0.1" && server["gamePort"]!.GetValue<int>() == gatewayGame && server["apiAddress"]!.GetValue<string>() == gateway + "/s/" + serverId && server["relayPort"]!.GetValue<int>() == 0,
        "Listed server shows only Gateway addresses", new { host = server["host"]?.ToString(), gamePort = server["gamePort"]?.ToString(), apiAddress = server["apiAddress"]?.ToString() });
    var real = JsonNode.Parse(await new HttpClient().GetStringAsync($"http://127.0.0.1:{hostHttp}/api/server"))!;
    Check(real["transportIdentity"]!.GetValue<string>() == "" && real["relayPort"]!.GetValue<int>() == relayPort, "District host has no public game port; it serves players only over the relay link");
    var viaGateway = JsonNode.Parse(await http.GetStringAsync(gateway + "/s/" + serverId + "/api/server"))!;
    Check(viaGateway["serverId"]!.GetValue<string>() == serverId && viaGateway["transportIdentity"]!.GetValue<string>().StartsWith("sc/"), "Server API reached at /s/{id} through the Gateway, with the Gateway's transport identity");

    // ---- weather comes from the separate climate process
    double c1 = real["campaignSeconds"]!.GetValue<double>(); await Task.Delay(1500);
    double c2 = JsonNode.Parse(await new HttpClient().GetStringAsync($"http://127.0.0.1:{hostHttp}/api/server"))!["campaignSeconds"]!.GetValue<double>();
    string climateLog; lock (processes[0].Log) climateLog = processes[0].Log.ToString();
    string districtLog; lock (processes[2].Log) districtLog = processes[2].Log.ToString();
    Check(c2 > c1 && districtLog.Contains("Weather from climate process"), "District weather clock advances from the climate process", new { before = c1, after = c2 });

    // ---- play through the Gateway's game port
    var ticket = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/matches/join", new { characterId, serverId })).Content.ReadAsStringAsync())!;
    Check(ticket["host"]!.GetValue<string>() == "127.0.0.1" && ticket["gamePort"]!.GetValue<int>() == gatewayGame, "Admission ticket points at the Gateway's game port");
    var viaGatewayPlay = await Play(viaGateway, ticket, "127.0.0.1", gatewayGame, () => playingHealth = JsonNode.Parse(http.GetStringAsync(gateway + "/gateway/health").Result));
    report["roundTripThroughGateway"] = viaGatewayPlay.Latency;
    Check(viaGatewayPlay.Accepted, "Player admitted to the district host through the Gateway", viaGatewayPlay.Rejected is null ? null : new { viaGatewayPlay.Rejected });
    Check(viaGatewayPlay.Snapshots >= 40 && viaGatewayPlay.Simulated > 0, "World snapshots stream back through the Gateway", new { viaGatewayPlay.Snapshots, simulatedSeconds = Math.Round(viaGatewayPlay.Simulated, 2) });

    // ---- baseline: the same play straight to a district host's own game port, no Gateway in between
    int directHttp = Free(), directGame = FreeUdp();
    Start("district-direct", Exe("StormChase.Server", "StormChase.Server"), "--http-port", directHttp.ToString(), "--game-port", directGame.ToString(), "--climate", $"127.0.0.1:{climatePort}",
        "--master-url", $"http://127.0.0.1:{accountsPort}", "--master-key-env", "LIFECARE_MASTER_KEY", "--world-seed", Seed.ToString(), "--server-name", "Phase 0 direct baseline", "--data-root", Path.Combine(data, "district-direct"));
    var accounts = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; accounts.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
    JsonNode? direct = null;
    await Until(async () => { direct = JsonNode.Parse(await accounts.GetStringAsync($"http://127.0.0.1:{directHttp}/api/server")); return direct?["ready"]?.GetValue<bool>() == true; }, TimeSpan.FromMinutes(4));
    await Until(async () => (JsonNode.Parse(await accounts.GetStringAsync($"http://127.0.0.1:{accountsPort}/api/servers")) as JsonArray)!.Any(x => x?["serverId"]?.GetValue<string>() == direct!["serverId"]!.GetValue<string>() && x!["ready"]!.GetValue<bool>()), TimeSpan.FromSeconds(30));
    var second = JsonNode.Parse(await (await accounts.PostAsJsonAsync($"http://127.0.0.1:{accountsPort}/api/characters", new { name = "Probe Baseline" })).Content.ReadAsStringAsync())!;
    var directTicket = JsonNode.Parse(await (await accounts.PostAsJsonAsync($"http://127.0.0.1:{accountsPort}/api/matches/join", new { characterId = second["id"]!.GetValue<string>(), serverId = direct!["serverId"]!.GetValue<string>() })).Content.ReadAsStringAsync())!;
    var directPlay = await Play(direct, directTicket, "127.0.0.1", directGame);
    report["roundTripDirect"] = directPlay.Latency;
    Check(directPlay.Accepted && directPlay.Latency is not null, "Baseline player admitted straight to a district host", directPlay.Rejected is null ? null : new { directPlay.Rejected });
    if (viaGatewayPlay.Latency is { } g && directPlay.Latency is { } d)
    {
        var added = new { medianMs = Math.Round(g.MedianMs - d.MedianMs, 2), p95Ms = Math.Round(g.P95Ms - d.P95Ms, 2) };
        report["gatewayAddedLatency"] = added;
        Check(added.medianMs < 5, "The Gateway hop adds under 5 ms to the median round trip", new { gateway = g, direct = d, added });
    }
    Check(playingHealth?["players"]?.GetValue<int>() == 1 && playingHealth?["links"]?.GetValue<int>() == 1, "While playing, the Gateway fronts one player over one relay link", playingHealth);
    JsonNode? health = null;
    Check(await Until(async () => (health = JsonNode.Parse(await http.GetStringAsync(gateway + "/gateway/health")))!["players"]!.GetValue<int>() == 0, TimeSpan.FromSeconds(5)) && health!["links"]!.GetValue<int>() == 1,
        "After the player leaves, the Gateway drops the session and keeps the host link", health);
}
catch (Exception error) { Check(false, "Probe ran to completion", error.Message); }
finally
{
    foreach (var (name, process, log) in processes.AsEnumerable().Reverse())
    {
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } } catch { }
        string text; lock (log) text = log.ToString();
        File.WriteAllText(Path.Combine(output, name + ".log"), text);
    }
    report["checks"] = checks; report["passed"] = checks.Count - failed; report["failed"] = failed;
    File.WriteAllText(Path.Combine(output, "phase0-gate.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed; report in {output}");
}
return failed == 0 ? 0 : 1;

async Task<PlayResult> Play(JsonNode info, JsonNode ticket, string host, int port, Action? beforeLeaving = null)
{
    await Task.Yield();
    using var client = StormTransportHost.CreateClient(new(info["transportAuthority"]!.GetValue<string>(), ExpectedPeerIdentity: info["transportIdentity"]!.GetValue<string>()));
    uint connection = client.Connect(host, (ushort)port);
    bool accepted = false, connected = false; string? rejected = null; int snapshots = 0; double firstSim = -1, lastSim = 0;
    var rtts = new List<double>(); var sentAt = new HashSet<long>();
    void Pump()
    {
        foreach (var e in client.Poll())
        {
            if (e.Kind == TransportEventKind.Connected && !connected) { connected = true; client.Send(connection, MessageKind.JoinRequest, new JoinRequest(ticket["ticket"]!.GetValue<string>(), info["worldContentFingerprint"]!.GetValue<string>())); }
            if (e.Message is not { } m) continue;
            if (m.Kind == MessageKind.JoinAccepted) accepted = true;
            else if (m.Kind == MessageKind.JoinRejected) rejected = m.Get<JoinRejected>().Reason;
            else if (m.Kind == MessageKind.WorldSnapshot) { snapshots++; lastSim = m.Get<WorldSnapshot>().SimulationSeconds; if (firstSim < 0) firstSim = lastSim; }
            else if (m.Kind == MessageKind.Pong && sentAt.Remove(m.Get<Ping>().Timestamp)) rtts.Add(Stopwatch.GetElapsedTime(m.Get<Ping>().Timestamp).TotalMilliseconds);
        }
    }
    var wait = Stopwatch.StartNew();
    while (!accepted && rejected is null && wait.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(1); }
    if (!accepted) return new(false, rejected, 0, 0, null);
    long n = 0; var play = Stopwatch.StartNew(); var nextInput = TimeSpan.Zero; var nextPing = TimeSpan.FromMilliseconds(13);
    var jitter = new Random(7);
    while (play.Elapsed < TimeSpan.FromSeconds(8))
    {
        if (play.Elapsed >= nextInput) { client.Send(connection, MessageKind.ClientInput, new ClientInput(++n, default, 0, 0, PlayerMovementMode.OnFoot, 0, false, Forward: 1), false); nextInput += TimeSpan.FromMilliseconds(50); }
        // Pings at uneven times sample every phase of the host's 50 ms tick.
        if (play.Elapsed >= nextPing) { long stamp = Stopwatch.GetTimestamp(); sentAt.Add(stamp); client.Send(connection, MessageKind.Ping, new Ping(stamp), false); nextPing += TimeSpan.FromMilliseconds(90 + jitter.Next(0, 47)); }
        Pump(); Thread.Sleep(1);
    }
    beforeLeaving?.Invoke();
    rtts.Sort();
    var latency = rtts.Count == 0 ? null : new Latency(rtts.Count, Math.Round(rtts[rtts.Count / 2], 2), Math.Round(rtts[(int)((rtts.Count - 1) * .95)], 2), Math.Round(rtts[^1], 2));
    return new(true, null, snapshots, firstSim < 0 ? 0 : lastSim - firstSim, latency);
}

record Latency(int Samples, double MedianMs, double P95Ms, double MaxMs);
record PlayResult(bool Accepted, string? Rejected, int Snapshots, double Simulated, Latency? Latency);
