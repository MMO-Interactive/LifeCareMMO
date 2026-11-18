using Engine.GUI;
using Shared.Contracts.Rendering;

/// <summary>
/// LifeCare: Online front door, rendered by the same DX12 UI as gameplay. The flow is fixed:
/// sign in → choose a character (or create one when the account has none) → enter the world.
/// Every request goes to the Gateway; the world is the Gateway's own district, never a list to pick from.
/// </summary>
internal sealed class StormChaseFrontend : IDisposable
{
    public const string GameTitle = "LifeCare: Online";
    private const int MaximumListedCharacters = 6;
    private readonly GuiDx12Composer composer = new();
    private readonly GuiLayoutEngine layout;
    private readonly ChaseApiClient api = new();
    private readonly ChaseClientPreferences preferences = ChaseClientPreferences.Load();
    private readonly Dictionary<string, ChaseTextBuffer> fields = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private Task<Action>? operation;
    private GuiFrame previous = GuiFrame.Empty;
    private string? focus;
    private long revision;
    private bool disposed;
    private ChaseCharacter[] characters = [];
    private ChaseCharacter? character;
    private ChaseFrontendSelection? selection;
    private bool loadingGame;
    public ChaseFrontendScreen Screen { get; private set; } = ChaseFrontendScreen.Login;
    public string Status { get; private set; } = "Sign in to your LifeCare account.";
    public bool Busy => operation is not null || loadingGame;
    public bool ExitRequested { get; private set; }
    public GuiFrame Frame => previous;
    public ChaseFrontendSelection? Selection => selection;
    public ChaseClientPreferences Preferences => preferences;
    public bool SignedIn => api.Login is not null;

