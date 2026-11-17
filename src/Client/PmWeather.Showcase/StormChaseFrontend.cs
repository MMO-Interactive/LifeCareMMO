using System.Diagnostics;
using Engine.GUI;
using Shared.Contracts.Rendering;

/// <summary>Login, characters, server browser and local hosting rendered by the same DX12 UI as gameplay.</summary>
internal sealed class StormChaseFrontend : IDisposable
{
    private readonly GuiDx12Composer composer = new();
    private readonly GuiLayoutEngine layout;
    private readonly ChaseApiClient api = new();
    private readonly ChaseClientPreferences preferences = ChaseClientPreferences.Load();
    private readonly LocalChaseServer localServer;
    private readonly LocalChaseServer accountsServer = new(accountsOnly: true);
    private readonly Dictionary<string, ChaseTextBuffer> fields = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private Task<Action>? operation;
    private GuiFrame previous = GuiFrame.Empty;
    private string? focus;
    private long revision;
    private bool disposed;
    private ChaseCharacter[] characters = [];
    private ChaseBrowserEntry[] servers = [];
    private ChaseCharacter? character;
    private ChaseFrontendSelection? selection;
    private int selectedServer;
    private string? pendingMode;
    private bool loadingGame;
    public ChaseFrontendScreen Screen { get; private set; } = ChaseFrontendScreen.Login;
    public string Status { get; private set; } = "Sign in to your Storm Chase account.";
    public bool Busy => operation is not null || loadingGame;
    public bool ExitRequested { get; private set; }
    public GuiFrame Frame => previous;
    public ChaseFrontendSelection? Selection => selection;
    public ChaseClientPreferences Preferences => preferences;
    public bool SignedIn => api.Login is not null;

    public StormChaseFrontend(LocalChaseServer localServer)
    {
        this.localServer = localServer; layout = new(composer);
        fields["server"] = new(preferences.LastServer, 256);
        fields["username"] = new(preferences.Username, 32);
        fields["password"] = new("", 128);
        fields["character"] = new("", 32);
        fields["host-name"] = new("My Storm Chase", 64);
        fields["http-port"] = new("5180", 5);
        fields["game-port"] = new("7777", 5);
        fields["players"] = new("16", 2);
        fields["seed"] = new("73019", 10);
        focus = "username";
    }

    public RenderUiFrame Update(int width, int height, GuiInputState input, IReadOnlyList<GuiKeyboardEvent> keyboard, double seconds)
    {
        float scale = Math.Min(1, Math.Min(width / 960f, height / 720f));
        if (scale < 1)
        {
            width = (int)Math.Ceiling(width / scale); height = (int)Math.Ceiling(height / scale);
            input = new(input.Pointer with { X = input.Pointer.X / scale, Y = input.Pointer.Y / scale });
        }
        FinishOperation();
        previous = layout.BuildFrame(Document(width, height), new(width, height), input, previous.Interaction);
        if (input.Pointer.IsPrimaryButtonPressed)
        {
            var hovered = previous.Elements.LastOrDefault(element => element.IsInteractive && element.Bounds.Contains(input.Pointer.X, input.Pointer.Y));
            if (hovered?.ElementId is { } id) focus = id;
        }
        foreach (var key in keyboard)
        {
            if (key.Key == 0x09)
            {
                var available = previous.Elements.Where(element => element.IsInteractive && element.ElementId is not null).Select(element => element.ElementId!).Distinct().ToArray();
                int index = Array.IndexOf(available, focus);
                if (available.Length > 0) focus = available[(index + (key.Shift ? available.Length - 1 : 1)) % available.Length];
            }
            else if (key.Key == 0x1B && !Busy) Back();
            else if (key.Key == 0x0D && !Busy)
                Activate(focus is not null && !fields.ContainsKey(focus) ? focus : DefaultAction());
            else if (focus is not null && fields.TryGetValue(focus, out var field) && !Busy) field.Apply(key);
        }
        if (previous.Interaction.ActivatedElementId is { } action && !Busy && !fields.ContainsKey(action)) Activate(action);
        // Rebuild after interaction so text, focus and asynchronous state are visible this frame.
        previous = layout.BuildFrame(Document(width, height), new(width, height), input, previous.Interaction);
        return composer.Build(previous, ++revision, mesh => DrawFocus(mesh, seconds));
    }

