using System.Text.Json;
using Engine.GUI;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Shared.Contracts.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    private static async Task<int> VerifyStormChaseFrontendAsync(RendererOptions options, NativeSurface surface,
        MutableRenderAssetCatalog catalog, CaptureLogger logger, RenderGpuDebugValidation validation, string output, string staging)
    {
        var checks = new List<object>(); bool passed = true;
        void Check(bool success, string name) { checks.Add(new { Name = name, Passed = success }); passed &= success; }
        using var profile = new FrontendVerificationProfile(output);
        ChaseClientPreferences migrated;
        string deploymentEndpoint;
        try
        {
            Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API", null);
            deploymentEndpoint = ChaseClientConfiguration.AccountAddress;
            migrated = ChaseClientPreferences.Load();
            using var deploymentLocal = new LocalChaseServer();
            using var deploymentFrontend = new StormChaseFrontend(deploymentLocal);
            deploymentFrontend.Update(1280, 720, GuiInputState.Empty, [], 0);
            Check(deploymentFrontend.Preferences.AccountAddress == deploymentEndpoint &&
                    deploymentFrontend.Frame.Elements.Where(element => element.IsInteractive).Select(element => element.ElementId).ToHashSet()
                        .SetEquals(new[] { "username", "password", "login", "register", "quit" }) &&
                    !deploymentFrontend.Frame.DrawCommands.OfType<GuiTextCommand>().Any(command =>
                        command.Text.Contains(deploymentEndpoint, StringComparison.OrdinalIgnoreCase) ||
                        command.Text.Contains(FrontendVerificationProfile.LegacyAccountAddress, StringComparison.OrdinalIgnoreCase)),
                "Packaged account endpoint stays private in login after migration from player-editable settings");
        }
        finally { Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API", FrontendVerificationProfile.AccountAddress); }
        Check(migrated.AccountAddress == deploymentEndpoint && migrated.AccountAddress != FrontendVerificationProfile.LegacyAccountAddress,
            "Packaged deployment configuration replaces a stale player-saved account endpoint before credentials can be routed");
        Check(migrated.LastServer == "http://127.0.0.1:5188" && migrated.Servers.SequenceEqual(new[] { "http://127.0.0.1:5188" }) &&
                migrated.WeatherQuality == "Ultra" && migrated.Muted && !migrated.Fullscreen && migrated.Username == "",
            "Account endpoint migration preserves saved game server, quality, sound and player preferences");
        using var local = new LocalChaseServer(); using var frontend = new StormChaseFrontend(local);
        Check(frontend.Preferences.AccountAddress == FrontendVerificationProfile.AccountAddress,
            "Explicit isolated operator override takes precedence over packaged configuration and stale saved settings");
        using var api = new ChaseApiClient();
        api.Configure("http://127.0.0.1:5080/"); Check(api.Address == "http://127.0.0.1:5080", "Local API address normalizes without storing credentials");
        bool rejected = false; try { api.Configure("https://user:password@example.com"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Embedded URL credentials rejected");
        api.Configure("http://example.com"); rejected = false;
        try { await api.SignInAsync("player", "not-a-real-password", false, default); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Remote credentials require HTTPS before any HTTP request");
        var buffer = new ChaseTextBuffer("abcd", 4); buffer.Apply(new(0x25, '\0', false)); buffer.Apply(new(0, '\b', false));
        Check(buffer.Value == "abd" && buffer.Cursor == 2, "Left and backspace edit at caret");
        buffer.Apply(new(0, 'X', false)); buffer.Apply(new(0, 'Y', false)); Check(buffer.Value == "abXd", "Character length bound does not overwrite existing text");
        buffer.Apply(new(0x24, '\0', false)); buffer.Apply(new(0x2E, '\0', false)); Check(buffer.Value == "bXd", "Home and Delete use game text buffer");
        buffer.Apply(new(0, '\n', false)); Check(buffer.Value == "bXd", "Text fields reject control characters");

        var ui = frontend.Update(1280, 720, GuiInputState.Empty, [], 0);
        Check(frontend.Screen == ChaseFrontendScreen.Login && !frontend.SignedIn, "Client starts at account login");
        Check(!frontend.Frame.Elements.Any(element => element.ElementId is "offline" or "host" or "browser" or "menu"),
            "Login contains no single-player, hosting or multiplayer bypass");
        Check(frontend.Frame.Elements.Where(element => element.IsInteractive).Select(element => element.ElementId).ToHashSet()
                .SetEquals(new[] { "username", "password", "login", "register", "quit" }),
            "Login exposes only account name, password, sign-in, registration and quit");
        var loginText = frontend.Frame.DrawCommands.OfType<GuiTextCommand>().Select(command => command.Text).ToArray();
        Check(!loginText.Any(text => text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("https://", StringComparison.OrdinalIgnoreCase) || text.Contains("dev.redirectme.net", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Account service", StringComparison.OrdinalIgnoreCase)),
            "Login draw commands contain no account endpoint or address setup label");
        string genericFailure = "The service is unavailable. Please try again later.";
        Check(StormChaseFrontend.PlayerSafeFailure(new HttpRequestException("Could not connect to https://dev.redirectme.net/api/server")) == genericFailure &&
              StormChaseFrontend.PlayerSafeFailure(new InvalidDataException("Invalid metadata from https://dev.redirectme.net")) == genericFailure &&
              StormChaseFrontend.PlayerSafeFailure(new TaskCanceledException("Request to https://dev.redirectme.net timed out")) == genericFailure,
            "Transport, metadata and timeout errors hide infrastructure details from players");
        Check(StormChaseFrontend.PlayerSafeFailure(new InvalidOperationException("Sign-in failed or expired. Check your account and password.")) ==
                "Sign-in failed or expired. Check your account and password." &&
              StormChaseFrontend.PlayerSafeFailure(new InvalidOperationException("That account or character already exists.")) ==
                "That account or character already exists.", "Controlled account errors remain actionable");
        frontend.JoinFailed("Server metadata at https://dev.redirectme.net is unavailable");
        frontend.Update(1280, 720, GuiInputState.Empty, [], .005);
        Check(frontend.Status == genericFailure && !frontend.Frame.DrawCommands.OfType<GuiTextCommand>()
                .Any(command => command.Text.Contains("dev.redirectme.net", StringComparison.OrdinalIgnoreCase)),
            "Join failure displays a generic message in the native login UI");
        frontend.ReturnToMenu("Native transport disconnected from https://dev.redirectme.net with private metadata");
        frontend.Update(1280, 720, GuiInputState.Empty, [], .006);
        Check(frontend.Status == genericFailure && !frontend.Frame.DrawCommands.OfType<GuiTextCommand>()
                .Any(command => command.Text.Contains("dev.redirectme.net", StringComparison.OrdinalIgnoreCase)),
            "Active-session disconnects cannot leak transport details through the returned menu");
        frontend.ReturnToMenu("Returned to the main menu.");
        Check(frontend.Status == "Returned to the main menu.", "Normal leave feedback survives status privacy filtering");
        frontend.JoinFailed("Server metadata at https://dev.redirectme.net is unavailable");
        frontend.Update(1280, 720, GuiInputState.Empty, [new(0x1B, '\0', false)], .01);
        Check(frontend.Screen == ChaseFrontendScreen.Login && frontend.Selection is null, "Escape cannot bypass authentication into the main menu");
        var password = frontend.Frame.Elements.Single(element => element.ElementId == "password").Bounds;
        frontend.Update(1280, 720, new(new(true, password.X + 15, password.Y + 20, true, false)), [], .1);
        frontend.Update(1280, 720, new(new(true, password.X + 15, password.Y + 20, false, true)),
            "secret-field-123".Select(character => new GuiKeyboardEvent(0, character, false)).ToArray(), .2);
        var visibleText = frontend.Frame.DrawCommands.OfType<GuiTextCommand>().Select(command => command.Text).ToArray();
        Check(visibleText.Contains(new string('*', 16)) && !visibleText.Any(text => text.Contains("secret-field-123", StringComparison.Ordinal)),
            "Password is masked in native UI draw commands");
        ui = frontend.Update(640, 360, GuiInputState.Empty, [], 1);
        Check(ui.Width >= 960 && ui.Height >= 720, "Small windows scale a complete logical menu instead of clipping controls");
        Check(frontend.Frame.Elements.Where(element => element.IsInteractive).All(element => element.Bounds.Bottom <= ui.Height), "Every login control fits scaled viewport");

        await using var backend = new Dx12RenderBackend(options, surface, catalog, logger);
        await backend.InitializeAsync(default);
        var captures = new List<object>(); long number = 0;
        foreach (var screen in Enum.GetValues<ChaseFrontendScreen>().Where(screen => screen != ChaseFrontendScreen.Joining))
        {
            frontend.ShowForVerification(screen);
            ui = frontend.Update(surface.Width, surface.Height, GuiInputState.Empty, [], 2);
            if (screen == ChaseFrontendScreen.Menu)
            {
                var menuText = frontend.Frame.DrawCommands.OfType<GuiTextCommand>().Select(command => command.Text).ToArray();
                Check(menuText.Contains("SINGLE PLAYER") && menuText.Contains("HOST A SERVER") && menuText.Contains("JOIN MULTIPLAYER"),
                    "Authenticated main menu presents Tiny Clinic style game-mode flow");
            }
            Check(ui.Vertices.Count > 0 && frontend.Frame.Elements.Count > 3, screen + " builds native game controls");
            var frame = CreateFrontendBackground(number, surface.Width, surface.Height, 40) with { Ui = ui };
            ExportCompleted.SetValue(backend, false);
            for (int warm = 0; warm < 32 && !(bool)ExportCompleted.GetValue(backend)!; warm++)
                await backend.RenderAsync(frame with { FrameNumber = number++ }, default);
            Check((bool)ExportCompleted.GetValue(backend)!, screen + " exports UI in actual DX12 frame");
            if (!(bool)ExportCompleted.GetValue(backend)!) throw new InvalidOperationException("The native UI capture did not reach resident export frames: " + screen);
            string imagePath = Path.Combine(output, "frontend-" + screen.ToString().ToLowerInvariant() + ".png"); File.Copy(staging, imagePath, true);
            using var image = Image.Load<Rgba32>(imagePath); int cyan = 0, dark = 0, sampled = 0;
            // The page card is located from its actual layout bounds: its header strip must be the
            // dark card surface, and the card must carry accent (primary action / status) pixels.
            var card = frontend.Frame.Elements.Single(element => element.ElementId == "menu-card").Bounds;
            int cardX = (int)card.X, cardY = (int)card.Y, cardRight = (int)card.Right, cardBottom = (int)card.Bottom;
            image.ProcessPixelRows(rows => {
                for (int y = cardY; y < Math.Min(image.Height, cardBottom); y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = cardX + 28; x < Math.Min(image.Width, cardRight - 28); x++)
                    {
                        var pixel = row[x];
                        if (y >= cardY + 4 && y < cardY + 30) { sampled++; if (pixel.R < 45 && pixel.G < 65 && pixel.B < 75) dark++; }
                        if (pixel.G > 120 && pixel.B > 90 && pixel.G > pixel.R * 1.4f) cyan++;
                    }
                }
            });
            Check(cyan > 500, screen + " native capture contains authored UI accent pixels");
            Check(dark > sampled * .7, screen + " authored header panel is visible in rendered pixels");
            captures.Add(new { Screen = screen.ToString(), Path = imagePath, NativeDx12 = true, CyanPixels = cyan, VertexCount = ui.Vertices.Count });
        }
        {
            // Pause menu shares the same card style; capture it beside the account pages.
            var pause = new ChasePauseMenu();
            ui = pause.Update(surface.Width, surface.Height, GuiInputState.Empty, [], "Verification chaser", false);
            var pauseFrame = CreateFrontendBackground(number, surface.Width, surface.Height, 40) with { Ui = ui };
            ExportCompleted.SetValue(backend, false);
            for (int warm = 0; warm < 32 && !(bool)ExportCompleted.GetValue(backend)!; warm++)
                await backend.RenderAsync(pauseFrame with { FrameNumber = number++ }, default);
            Check((bool)ExportCompleted.GetValue(backend)! && ui.Vertices.Count > 0, "Pause menu exports UI in actual DX12 frame");
            File.Copy(staging, Path.Combine(output, "frontend-pause.png"), true);
        }
        validation.CollectErrors(backend, "frontend capture", true);
        string report = Path.Combine(output, "frontend-check.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed,
            Scope = "Native fixture UI pages and edit/security primitives; actual authenticated gameplay is verified separately by the HWND flow script",
            IsolatedProfile = profile.DataRoot, AccountEndpointConfiguredPrivately = true, Checks = checks, Captures = captures }, JsonOptions));
        Console.WriteLine("Frontend UI checks " + (passed ? "passed" : "FAILED") + ": " + checks.Count + ". Report: " + report);
        return passed ? 0 : 1;
    }

    private sealed class FrontendVerificationProfile : IDisposable
    {
        public const string AccountAddress = "http://127.0.0.1:5080";
        public const string LegacyAccountAddress = "https://legacy-accounts.example.invalid";
        private readonly string? previousRoot = Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_DATA_ROOT");
        private readonly string? previousEndpoint = Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API");
        public string DataRoot { get; }
        public FrontendVerificationProfile(string output)
        {
            DataRoot = Path.Combine(Path.GetFullPath(output), "isolated-frontend-profile-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_DATA_ROOT", DataRoot);
            Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API", AccountAddress);
            try
            {
                new ChaseClientPreferences { AccountAddress = LegacyAccountAddress, LastServer = "http://127.0.0.1:5188",
                    Username = "", Servers = ["http://127.0.0.1:5188"], WeatherQuality = "Ultra", Muted = true, Fullscreen = false }.Save();
            }
            catch
            {
                Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_DATA_ROOT", previousRoot);
                Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API", previousEndpoint);
                throw;
            }
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_DATA_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API", previousEndpoint);
        }
    }

    private static RenderFrame CreateFrontendBackground(long number, int width, int height, float seconds)
    {
        var frame = CreateFrame(new("frontend", number, seconds, 3, 76, 140, 240, .12f, true, true), width, height, false);
        return frame with { Items = frame.Items.Take(1).ToArray(), Lights = [], AnimationTimeSeconds = seconds };
    }
}