    public StormChaseFrontend()
    {
        layout = new(composer);
        api.Configure(preferences.AccountAddress);
        fields["username"] = new(preferences.Username, 32);
        fields["password"] = new("", 128);
        fields["character"] = new("", 32);
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
                Activate(focus is not null && !fields.ContainsKey(focus) && !focus.StartsWith("character/", StringComparison.Ordinal) ? focus : DefaultAction());
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
        // Authentication is the front door; nothing past it is reachable signed out.
        if (api.Login is null && action is not ("login" or "register" or "quit"))
        { Change(ChaseFrontendScreen.Login); Status = "Sign in to continue."; return; }
        switch (action)
        {
            case "login": SignIn(false); break;
            case "register": SignIn(true); break;
            case "new-character": Change(ChaseFrontendScreen.CreateCharacter); Status = "Choose a name for your new character."; break;
            case "create-character": CreateCharacter(); break;
            case "enter": EnterWorld(); break;
            case "settings": Change(ChaseFrontendScreen.Settings); break;
            case "quality":
                preferences.WeatherQuality = preferences.WeatherQuality == "High" ? "Ultra" : "High"; preferences.Save(); break;
            case "mute": preferences.Muted = !preferences.Muted; preferences.Save(); break;
            case "fullscreen": preferences.Fullscreen = !preferences.Fullscreen; preferences.Save(); break;
            case "sign-out": api.SignOut(); character = null; characters = []; fields["password"].Set(""); Change(ChaseFrontendScreen.Login); Status = "Sign in to your LifeCare account."; break;
            case "back": Back(); break;
            case "quit": ExitRequested = true; break;
            default:
                if (action.StartsWith("character/", StringComparison.Ordinal))
                {
                    var picked = characters.FirstOrDefault(item => item.Id == action[10..]);
                    // Clicking the already-selected character enters the world, like a double click.
                    if (picked is not null && picked.Id == character?.Id) EnterWorld();
                    else { character = picked; Status = character is null ? "Select a character." : character.Name + " is ready. Enter the world when you are."; }
                }
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
            await api.SignInAsync(username, password, register, lifetime.Token);
            var all = await api.CharactersAsync(lifetime.Token);
            return () => { preferences.Username = username; preferences.Save(); fields["password"].Set("");
                ShowCharacters(all, preferences.LastCharacterId); };
        });
    }
    /// <summary>An account with no characters goes straight to creation; otherwise to selection.</summary>
    private void ShowCharacters(ChaseCharacter[] all, string? preferredId)
    {
        characters = all;
        character = all.FirstOrDefault(item => item.Id == preferredId) ?? all.FirstOrDefault();
        if (all.Length == 0)
        { Change(ChaseFrontendScreen.CreateCharacter); Status = "Welcome to LifeCare. Create your first character to begin."; }
        else
        { Change(ChaseFrontendScreen.Characters); Status = character!.Name + " is ready. Enter the world when you are."; }
    }
    private void CreateCharacter()
    {
        string name = fields["character"].Value.Trim();
        if (name.Length < 2) { Status = "Give your character a name of at least 2 characters."; return; }
        Start(async () => { var created = await api.CreateCharacterAsync(name, lifetime.Token);
            var all = await api.CharactersAsync(lifetime.Token);
            return () => { fields["character"].Set(""); ShowCharacters(all, created.Id); }; });
    }
    /// <summary>Joins the world the Gateway hosts. Its directory addresses are already Gateway routes.</summary>
    private void EnterWorld()
    {
        if (character is not { } chosen) { Status = "Select a character."; return; }
        Start(async () =>
        {
            var worlds = await api.DirectoryAsync(lifetime.Token);
            var world = worlds.Where(server => server.Ready && !string.IsNullOrWhiteSpace(server.ApiAddress))
                .OrderBy(server => server.Players >= server.MaxPlayers).ThenByDescending(server => server.Players).FirstOrDefault()
                ?? throw new InvalidOperationException(WorldStarting);
            using var worldApi = new ChaseApiClient(); worldApi.Configure(world.ApiAddress);
            var uri = new Uri(worldApi.Address);
            if (!uri.IsLoopback && uri.Scheme != "https") throw new InvalidDataException("The world must be reached over HTTPS.");
            var server = await worldApi.ServerAsync(lifetime.Token);
            if (!server.Ready) throw new InvalidOperationException(WorldStarting);
            return () =>
            {
                preferences.LastCharacterId = chosen.Id; preferences.Save();
                // The short-lived admission ticket is requested only after world and avatar loading finish.
                selection = new(null, worldApi.Address, api.Login!.AccessToken, chosen, server, api.Address, api.Login.RefreshToken);
                Screen = ChaseFrontendScreen.Joining; Status = "Entering the world…";
            };
        });
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
    private const string WorldStarting = "The world is starting up. Try again in a moment.";
    public const string ReturnedToCharacters = "Returned to character select.";
    internal static string PlayerSafeFailure(Exception error) => error is InvalidOperationException && error.Message is
        "Sign-in failed or expired. Check your account and password." or
        "That account or character already exists." or
        "This account cannot access that character or action." or
        "Too many requests. Wait a moment and try again." or
        "Your login expired. Sign in again." or
        "Sign in to continue." or
        WorldStarting
            ? error.Message : "The service is unavailable. Please try again later.";
    private void Change(ChaseFrontendScreen screen)
    { Screen = screen; previous = GuiFrame.Empty; focus = screen switch { ChaseFrontendScreen.Login => "username", ChaseFrontendScreen.CreateCharacter => "character", ChaseFrontendScreen.Characters => "enter", _ => null }; }
    private void Back()
    {
        if (api.Login is null) return;
        if (Screen == ChaseFrontendScreen.Settings || Screen == ChaseFrontendScreen.CreateCharacter && characters.Length > 0)
            Change(ChaseFrontendScreen.Characters);
    }
    /// <summary>Leaving the world returns to character select (or login, if the session ended).</summary>
    public void ReturnToCharacterSelect(string status = "")
    {
        selection = null; loadingGame = false;
        if (api.Login is null) Change(ChaseFrontendScreen.Login);
        else Change(characters.Length == 0 ? ChaseFrontendScreen.CreateCharacter : ChaseFrontendScreen.Characters);
        if (string.IsNullOrWhiteSpace(status)) Status = "Ready when you are.";
        else if (status is ReturnedToCharacters or "Ready when you are." or "The service is unavailable. Please try again later.") Status = status;
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
        ReturnToCharacterSelect(PlayerSafeFailure(error));
    }
    private string DefaultAction() => Screen switch
    {
        ChaseFrontendScreen.Login => "login", ChaseFrontendScreen.CreateCharacter => "create-character",
        ChaseFrontendScreen.Characters => "enter", _ => "back"
    };

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
        switch (Screen)
        {
            case ChaseFrontendScreen.Login:
                Heading("Sign in", "One account for every character you play in the LifeCare world.");
                kit.Field("username", "Account name", fields["username"].Value, pad, y, inner, enabled: !Busy); y += 80;
                kit.Field("password", "Password", fields["password"].Value, pad, y, inner, password: true, enabled: !Busy); y += 88;
                kit.Button("login", "SIGN IN", pad, y, half, primary, enabled: !Busy);
                kit.Button("register", "CREATE ACCOUNT", pad + half + 12, y, half, enabled: !Busy); y += 58;
                kit.Button("quit", "QUIT", pad, y, inner, ghost, 40); y += 52;
                break;
            case ChaseFrontendScreen.Characters:
                Heading("Choose your character", "Your characters keep their homes, jobs, money and reputation between visits.");
                for (int i = 0; i < Math.Min(MaximumListedCharacters, characters.Length); i++)
                {
                    kit.Row("character/" + characters[i].Id, characters[i].Name, $"REPUTATION {characters[i].Reputation}", pad, y, inner, characters[i].Id == character?.Id);
                    y += 54;
                }
                y += 6;
                kit.Button("enter", character is null ? "ENTER WORLD" : "ENTER WORLD AS " + character.Name.ToUpperInvariant(), pad, y, inner, primary, 50,
                    enabled: character is not null && !Busy); y += 60;
                kit.Button("new-character", "NEW CHARACTER", pad, y, half, height: 42, enabled: !Busy);
                kit.Button("settings", "SETTINGS", pad + half + 12, y, half, height: 42, enabled: !Busy); y += 52;
                kit.Button("sign-out", "SIGN OUT", pad, y, half, ghost, 40, enabled: !Busy);
                kit.Button("quit", "QUIT", pad + half + 12, y, half, ghost, 40); y += 50;
                break;
            case ChaseFrontendScreen.CreateCharacter:
                Heading("Create your character", "Pick a name. It's how everyone else in the world will know you.");
                kit.Field("character", "Character name", fields["character"].Value, pad, y, inner, enabled: !Busy); y += 84;
                kit.Button("create-character", "CREATE CHARACTER", pad, y, inner, primary, 50, enabled: !Busy); y += 60;
                if (characters.Length > 0) kit.Button("back", "BACK TO CHARACTERS", pad, y, inner, ghost, 40);
                else
                {
                    kit.Button("sign-out", "SIGN OUT", pad, y, half, ghost, 40, enabled: !Busy);
                    kit.Button("quit", "QUIT", pad + half + 12, y, half, ghost, 40);
                }
                y += 50;
                break;
            case ChaseFrontendScreen.Settings:
                Heading("Settings", "Saved for this computer.");
                kit.Row("quality", "WEATHER QUALITY", preferences.WeatherQuality.ToUpperInvariant(), pad, y, inner, false, Accent); y += 54;
                kit.Text("High keeps the established quality and frame budget. Ultra samples storm clouds more finely.", pad, y, inner, 9.5f, Muted, wrap: true, lines: 2); y += 44;
                kit.Row("mute", "WEATHER AUDIO", preferences.Muted ? "OFF" : "ON", pad, y, inner, false, preferences.Muted ? Muted : Accent); y += 54;
                kit.Row("fullscreen", "START IN FULLSCREEN", preferences.Fullscreen ? "ON" : "OFF", pad, y, inner, false, preferences.Fullscreen ? Accent : Muted); y += 62;
                kit.Text("WASD move or drive  ·  click to look  ·  E vehicle  ·  P phone  ·  F photo  ·  F11 fullscreen", pad, y, inner, 9, Muted, wrap: true, lines: 2); y += 46;
                kit.Button("back", "SAVE AND RETURN", pad, y, inner, primary); y += 58;
                break;
            case ChaseFrontendScreen.Joining:
                Heading("Entering the world", (character?.Name ?? "Your character") + " is on the way. Loading the world and everyone in it…");
                y += 40;
                break;
        }
        var statusColor = Busy ? Accent : Muted;
        content.Add(ChaseMenuKit.Fill(pad * k, (y + 8) * k, 8 * k, 8 * k, statusColor, 4 * k));
        kit.Text(Busy && Screen != ChaseFrontendScreen.Joining ? "Connecting…" : Status, pad + 18, y, inner - 18, 9.5f, statusColor, wrap: true, lines: 2);
        y += 38;
        float cardHeight = y + pad - 14;
        float cardX = brand ? W - Math.Max(56, W * .07f) - cardWidth : (W - cardWidth) / 2;
        float cardY = Math.Max(16, (H - cardHeight) / 2);

        var root = new List<GuiElement>(ChaseMenuKit.Backdrop(width, height, brand));
        var outer = new ChaseMenuKit(k, root);
        if (brand)
        {
            float bx = Math.Max(56, W * .07f), by = H * .30f;
            outer.Text("LIFECARE", bx, by, 620, 46, Ink, bold: true);
            outer.Text("ONLINE", bx + 2, by + 66, 620, 20, Accent, bold: true);
            root.Add(ChaseMenuKit.Fill(bx * k, (by + 104) * k, 64 * k, 4 * k, Accent, 2 * k));
            outer.Text("THE LIFECARE MMO", bx, by + 122, 520, 11, Accent, bold: true);
            outer.Text("Make a home, take a job and ride out the weather together in one shared, persistent world.",
                bx, by + 150, 440, 12, Muted, wrap: true, lines: 3);
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
        lifetime.Dispose(); composer.Dispose(); fields["password"].Set("");
    }
}
