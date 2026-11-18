using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal readonly record struct GuiKeyboardEvent(int Key, char Character, bool Shift, bool Control = false);
/// <summary>LifeCare: Online front door: sign in, pick (or first create) a character, enter the world.</summary>
internal enum ChaseFrontendScreen { Login, Characters, CreateCharacter, Settings, Joining }

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
/// <summary>An authenticated character bound for the Gateway's world. There is no offline selection.</summary>
internal sealed record ChaseFrontendSelection(ChaseJoinTicket? Ticket = null,
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
    /// <summary>The Gateway. Login, characters and the world are all reached through it.</summary>
    public string AccountAddress { get; set; } = ChaseClientConfiguration.AccountAddress;
    public string Username { get; set; } = "";
    /// <summary>The character last taken into the world, preselected on the character screen.</summary>
    public string LastCharacterId { get; set; } = "";
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
            value.Username ??= ""; value.LastCharacterId ??= "";
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
