using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Construction service probe: two players, one lot, everything through the Gateway; then a restart to prove persistence.
string repo = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string output = Path.Combine(repo, "artifacts", "construction-probe"); Directory.CreateDirectory(output);
string data = Path.Combine(output, "data-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "mmoi-dotnet", "10.0.401", "dotnet.exe");
string Dll(string p) => Path.Combine(repo, "src", "Servers", p, "bin", "Release", "net10.0", p + ".dll");
int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
int accountsPort = Free(), constructionPort = Free(), gatewayPort = Free(), gamePort;
using (var u = new UdpClient(0)) gamePort = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
string gateway = $"http://127.0.0.1:{gatewayPort}";
int failed = 0, passed = 0; var checks = new List<object>();
void Check(bool ok, string name, object? detail = null) { checks.Add(new { name, ok, detail }); if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail is null ? "" : "  " + JsonSerializer.Serialize(detail))); }
var processes = new List<Process>();
Process Start(string dll, params string[] a)
{
    var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(dll)! };
    info.ArgumentList.Add(dll); foreach (var x in a) info.ArgumentList.Add(x);
    info.Environment["LIFECARE_MASTER_KEY"] = Environment.GetEnvironmentVariable("LIFECARE_MASTER_KEY") ?? "";
    var p = Process.Start(info)!; p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine(); processes.Add(p); return p;
}
async Task<bool> Until(Func<Task<bool>> c, int seconds) { var w = Stopwatch.StartNew(); while (w.Elapsed.TotalSeconds < seconds) { try { if (await c()) return true; } catch { } await Task.Delay(200); } return false; }
var plain = new HttpClient();
async Task<(HttpClient Http, string Character)> Player(string name)
{
    var http = new HttpClient();
    string user = name + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant(), password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    (await http.PostAsJsonAsync(gateway + "/api/auth/register", new { username = user, password })).EnsureSuccessStatusCode();
    var login = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/auth/login", new { username = user, password })).Content.ReadAsStringAsync())!;
    http.DefaultRequestHeaders.Authorization = new("Bearer", login["accessToken"]!.GetValue<string>());
    var character = JsonNode.Parse(await (await http.PostAsJsonAsync(gateway + "/api/characters", new { name = name + " Lee" })).Content.ReadAsStringAsync())!;
    return (http, character["id"]!.GetValue<string>());
}
async Task<(HttpStatusCode Status, JsonNode? Body)> Post(HttpClient http, string path, object body)
{ var r = await http.PostAsJsonAsync(gateway + "/construction" + path, body); var t = await r.Content.ReadAsStringAsync(); return (r.StatusCode, t.Length > 0 ? JsonNode.Parse(t) : null); }

