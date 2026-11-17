using System.Diagnostics;
using Engine.GUI;

/// <summary>
/// The bottom dock, after Life Beyond v2's: every navigation button lives here. Clicking the header tab opens or hides
/// it with an eased slide, and the hotbar, status line and any window near the bottom are pushed up to stay above it.
/// Buttons light up for the panels that are open, so the dock doubles as the panel switchboard.
/// </summary>
internal sealed partial class StormChaseHud
{
    private const float DockHeader = 26, DockBody = 78, DockButtonWidth = 100, DockButtonGap = 8;
    private readonly Stopwatch dockClock = Stopwatch.StartNew();
    private double dockLastSeconds;
    private float dockProgress;

    public bool DockOpen { get; set; }
    public bool CharacterOpen { get; set; } = true;
    public bool TaskOpen { get; set; } = true;
    public bool NotesOpen { get; set; } = true;
    public bool MinimapOpen { get; set; } = true;

    private static readonly (string Id, string Label, string Key)[] DockButtons =
    [
        ("hud/character", "Character", ""), ("hud/task", "Task", ""), ("hud/notes", "Alerts", ""), ("hud/chat", "Chat", "Enter"),
        ("hud/minimap", "Minimap", "[ ]"), ("hud/map", "World map", "N"), ("hud/inventory", "Bag", "I"), ("hud/phone", "Phone", "P"),
        ("hud/jobs", "Jobs", "O"), ("hud/build", "Build", "Y"), ("hud/shop", "Shop", "U"), ("hud/home", "Home", ""), ("hud/menu", "Menu", "Esc"),
    ];

    /// <summary>How much of the screen bottom the dock currently takes, in design units; advances the slide each frame.</summary>
    private float DockReserve()
    {
        double now = dockClock.Elapsed.TotalSeconds; float delta = (float)Math.Clamp(now - dockLastSeconds, 0, .1); dockLastSeconds = now;
        dockProgress = Math.Clamp(dockProgress + (DockOpen ? delta : -delta) / .22f, 0, 1);
        float eased = dockProgress * dockProgress * (3 - 2 * dockProgress);
        return DockHeader + DockBody * eased;
    }

    /// <summary>Finishes the dock's slide at once (still captures).</summary>
    public void SnapDock() { dockProgress = DockOpen ? 1 : 0; dockLastSeconds = dockClock.Elapsed.TotalSeconds; }

    private bool DockLit(string id) => id switch
    {
        "hud/character" => CharacterOpen, "hud/task" => TaskOpen, "hud/notes" => NotesOpen, "hud/chat" => ChatOpen, "hud/minimap" => MinimapOpen,
        "hud/map" => MapOpen, "hud/inventory" => InventoryOpen, "hud/phone" => PhoneOpen, _ => false,
    };

    private float DockButtonsLeft(float width) => (width - (DockButtons.Length * (DockButtonWidth + DockButtonGap) - DockButtonGap)) / 2;

    private void BuildDock(List<GuiElement> root, HudLayout L, HudKit k)
    {
        var dock = L.Dock;
        var tab = new GuiRect(L.Width / 2 - 170, dock.Y, 340, DockHeader);
        if (dock.Height > DockHeader + 1)
        {
            var body = new GuiRect(8, dock.Y + DockHeader - 4, L.Width - 16, DockBody + 20);
            root.Add(k.Fill(body, Theme.Glass, 14, Theme.GlassEdge));
            float x = DockButtonsLeft(L.Width), y = body.Y + 12;
            foreach (var (id, label, key) in DockButtons)
            {
                var r = new GuiRect(x, y, DockButtonWidth, 54); bool lit = DockLit(id);
                root.Add(new GuiButton { Id = id, Text = "", Translation = new(r.X * k.Scale, r.Y * k.Scale), Width = GuiLength.Pixels(r.Width * k.Scale), Height = GuiLength.Pixels(r.Height * k.Scale),
                    CornerRadius = 9 * k.Scale, BackgroundColor = lit ? new(26, 92, 140, 250) : Theme.Tile, HoverBackgroundColor = new(34, 104, 156, 250),
                    PressedBackgroundColor = new(10, 40, 70, 255), BorderColor = lit ? Theme.Accent : Theme.TileEdge, BorderThickness = Math.Max(1, k.Scale) });
                if (lit) root.Add(k.Fill(new(r.X + 30, r.Bottom - 4, r.Width - 60, 3), Theme.Accent, 1.5f));
                root.Add(k.Label(r, label, 0, 33, r.Width, 8, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
                if (key.Length > 0) root.Add(k.Label(r, key, r.Width - 46, 3, 40, 6.5f, Theme.Sky, align: GuiTextAlignment.Right));
                x += DockButtonWidth + DockButtonGap;
            }
        }
        root.Add(k.Fill(tab, Theme.Header, 12, Theme.GlassEdge));
        root.Add(k.Label(tab, DockOpen ? "MAIN PANELS  ·  CLICK TO HIDE" : "MAIN PANELS  ·  CLICK TO OPEN", 0, 6, tab.Width, 7.5f, Theme.Muted, bold: true, align: GuiTextAlignment.Center));
        root.Add(new GuiButton { Id = "dock/toggle", Text = "", Translation = new(tab.X * k.Scale, tab.Y * k.Scale), Width = GuiLength.Pixels(tab.Width * k.Scale),
            Height = GuiLength.Pixels(tab.Height * k.Scale), CornerRadius = 12 * k.Scale, BackgroundColor = new(0, 0, 0, 0), HoverBackgroundColor = new(255, 255, 255, 16),
            PressedBackgroundColor = new(255, 255, 255, 30) });
    }

    private void DrawDockInstruments(GuiDx12MeshBuilder mesh, HudLayout L)
    {
        float s = L.Scale, cx = L.Width / 2 + 150, cy = L.Dock.Y + DockHeader / 2;
        // Chevron on the tab: up to open, down to hide.
        float d = DockOpen ? 1 : -1;
        mesh.Line((cx - 6) * s, (cy - 2 * d) * s, cx * s, (cy + 3 * d) * s, Theme.Accent, 2 * s);
        mesh.Line(cx * s, (cy + 3 * d) * s, (cx + 6) * s, (cy - 2 * d) * s, Theme.Accent, 2 * s);
        if (L.Dock.Height <= DockHeader + 1) return;
        float x = DockButtonsLeft(L.Width), y = L.Dock.Y + DockHeader - 4 + 12;
        foreach (var (id, _, _) in DockButtons) { DrawIcon(mesh, id, (x + DockButtonWidth / 2) * s, (y + 18) * s, 10 * s, s); x += DockButtonWidth + DockButtonGap; }
    }

    private bool HandleDockAction(string action, out string? request)
    {
        request = null;
        switch (action)
        {
            case "dock/toggle": DockOpen = !DockOpen; return true;
            case "hud/character": CharacterOpen = !CharacterOpen; return true;
            case "hud/task": TaskOpen = !TaskOpen; return true;
            case "hud/notes": NotesOpen = !NotesOpen; return true;
            case "hud/minimap": MinimapOpen = !MinimapOpen; return true;
            case "hud/home": request = "home"; return true;
            default: return false;
        }
    }
}
