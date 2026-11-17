using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

internal readonly record struct GuiKeyboardEvent(int Key, char Character, bool Shift, bool Control = false);
internal enum ChaseFrontendScreen { Login, Menu, Browser, Host, Characters, Settings, Joining }

internal sealed record ChaseServerInfo(string ServerId = "", string Name = "Storm Chase", string Region = "Local",
    int Players = 0, int MaxPlayers = 16, int GamePort = 7777, int WorldSeed = 73019,
    float WorldSizeKm = 48, int Day = 1, float Hour = 8, int StormCount = 0, string Version = "1",
    string TransportAuthority = "", string TransportIdentity = "", string WorldContentFingerprint = "", bool Ready = false,
    string ApiAddress = "");
internal sealed record ChaseCharacter(string Id, string Name, long Reputation = 0, DateTimeOffset CreatedUtc = default);
internal sealed record ChaseLogin(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
internal sealed record ChaseJoinTicket(string Ticket, DateTimeOffset ExpiresUtc, string Host,
    int GamePort, string ServerId, string CharacterId);
internal sealed record ChaseBrowserEntry(string Address, ChaseServerInfo? Server, string? Error, double PingMs);
internal sealed record ChaseFrontendSelection(bool Offline, ChaseJoinTicket? Ticket = null,
    string? ApiAddress = null, string? AccessToken = null, ChaseCharacter? Character = null, ChaseServerInfo? Server = null,
    string? AccountAddress = null, string? RefreshToken = null);

/// <summary>Editable text stays in the game; native messages supply characters, not native edit controls.</summary>
internal sealed class ChaseTextBuffer(string value = "", int maximumLength = 128)
{
    public string Value { get; private set; } = value;
    public int Cursor { get; private set; } = value.Length;
    public int MaximumLength { get; } = maximumLength;
    public void Set(string value) { Value = value[..Math.Min(value.Length, MaximumLength)]; Cursor = Value.Length; }
    public void Apply(GuiKeyboardEvent input)
    {
        if (input.Character == '\b')
        {
            if (Cursor > 0) { Value = Value.Remove(Cursor - 1, 1); Cursor--; }
        }
        else if (!char.IsControl(input.Character) && input.Character != '\0' && Value.Length < MaximumLength)
        { Value = Value.Insert(Cursor, input.Character.ToString()); Cursor++; }
        else if (input.Key == 0x25) Cursor = Math.Max(0, Cursor - 1);
        else if (input.Key == 0x27) Cursor = Math.Min(Value.Length, Cursor + 1);
        else if (input.Key == 0x24) Cursor = 0;
        else if (input.Key == 0x23) Cursor = Value.Length;
        else if (input.Key == 0x2E && Cursor < Value.Length) Value = Value.Remove(Cursor, 1);
    }
}

internal sealed class ChaseClientPreferences
{
    public string AccountAddress { get; set; } = ChaseClientConfiguration.AccountAddress;
    public string LastServer { get; set; } = "http://127.0.0.1:5180";
    public string Username { get; set; } = "";
    public List<string> Servers { get; set; } = ["http://127.0.0.1:5180"];
    public string WeatherQuality { get; set; } = "High";
    public bool Muted { get; set; }
    public bool Fullscreen { get; set; }
    public static string DataRoot => Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_DATA_ROOT") is { Length: > 0 } root
        ? Path.GetFullPath(root) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMOI", "StormChase");
    private static string FilePath => Path.Combine(DataRoot, "client-settings.json");
    public static ChaseClientPreferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<ChaseClientPreferences>(File.ReadAllText(FilePath)) ?? new();
            // The endpoint belongs to the deployment, including migrations from
            // older clients that persisted a player-editable service address.
            value.AccountAddress = ChaseClientConfiguration.AccountAddress;
            value.LastServer ??= "http://127.0.0.1:5180"; value.Username ??= "";
            value.Servers = (value.Servers ?? []).Where(address => !string.IsNullOrWhiteSpace(address)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
            if (value.WeatherQuality is not ("High" or "Ultra")) value.WeatherQuality = "High";
            return value;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataRoot);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void RememberServer(string address)
    {
        LastServer = address;
        Servers.RemoveAll(item => string.Equals(item, address, StringComparison.OrdinalIgnoreCase));
        Servers.Insert(0, address);
        if (Servers.Count > 32) Servers.RemoveRange(32, Servers.Count - 32);
        Save();
    }
}

internal sealed class ChaseApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    public string Address { get; private set; } = ChaseClientConfiguration.AccountAddress;
    public ChaseLogin? Login { get; private set; }
    public void Configure(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTP or HTTPS server address, such as http://127.0.0.1:5080.");
        string normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (normalized != Address) { Address = normalized; Login = null; }
    }
    public async Task<ChaseServerInfo> ServerAsync(CancellationToken cancellation = default) =>
        await GetAsync<ChaseServerInfo>("/api/server", cancellation, false);
    public Task<ChaseServerInfo[]> DirectoryAsync(CancellationToken cancellation) =>
        GetAsync<ChaseServerInfo[]>("/api/servers", cancellation);
    public async Task SignInAsync(string username, string password, bool register, CancellationToken cancellation)
    {
        var uri = new Uri(Address);
        if (uri.Scheme != "https" && !uri.IsLoopback)
            throw new InvalidOperationException("An online server must use HTTPS before you send account credentials.");
        if (register) await SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/register", new { username, password }, cancellation, false);
        Login = await SendAsync<ChaseLogin>(HttpMethod.Post, "/api/auth/login", new { username, password }, cancellation, false);
        if (string.IsNullOrWhiteSpace(Login.AccessToken)) throw new InvalidDataException("The server did not return a login token.");
    }
    public Task<ChaseCharacter[]> CharactersAsync(CancellationToken cancellation) => GetAsync<ChaseCharacter[]>("/api/characters", cancellation);
    public Task<ChaseCharacter> CreateCharacterAsync(string name, CancellationToken cancellation) =>
        SendAsync<ChaseCharacter>(HttpMethod.Post, "/api/characters", new { name }, cancellation);
    public Task<ChaseJoinTicket> JoinAsync(string characterId, CancellationToken cancellation) =>
        SendAsync<ChaseJoinTicket>(HttpMethod.Post, "/api/matches/join", new { characterId }, cancellation);
    public Task<ChaseJoinTicket> JoinAsync(string characterId, string serverId, CancellationToken cancellation) =>
        SendAsync<ChaseJoinTicket>(HttpMethod.Post, "/api/matches/join", new { characterId, serverId }, cancellation);
    public Task<JsonElement> CareerAsync(string characterId, CancellationToken cancellation) =>
        GetAsync<JsonElement>("/api/characters/" + Uri.EscapeDataString(characterId) + "/career", cancellation);
    public async Task<ChaseBrowserEntry> ProbeAsync(string address, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var probe = new ChaseApiClient(); probe.Configure(address);
            var server = await probe.ServerAsync(cancellation);
            return new(probe.Address, server, null, clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ArgumentException or InvalidDataException)
        { return new(address, null, e is TaskCanceledException ? "Timed out" : "Unavailable", clock.Elapsed.TotalMilliseconds); }
    }
    public void SignOut() => Login = null;
    public void ResumeLogin(string address, string accessToken, string? refreshToken = null)
    { Configure(address); if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("A valid access token is required."); Login = new("Bearer", accessToken, 0, refreshToken ?? ""); }
    public async Task RefreshLoginAsync(CancellationToken cancellation)
    {
        await refreshGate.WaitAsync(cancellation);
        try { await RefreshCoreAsync(cancellation); }
        finally { refreshGate.Release(); }
    }
    private async Task RefreshCoreAsync(CancellationToken cancellation)
    {
        var existing = Login ?? throw new InvalidOperationException("Sign in to continue.");
        if (string.IsNullOrWhiteSpace(existing.RefreshToken)) throw new InvalidOperationException("Your login expired. Sign in again.");
        var refreshed = await SendAsync<ChaseLogin>(HttpMethod.Post, "/api/auth/refresh",
            new { refreshToken = existing.RefreshToken }, cancellation, false);
        if (string.IsNullOrWhiteSpace(refreshed.AccessToken) || string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            throw new InvalidDataException("The account service did not return valid refreshed tokens.");
        Login = refreshed;
    }
    private Task<T> GetAsync<T>(string path, CancellationToken cancellation, bool authenticated = true) =>
        SendAsync<T>(HttpMethod.Get, path, null, cancellation, authenticated);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellation, bool authenticated = true, bool retryExpired = true)
    {
        using var request = new HttpRequestMessage(method, Address + path);
        if (authenticated)
        {
            if (Login is null) throw new InvalidOperationException("Sign in to this server first.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Login.AccessToken);
        }
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (authenticated && retryExpired && response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            !string.IsNullOrWhiteSpace(Login?.RefreshToken))
        {
            string rejectedToken = request.Headers.Authorization!.Parameter!;
            await refreshGate.WaitAsync(cancellation);
            try { if (Login?.AccessToken == rejectedToken) await RefreshCoreAsync(cancellation); }
            finally { refreshGate.Release(); }
            return await SendAsync<T>(method, path, body, cancellation, authenticated, false);
        }
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Sign-in failed or expired. Check your account and password.",
                System.Net.HttpStatusCode.Conflict => "That account or character already exists.",
                System.Net.HttpStatusCode.Forbidden => "This account cannot access that character or action.",
                System.Net.HttpStatusCode.TooManyRequests => "Too many requests. Wait a moment and try again.",
                _ => "The server rejected the request (" + (int)response.StatusCode + ")."
            };
            // Never echo an arbitrary server error body into the game or logs: it may contain credentials.
            throw new InvalidOperationException(message);
        }
        if (response.Content.Headers.ContentLength is > 1_048_576) throw new InvalidDataException("The server response exceeded the client limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0) break;
            if (bounded.Length + read > 1_048_576) throw new InvalidDataException("The server response exceeded the client limit.");
            bounded.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(bounded.GetBuffer().AsSpan(0, checked((int)bounded.Length)), Json)
            ?? throw new InvalidDataException("The server returned an empty response.");
    }
    public void Dispose() { http.Dispose(); refreshGate.Dispose(); }
}