    private void DrawFocus(GuiDx12MeshBuilder mesh, double seconds)
    {
        if (focus is null) return;
        var element = previous.Elements.LastOrDefault(element => element.ElementId == focus);
        if (element is null) return;
        float k = menuScale;
        mesh.BorderRect(element.Bounds, Accent, 2 * k, 10 * k);
        if (fields.TryGetValue(focus!, out var field) && seconds % 1 < .6 && !Busy)
        {
            string prefix = focus == "password" ? new string('*', field.Cursor) : field.Value[..field.Cursor];
            float offset = composer.Measure(prefix, new(StormChaseHud.Theme.Font, ChaseMenuKit.FieldTextSize * k, 1.1f, Ink), new(0, 0), GuiTextWrapMode.NoWrap).Width;
            float x = Math.Min(element.Bounds.Right - 14 * k, element.Bounds.X + ChaseMenuKit.FieldTextInset * k + offset);
            mesh.Line(x, element.Bounds.Y + 12 * k, x, element.Bounds.Bottom - 12 * k, Accent, 1.5f * k);
        }
    }

    private void Activate(string action)
    {
        // Authentication is the front door, just as it is in Tiny Clinic. Game modes live in the signed-in menu.
        if (api.Login is null && action is not ("login" or "register" or "quit"))
        { Change(ChaseFrontendScreen.Login); Status = "Sign in to continue."; return; }
        switch (action)
        {
            case "login": SignIn(false); break;
            case "register": SignIn(true); break;
            case "offline":
                if (character is null) { pendingMode = "offline"; LoadCharacters(); break; }
                selection = new(true, AccountAddress: api.Address, AccessToken: api.Login!.AccessToken, Character: character, RefreshToken: api.Login.RefreshToken); break;
            case "menu":
                if (pendingMode is { } mode && character is not null) { pendingMode = null; Activate(mode); }
                else Change(ChaseFrontendScreen.Menu);
                break;
            case "browser": Change(ChaseFrontendScreen.Browser); RefreshServers(); break;
            case "refresh": RefreshServers(); break;
            case "add-server":
                try { using var probe = new ChaseApiClient(); probe.Configure(fields["server"].Value); preferences.RememberServer(probe.Address); RefreshServers(); }
                catch (Exception e) { ReportFailure(e); }
                break;
            case "host": Change(ChaseFrontendScreen.Host); break;
            case "start-host": StartHost(); break;
            case "stop-host": Start(async () => { await localServer.StopAsync(); return () => Status = "Your local server stopped."; }); break;
            case "characters": LoadCharacters(); break;
            case "create-character":
                Start(async () => { var created = await api.CreateCharacterAsync(fields["character"].Value.Trim(), lifetime.Token);
                    var all = await api.CharactersAsync(lifetime.Token); return () => { characters = all; character = created; fields["character"].Set(""); Status = "Character created. Ready to chase."; }; });
                break;
            case "join": Join(); break;
            case "settings": Change(ChaseFrontendScreen.Settings); break;
            case "quality":
                preferences.WeatherQuality = preferences.WeatherQuality == "High" ? "Ultra" : "High"; preferences.Save(); break;
            case "mute": preferences.Muted = !preferences.Muted; preferences.Save(); break;
            case "fullscreen": preferences.Fullscreen = !preferences.Fullscreen; preferences.Save(); break;
            case "sign-out": api.SignOut(); character = null; characters = []; pendingMode = null; fields["password"].Set(""); Change(ChaseFrontendScreen.Login); Status = "Sign in to your Storm Chase account."; break;
            case "back": Back(); break;
            case "quit": ExitRequested = true; break;
            default:
                if (action.StartsWith("server/", StringComparison.Ordinal) && int.TryParse(action.AsSpan(7), out int serverIndex) && serverIndex >= 0 && serverIndex < servers.Length)
                { selectedServer = serverIndex; fields["server"].Set(servers[serverIndex].Address); preferences.RememberServer(servers[serverIndex].Address); Status = servers[serverIndex].Server?.Name ?? "Selected server is unavailable."; }
                else if (action.StartsWith("character/", StringComparison.Ordinal))
                { character = characters.FirstOrDefault(item => item.Id == action[10..]); Status = character is null ? "Select a character." : "Selected " + character.Name + "."; }
                break;
        }
        Console.WriteLine("Frontend action: " + action.Split('/')[0]);
    }