try
{
    Start(Dll("StormChase.Server"), "--accounts-only", "--http-port", accountsPort.ToString(), "--data-root", Path.Combine(data, "accounts"));
    Check(await Until(async () => (await plain.GetAsync($"http://127.0.0.1:{accountsPort}/health")).IsSuccessStatusCode, 60), "Accounts up");
    string key = File.ReadAllText(Path.Combine(data, "accounts", "master-server.key")).Trim();
    Environment.SetEnvironmentVariable("LIFECARE_MASTER_KEY", key);
    string[] constructionArgs = ["--http-port", constructionPort.ToString(), "--accounts", $"http://127.0.0.1:{accountsPort}", "--data-root", Path.Combine(data, "construction")];
    var construction = Start(Dll("LifeCare.Construction.Service"), constructionArgs);
    Start(Dll("LifeCare.Gateway"), "--http-port", gatewayPort.ToString(), "--game-port", gamePort.ToString(), "--accounts", $"http://127.0.0.1:{accountsPort}",
        "--construction", $"http://127.0.0.1:{constructionPort}", "--data-root", Path.Combine(data, "gateway"));
    Check(await Until(async () => (await plain.GetAsync(gateway + "/construction/health")).IsSuccessStatusCode, 60), "Construction service reachable through the Gateway");

    var (ownerHttp, owner) = await Player("owner"); var (visitorHttp, visitor) = await Player("visitor");
    Check((await Post(ownerHttp, "/api/internal/lots", new { lotId = "lot-a", ownerCharacterId = owner, minimumTileX = 0, minimumTileZ = 0, maximumTileX = 9, maximumTileZ = 9 })).Status == HttpStatusCode.NotFound,
        "Players cannot issue deeds: internal routes are blocked at the Gateway");
    var internalClient = new HttpClient(); internalClient.DefaultRequestHeaders.Add("X-LifeCare-Service", key);
    Check((await internalClient.PostAsJsonAsync($"http://127.0.0.1:{constructionPort}/api/internal/lots", new { lotId = "lot-a", ownerCharacterId = owner, minimumTileX = 0, minimumTileZ = 0, maximumTileX = 9, maximumTileZ = 9 })).IsSuccessStatusCode,
        "A trusted service issues the owner a 10x10 deed");

    var floor = await Post(ownerHttp, "/api/lots/lot-a/build", new { characterId = owner, expectedRevision = 0, edit = new { kind = "AddFloor", level = 0, minimumTileX = 2, minimumTileZ = 2, maximumTileX = 4, maximumTileZ = 4 } });
    Check(floor.Status == HttpStatusCode.OK && floor.Body!["lot"]!["revision"]!.GetValue<long>() == 1, "Owner lays a 3x3 floor through the Gateway (revision 1)", (int)floor.Status);
    var walls = new List<object>();
    for (int i = 2; i <= 4; i++) { walls.Add(new { edgeX = i, edgeZ = 2, axis = "AlongX", level = 0 }); walls.Add(new { edgeX = i, edgeZ = 5, axis = "AlongX", level = 0 }); walls.Add(new { edgeX = 2, edgeZ = i, axis = "AlongZ", level = 0 }); walls.Add(new { edgeX = 5, edgeZ = i, axis = "AlongZ", level = 0 }); }
    var walled = await Post(ownerHttp, "/api/lots/lot-a/build", new { characterId = owner, expectedRevision = 1, edit = new { kind = "AddWall", level = 0, walls } });
    Check(walled.Status == HttpStatusCode.OK, "Owner walls the room in (revision 2)", (int)walled.Status);
    var stale = await Post(ownerHttp, "/api/lots/lot-a/build", new { characterId = owner, expectedRevision = 1, edit = new { kind = "AddFloor", level = 0, minimumTileX = 6, minimumTileZ = 6, maximumTileX = 6, maximumTileZ = 6 } });
    Check(stale.Status == HttpStatusCode.Conflict, "An edit made against an old revision is refused", (int)stale.Status);
    var bed = new { instanceId = "bed-1", definitionId = "exam-bed", level = 0, tileX = 2, tileZ = 2, subCellX = 2, subCellZ = 2, yawQuarterTurns = 0, footprintWidthSubCells = 4, footprintDepthSubCells = 2 };
    var preview = await Post(ownerHttp, "/api/lots/lot-a/furnishings/preview", new { characterId = owner, piece = bed });
    Check(preview.Body?["isValid"]?.GetValue<bool>() == true, "Ghost preview through the Gateway says the bed fits");
    Check((await Post(ownerHttp, "/api/lots/lot-a/furnishings/place", new { characterId = owner, piece = bed })).Status == HttpStatusCode.OK, "Owner sets the bed down (revision 3)");

    var seen = JsonNode.Parse(await visitorHttp.GetStringAsync(gateway + "/construction/api/lots/lot-a"))!;
    Check(seen["revision"]!.GetValue<long>() == 3 && seen["floors"]!.AsArray().Count == 9 && seen["walls"]!.AsArray().Count == 12 && seen["furnishings"]!.AsArray().Count == 1,
        "A second player sees the same lot: revision 3, 9 floor tiles, 12 walls, 1 bed");
    var intrude = await Post(visitorHttp, "/api/lots/lot-a/build", new { characterId = visitor, edit = new { kind = "AddFloor", level = 0, minimumTileX = 7, minimumTileZ = 7, maximumTileX = 7, maximumTileZ = 7 } });
    Check(intrude.Status == (HttpStatusCode)422 && intrude.Body?["errorCode"]?.GetValue<string>() == "not_owner", "The other player cannot build on the lot", (int)intrude.Status);
    var impersonate = await Post(visitorHttp, "/api/lots/lot-a/build", new { characterId = owner, edit = new { kind = "AddFloor", level = 0, minimumTileX = 7, minimumTileZ = 7, maximumTileX = 7, maximumTileZ = 7 } });
    Check(impersonate.Status == HttpStatusCode.Forbidden, "A player cannot act as someone else's character", (int)impersonate.Status);
    Check((await plain.GetAsync(gateway + "/construction/api/lots/lot-a")).StatusCode == HttpStatusCode.Unauthorized, "Lots are not readable without signing in");

    construction.Kill(true); construction.WaitForExit(10000);
    Start(Dll("LifeCare.Construction.Service"), constructionArgs);
    Check(await Until(async () => JsonNode.Parse(await visitorHttp.GetStringAsync(gateway + "/construction/api/lots/lot-a"))!["revision"]!.GetValue<long>() == 3, 60),
        "After a restart of the Construction service the lot is unchanged at revision 3");
}
catch (Exception error) { Check(false, "Probe ran to completion", error.Message); }
finally
{
    foreach (var p in processes) try { if (!p.HasExited) p.Kill(true); } catch { }
    File.WriteAllText(Path.Combine(output, "construction-probe.json"), JsonSerializer.Serialize(new { passed, failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{passed}/{passed + failed} construction service checks passed");
}
return failed == 0 ? 0 : 1;