internal sealed class LocalChaseServer(bool accountsOnly = false) : IDisposable
{
    private Process? process;
    private string controlKey = "";
    private Task? outputTask, errorTask;
    public bool Running => process is { HasExited: false };
    public string Address { get; private set; } = accountsOnly ? "http://127.0.0.1:5080" : "http://127.0.0.1:5180";
    public string? MasterAddress { get; set; }
    public static string AccountDataRoot => Path.Combine(ChaseClientPreferences.DataRoot, "Accounts");
    public string? LogPath { get; private set; }
    public async Task StartAsync(string name, int httpPort, int gamePort, int maximumPlayers, int seed, CancellationToken cancellation)
    {
        if (Running) throw new InvalidOperationException("Your local server is already running.");
        if (httpPort is < 1024 or > 65535 || gamePort is < 1024 or > 65535 || maximumPlayers is < 1 or > 64)
            throw new ArgumentException("Use ports 1024–65535 and 1–64 players.");
        string packaged = Path.Combine(AppContext.BaseDirectory, "Server", "StormChase.Server.exe");
        string development = "";
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "multiplayer", "StormChase.Server", "bin", "Release", "net10.0", "StormChase.Server.exe");
            if (File.Exists(candidate)) { development = candidate; break; }
        }
        string executable = File.Exists(packaged) ? packaged : development;
        if (!File.Exists(executable)) throw new FileNotFoundException("The dedicated server is missing. Install the complete Storm Chase package.", executable);
        string root = accountsOnly ? AccountDataRoot : Path.Combine(ChaseClientPreferences.DataRoot, "Servers", "local-" + httpPort);
        Directory.CreateDirectory(Path.Combine(root, "Logs"));
        LogPath = Path.Combine(root, "Logs", "server-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
        controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true };
        // Development services use the same dotnet host as the client. Published
        // packages carry a self-contained server and launch its apphost directly.
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "coreclr.dll")) &&
            Environment.ProcessPath is { } currentHost && Path.GetFileName(currentHost).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = currentHost;
            start.ArgumentList.Add(Path.ChangeExtension(executable, ".dll"));
        }
        foreach (string argument in new[] { "--http-port", httpPort.ToString(), "--game-port", gamePort.ToString(), "--data-root", root,
            "--server-name", name, "--max-players", maximumPlayers.ToString(), "--world-seed", seed.ToString(), "--control-key", controlKey }) start.ArgumentList.Add(argument);
        if (accountsOnly) start.ArgumentList.Add("--accounts-only");
        else if (MasterAddress is { Length: > 0 } master)
        {
            if (!new Uri(master).IsLoopback)
                throw new InvalidOperationException("A local game server needs the operator's master registration key. Configure dedicated hosting for a remote account service.");
            string keyFile = Path.Combine(AccountDataRoot, "master-server.key");
            if (!File.Exists(keyFile)) throw new InvalidOperationException("Sign in to the local account service before hosting a server.");
            string key = File.ReadAllText(keyFile).Trim();
            if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
                throw new InvalidDataException("The account service's server registration key is invalid.");
            start.ArgumentList.Add("--master-url"); start.ArgumentList.Add(master);
            start.ArgumentList.Add("--master-key-env"); start.ArgumentList.Add("STORMCHASE_LOCAL_MASTER_KEY");
            start.Environment["STORMCHASE_LOCAL_MASTER_KEY"] = key;
        }
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the local server.");
        Address = "http://127.0.0.1:" + httpPort;
        var writer = TextWriter.Synchronized(new StreamWriter(LogPath, false) { AutoFlush = true });
        outputTask = DrainAsync(process.StandardOutput, writer); errorTask = DrainAsync(process.StandardError, writer);
        _ = Task.WhenAll(outputTask, errorTask).ContinueWith(_ => writer.Dispose(), TaskScheduler.Default);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var timeout = Stopwatch.StartNew();
        try
        {
            while (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The local server exited during startup. See " + LogPath);
                try { using var ready = await http.GetAsync(Address + "/health", cancellation); if (ready.IsSuccessStatusCode) return; }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(200, cancellation);
            }
            throw new TimeoutException("Your local server did not become ready. See " + LogPath);
        }
        catch { await StopAsync(); throw; }
    }
    public async Task EnsureAccountsAsync(string address, CancellationToken cancellation)
    {
        if (!accountsOnly) throw new InvalidOperationException("This launcher is not an account service.");
        var target = new Uri(address);
        if (!target.IsLoopback || target.Scheme != "http") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        try { using var ready = await http.GetAsync(address.TrimEnd('/') + "/health", cancellation); if (ready.IsSuccessStatusCode) return; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        cancellation.ThrowIfCancellationRequested();
        await StartAsync("Storm Chase Accounts", target.Port, 7777, 16, 73019, cancellation);
    }
    private static async Task DrainAsync(StreamReader reader, TextWriter writer)
    { while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line); }
    public async Task StopAsync()
    {
        if (process is null) return;
        if (!process.HasExited)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var request = new HttpRequestMessage(HttpMethod.Post, Address + "/api/admin/shutdown");
                request.Headers.Add("X-StormChase-Control", controlKey);
                using var response = await http.SendAsync(request);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        process.Dispose(); process = null; controlKey = "";
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}