    private void SignIn(bool register)
    {
        string username = fields["username"].Value.Trim(), password = fields["password"].Value;
        if (username.Length < 3 || username.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') || password.Length < 10)
        { Status = "Use 3–32 letters, digits or underscores for your account, and at least 10 characters for your password."; return; }
        Start(async () =>
        {
            api.Configure(preferences.AccountAddress); await accountsServer.EnsureAccountsAsync(api.Address, lifetime.Token);
            await api.SignInAsync(username, password, register, lifetime.Token);
            var all = await api.CharactersAsync(lifetime.Token);
            return () => { preferences.Username = username; preferences.AccountAddress = api.Address; preferences.Save(); fields["password"].Set("");
                characters = all; character = all.FirstOrDefault(); Change(ChaseFrontendScreen.Menu);
                Status = "Signed in as " + username + "."; };
        });
    }
    private void LoadCharacters()
    {
        if (api.Login is null) { Change(ChaseFrontendScreen.Login); return; }
        Start(async () => { var all = await api.CharactersAsync(lifetime.Token); return () => { characters = all; Change(ChaseFrontendScreen.Characters); }; });
    }
    private void RefreshServers()
    {
        string[] favourites = preferences.Servers.Take(32).ToArray();
        Start(async () => {
            var directory = await api.DirectoryAsync(lifetime.Token);
            var addresses = directory.Select(server => server.ApiAddress).Where(address => !string.IsNullOrWhiteSpace(address))
                .Concat(favourites).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
            var all = await Task.WhenAll(addresses.Select(address => api.ProbeAsync(address, lifetime.Token)));
            return () => { servers = all.OrderByDescending(entry => entry.Server is { Ready: true }).ThenBy(entry => entry.Server?.Name ?? entry.Address).ToArray();
                selectedServer = Math.Clamp(selectedServer, 0, Math.Max(0, all.Length - 1));
                if (servers.Length > 0) fields["server"].Set(servers[selectedServer].Address);
                Status = all.Count(item => item.Server is { Ready: true }) + " chase server(s) available.";
            };
        });
    }
    private void StartHost()
    {
        if (!int.TryParse(fields["http-port"].Value, out int httpPort) || !int.TryParse(fields["game-port"].Value, out int gamePort) ||
            !int.TryParse(fields["players"].Value, out int maximum) || !int.TryParse(fields["seed"].Value, out int seed))
        { Status = "Ports, player count and world seed must be whole numbers."; return; }
        string name = fields["host-name"].Value.Trim();
        if (name.Length < 3) { Status = "Give your server a name of at least 3 characters."; return; }
        localServer.MasterAddress = api.Address;
        Start(async () => { await localServer.StartAsync(name, httpPort, gamePort, maximum, seed, lifetime.Token);
            return () => { fields["server"].Set(localServer.Address); preferences.RememberServer(localServer.Address);
                servers = [new(localServer.Address, null, "Loading world…", 0)]; selectedServer = 0;
                Change(ChaseFrontendScreen.Browser); Status = "Your server started. Refresh when its world is ready."; }; });
    }
    private void Join()
    {
        string target = fields["server"].Value.Trim();
        if (api.Login is null)
        { Change(ChaseFrontendScreen.Login); Status = "Sign in before joining."; return; }
        if (character is null) { pendingMode = "join"; LoadCharacters(); return; }
        Start(async () => { using var targetApi = new ChaseApiClient(); targetApi.Configure(target);
            var uri = new Uri(targetApi.Address);
            if (!uri.IsLoopback && uri.Scheme != "https") throw new InvalidOperationException("Use the server's HTTPS address to join an online chase.");
            var server = await targetApi.ServerAsync(lifetime.Token);
            if (!server.Ready) throw new InvalidOperationException("The server is still loading its world. Try again shortly.");
            return () => {
            // Acquire the short-lived admission ticket only after world/avatar loading finishes.
            selection = new(false, null, targetApi.Address, api.Login!.AccessToken, character, server, api.Address, api.Login.RefreshToken); Screen = ChaseFrontendScreen.Joining; Status = "Joining the chase…";
        }; });
    }
    private void Start(Func<Task<Action>> action)
    {
        if (Busy) return;
        operation = Task.Run(action, lifetime.Token); Status = "Connecting…";
    }
    private void FinishOperation()
    {
        if (operation is not { IsCompleted: true }) return;
        try { operation.GetAwaiter().GetResult()(); }
        catch (Exception e) { ReportFailure(e); }
        finally { operation = null; }
    }
    private void ReportFailure(Exception error)
    {
        Console.Error.WriteLine("Frontend operation failed (" + error.GetType().Name + "): " + error.Message);
        Status = PlayerSafeFailure(error);
    }
    internal static string PlayerSafeFailure(Exception error) => error is InvalidOperationException && error.Message is
        "Sign-in failed or expired. Check your account and password." or
        "That account or character already exists." or
        "This account cannot access that character or action." or
        "Too many requests. Wait a moment and try again." or
        "Your login expired. Sign in again." or
        "Sign in to continue." or
        "The server is still loading its world. Try again shortly." or
        "Your local server is already running."
            ? error.Message : "The service is unavailable. Please try again later.";
    private void Change(ChaseFrontendScreen screen)
    { Screen = screen; previous = GuiFrame.Empty; focus = screen switch { ChaseFrontendScreen.Login => "username", ChaseFrontendScreen.Host => "host-name", ChaseFrontendScreen.Characters => "character", ChaseFrontendScreen.Browser => "server", _ => null }; }
    private void Back() { if (Screen != ChaseFrontendScreen.Login && api.Login is not null) { pendingMode = null; Change(ChaseFrontendScreen.Menu); } }
    public void ReturnToMenu(string status = "")
    {
        selection = null; pendingMode = null; loadingGame = false;
        Change(api.Login is null ? ChaseFrontendScreen.Login : ChaseFrontendScreen.Menu);
        if (string.IsNullOrWhiteSpace(status)) Status = "Ready for your next chase.";
        else if (status is "Returned to the main menu." or "Ready for your next chase." or "The service is unavailable. Please try again later.") Status = status;
        else
        {
            Console.Error.WriteLine("Frontend session ended: " + status);
            Status = PlayerSafeFailure(new InvalidOperationException(status));
        }
    }
    public void SetJoiningStatus(string status) { loadingGame = true; Change(ChaseFrontendScreen.Joining); Status = status; }
    public void JoinFailed(string status)
    {
        var error = new InvalidOperationException(status);
        Console.Error.WriteLine("Frontend join failed: " + status);
        ReturnToMenu(PlayerSafeFailure(error));
    }
    private string DefaultAction() => Screen switch { ChaseFrontendScreen.Login => "login", ChaseFrontendScreen.Host => "start-host", ChaseFrontendScreen.Characters => "create-character", ChaseFrontendScreen.Browser => "join", _ => "browser" };

    private static readonly GuiColor Ink = StormChaseHud.Theme.Ink, Muted = StormChaseHud.Theme.Muted, Accent = StormChaseHud.Theme.Accent;
    private float menuScale = 1;

    private GuiDocument Document(int width, int height)
    {
        float k = ChaseMenuKit.ScaleFor(width, height); menuScale = k;
        float W = width / k, H = height / k;
        bool brand = W >= 1060;
        const float cardWidth = 460, pad = 32, inner = cardWidth - pad * 2, half = (inner - 12) / 2;
        var content = new List<GuiElement>();
        var kit = new ChaseMenuKit(k, content);
        var primary = ChaseMenuKit.Kind.Primary; var ghost = ChaseMenuKit.Kind.Ghost;
        float y = pad;
        void Heading(string title, string subtitle)
        {
            kit.Text(title, pad, y, inner, 22, Ink, bold: true); y += 42;
            kit.Text(subtitle, pad, y, inner, 10.5f, Muted, wrap: true, lines: 2); y += 48;
        }
        void ModeButton(string id, string label, string description, ChaseMenuKit.Kind kind)
        {
            kit.Button(id, label, pad, y, inner, kind, 54, enabled: !Busy, align: GuiTextAlignment.Left, size: 12.5f);
            kit.Text(description, pad + inner * .45f, y + 19, inner * .55f - 18, 9.5f,
                kind == primary ? new GuiColor(6, 20, 22, 190) : Muted, align: GuiTextAlignment.Right);
            y += 62;
        }
        switch (Screen)
        {
            case ChaseFrontendScreen.Login:
                Heading("Sign in", "Your chasers and careers follow your account to every server.");
                kit.Field("username", "Account name", fields["username"].Value, pad, y, inner, enabled: !Busy); y += 80;
                kit.Field("password", "Password", fields["password"].Value, pad, y, inner, password: true, enabled: !Busy); y += 88;
                kit.Button("login", "SIGN IN", pad, y, half, primary, enabled: !Busy);
                kit.Button("register", "CREATE ACCOUNT", pad + half + 12, y, half, enabled: !Busy); y += 58;
                kit.Button("quit", "QUIT", pad, y, inner, ghost, 40); y += 52;
                break;
            case ChaseFrontendScreen.Menu:
                Heading("Welcome back", "Choose how you want to chase today.");
                string who = character?.Name ?? preferences.Username ?? "Chaser";
                content.Add(ChaseMenuKit.Fill(pad * k, y * k, 46 * k, 46 * k, Accent.WithAlpha(46), 23 * k));
                kit.Text(who.Length > 0 ? who[..1].ToUpperInvariant() : "?", pad, y + 11, 46, 14, Accent, bold: true, align: GuiTextAlignment.Center);
                kit.Text(who.Length > 0 ? who : "Chaser", pad + 60, y + 3, inner - 60, 13, Ink, bold: true);
                kit.Text(character is null ? "SIGNED IN AS " + preferences.Username : $"REPUTATION {character.Reputation}   ·   {preferences.Username}",
                    pad + 60, y + 26, inner - 60, 8.5f, Muted);
                y += 66;
                ModeButton("offline", "SINGLE PLAYER", "Your own career and storms", primary);
                ModeButton("host", "HOST A SERVER", "Run a world for friends", ChaseMenuKit.Kind.Secondary);
                ModeButton("browser", "JOIN MULTIPLAYER", "Browse live chases", ChaseMenuKit.Kind.Secondary);
                kit.Button("characters", "CHARACTERS", pad, y, half, height: 42, enabled: !Busy);
                kit.Button("settings", "SETTINGS", pad + half + 12, y, half, height: 42, enabled: !Busy); y += 52;
                kit.Button("sign-out", "SIGN OUT", pad, y, half, ghost, 40, enabled: !Busy);
                kit.Button("quit", "QUIT", pad + half + 12, y, half, ghost, 40); y += 50;
                break;
            case ChaseFrontendScreen.Browser:
                Heading("Find a chase", "Servers registered with your account service, plus your favourites.");
                kit.Field("server", "Direct address or favourite", fields["server"].Value, pad, y, inner, enabled: !Busy); y += 80;
                kit.Button("add-server", "ADD / FIND SERVER", pad, y, half, height: 42, enabled: !Busy);
                kit.Button("refresh", "REFRESH", pad + half + 12, y, half, height: 42, enabled: !Busy); y += 56;
                for (int i = 0; i < Math.Min(5, servers.Length); i++)
                {
                    var entry = servers[i];
                    string name = entry.Server?.Name ?? entry.Address;
                    string detail = entry.Server is { } server ? $"{server.Players}/{server.MaxPlayers}   ·   {entry.PingMs:0} ms" : entry.Error ?? "Unavailable";
                    kit.Row("server/" + i, name, detail, pad, y, inner, i == selectedServer,
                        entry.Server is { Ready: true } ? Accent : entry.Server is null ? StormChaseHud.Theme.Danger : StormChaseHud.Theme.Warn);
                    y += 54;
                }
                if (servers.Length == 0) { kit.Text("Add a server address, or host your own chase.", pad, y + 4, inner, 11, Muted); y += 40; }
                y += 6;
                kit.Button("join", "JOIN SELECTED CHASE", pad, y, half, primary, enabled: !Busy);
                kit.Button("back", "BACK", pad + half + 12, y, half, ghost); y += 58;
                break;
            case ChaseFrontendScreen.Host:
                Heading("Host a server", "Run the dedicated server here. Accounts and careers stay saved between sessions.");
                kit.Field("host-name", "Server name", fields["host-name"].Value, pad, y, inner, enabled: !Busy); y += 80;
                kit.Field("http-port", "API port", fields["http-port"].Value, pad, y, half, enabled: !Busy);
                kit.Field("game-port", "Game port (UDP)", fields["game-port"].Value, pad + half + 12, y, half, enabled: !Busy); y += 80;
                kit.Field("players", "Player limit", fields["players"].Value, pad, y, half, enabled: !Busy);
                kit.Field("seed", "World seed", fields["seed"].Value, pad + half + 12, y, half, enabled: !Busy); y += 88;
                kit.Button(localServer.Running ? "stop-host" : "start-host", localServer.Running ? "STOP MY SERVER" : "START SERVER", pad, y, inner, primary, enabled: !Busy); y += 56;
                kit.Button("back", "BACK", pad, y, inner, ghost, 40); y += 50;
                break;
            case ChaseFrontendScreen.Characters:
                Heading("Your chasers", "Each chaser keeps their own reputation, money and career.");
                for (int i = 0; i < Math.Min(4, characters.Length); i++)
                {
                    kit.Row("character/" + characters[i].Id, characters[i].Name, $"REPUTATION {characters[i].Reputation}", pad, y, inner, characters[i].Id == character?.Id);
                    y += 54;
                }
                if (characters.Length == 0) { kit.Text("Create your first chaser to begin a career.", pad, y + 4, inner, 11, Muted); y += 40; }
                y += 6;
                kit.Field("character", "New chaser name", fields["character"].Value, pad, y, inner, enabled: !Busy); y += 76;
                kit.Button("create-character", "CREATE CHASER", pad, y, inner, height: 42, enabled: !Busy); y += 54;
                kit.Button("menu", pendingMode == "offline" ? "START SINGLE PLAYER" : pendingMode == "join" ? "JOIN CHASE" : "CONTINUE", pad, y, half, primary, enabled: character is not null && !Busy);
                kit.Button("back", "BACK", pad + half + 12, y, half, ghost); y += 58;
                break;
            case ChaseFrontendScreen.Settings:
                Heading("Settings", "Saved for this computer.");
                kit.Row("quality", "TORNADO QUALITY", preferences.WeatherQuality.ToUpperInvariant(), pad, y, inner, false, Accent); y += 54;
                kit.Text("High keeps the established quality and frame budget. Ultra samples the storm volume more finely.", pad, y, inner, 9.5f, Muted, wrap: true, lines: 2); y += 44;
                kit.Row("mute", "WEATHER AUDIO", preferences.Muted ? "OFF" : "ON", pad, y, inner, false, preferences.Muted ? Muted : Accent); y += 54;
                kit.Row("fullscreen", "START IN FULLSCREEN", preferences.Fullscreen ? "ON" : "OFF", pad, y, inner, false, preferences.Fullscreen ? Accent : Muted); y += 62;
                kit.Text("WASD move or drive  ·  click to look  ·  E vehicle  ·  U shop  ·  B probe  ·  F photo  ·  F11 fullscreen", pad, y, inner, 9, Muted, wrap: true, lines: 2); y += 46;
                kit.Button("back", "SAVE AND RETURN", pad, y, inner, primary); y += 58;
                break;
            case ChaseFrontendScreen.Joining:
                Heading("Joining the chase", "Loading your chaser and the server's storm world…");
                y += 40;
                break;
        }
        var statusColor = Busy ? Accent : Muted;
        content.Add(ChaseMenuKit.Fill(pad * k, (y + 8) * k, 8 * k, 8 * k, statusColor, 4 * k));
        kit.Text(Busy ? "Connecting…" : Status, pad + 18, y, inner - 18, 9.5f, statusColor, wrap: true, lines: 2);
        y += 38;
        float cardHeight = y + pad - 14;
        float cardX = brand ? W - Math.Max(56, W * .07f) - cardWidth : (W - cardWidth) / 2;
        float cardY = Math.Max(16, (H - cardHeight) / 2);

        var root = new List<GuiElement>(ChaseMenuKit.Backdrop(width, height, brand));
        var outer = new ChaseMenuKit(k, root);
        if (brand)
        {
            float bx = Math.Max(56, W * .07f), by = H * .30f;
            outer.Text("STORM CHASE", bx, by, 620, 46, Ink, bold: true);
            root.Add(ChaseMenuKit.Fill(bx * k, (by + 86) * k, 64 * k, 4 * k, Accent, 2 * k));
            outer.Text("COOPERATIVE TORNADO RESEARCH", bx, by + 104, 520, 11, Accent, bold: true);
            outer.Text("Read the sounding, pick your target, intercept the storm and document it before it crosses 49 km of Midwest prairie.",
                bx, by + 132, 440, 12, Muted, wrap: true, lines: 3);
        }
        root.Add(new GuiOverlayPanel
        {
            Id = "menu-card", Translation = new(cardX * k, cardY * k), Width = GuiLength.Pixels(cardWidth * k), Height = GuiLength.Pixels(cardHeight * k),
            BackgroundColor = new(9, 16, 23, 238), BorderColor = StormChaseHud.Theme.GlassEdge, BorderThickness = Math.Max(1, k), CornerRadius = 16 * k,
            Items = content,
        });
        outer.Text("TAB NAVIGATE   ·   ENTER SELECT   ·   ESC BACK   ·   F11 FULLSCREEN", brand ? Math.Max(56, W * .07f) : 0, H - 40, brand ? 640 : W, 8.5f,
            StormChaseHud.Theme.Faint, align: brand ? GuiTextAlignment.Left : GuiTextAlignment.Center);
        return new(new GuiOverlayPanel { Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height), Items = root });
    }

    internal void ShowForVerification(ChaseFrontendScreen screen) => Change(screen);
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); api.Dispose();
        // Background HTTP operations are cancelled before native font resources are released.
        if (operation is not null) try { operation.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        accountsServer.Dispose(); lifetime.Dispose(); composer.Dispose(); fields["password"].Set("");
    }
}
