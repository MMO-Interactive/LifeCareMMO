using System.Text.Json;
using Engine.GUI;
using StormChase.Protocol;

/// <summary>A chat line as the HUD shows it.</summary>
internal sealed record HudChatLine(string Time, ChatChannel Channel, string From, string Text, bool System);

/// <summary>One item tile in the inventory window.</summary>
internal sealed record HudItem(string Id, string Name, string Detail, string Kind);

/// <summary>What the avatar has on them: the Bag window. Containers in the world are separate.</summary>
internal sealed record HudInventory(string Carrying, IReadOnlyList<HudItem> Items);

/// <summary>A container opened in the world (a delivery crate, a cabinet, a car trunk…): its own window and contents.</summary>
internal sealed record HudContainer(string Id, string Title, string Subtitle, IReadOnlyList<HudItem> Items, string? TakeVerb = "TAKE");

/// <summary>
/// HUD windows: the panels the player can drag by their title bar (character, task, notifications, minimap, chat,
/// inventory, phone, vehicle) and the two that open and close (chat, inventory). Positions persist per player in
/// hud-layout.json. Static elements (compass, clock, icon tiles, status pill, hotbar) never move.
/// </summary>
internal sealed partial class StormChaseHud
{
    public const float TitleBarHeight = 30;
    private readonly Dictionary<string, (float X, float Y)> windowOffsets = LoadLayout();
    private readonly List<(string Id, GuiRect Rect)> dragHandles = [];
    private (string Id, float GrabX, float GrabY, float StartX, float StartY)? dragging;
    private readonly ChaseTextBuffer chatInput = new("", StormChase.Protocol.ProtocolCodec.ChatLimit);
    private readonly List<HudChatLine> chatLines = [];

    /// <summary>Alt toggles a free cursor for dragging windows and clicking the HUD.</summary>
    public bool CursorMode { get; set; }
    public bool ChatOpen { get; set; } = true;
    public bool ChatTyping { get; private set; }
    public ChatChannel ChatChannel { get; set; } = ChatChannel.Local;
    /// <summary>Which tab the chat shows: null is all channels.</summary>
    public ChatChannel? ChatFilter { get; set; }
    /// <summary>Sends a typed line; returns false if it could not be queued.</summary>
    public Func<ChatChannel, string, bool>? SendChat { get; set; }
    public bool InventoryOpen { get; set; }
    public HudInventory? Inventory { get; set; }
    public int InventorySelection { get; set; } = -1;
    /// <summary>True while any window or text box needs the cursor and keyboard.</summary>
    public bool WantsCursor => CursorMode || ChatTyping || InventoryOpen || Container is not null || PhoneOpen || MapOpen || dragging is not null;

    private static string LayoutPath => Path.Combine(ChaseClientPreferences.DataRoot, "hud-layout.json");

    private static Dictionary<string, (float X, float Y)> LoadLayout()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(LayoutPath)) ?? [];
            return saved.Where(p => p.Value is { Length: 2 } v && float.IsFinite(v[0]) && float.IsFinite(v[1])).ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1]));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(ChaseClientPreferences.DataRoot);
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(windowOffsets.ToDictionary(p => p.Key, p => new[] { p.Value.X, p.Value.Y })));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Puts every window back where it started.</summary>
    public void ResetLayout() { windowOffsets.Clear(); SaveLayout(); }

    /// <summary>A window's rectangle after the player's drag, kept fully on screen; its title bar becomes a drag handle.</summary>
    private GuiRect Place(string id, GuiRect home, float W, float H)
    {
        var (dx, dy) = windowOffsets.GetValueOrDefault(id);
        var r = new GuiRect(Math.Clamp(home.X + dx, 0, Math.Max(0, W - home.Width)), Math.Clamp(home.Y + dy, 0, Math.Max(0, H - home.Height)), home.Width, home.Height);
        return r;
    }

    private void RegisterHandle(string id, GuiRect r) => dragHandles.Add((id, new(r.X, r.Y, r.Width - 44, TitleBarHeight)));

    /// <summary>Starts, continues or ends a title-bar drag. Runs before the document is built, with last frame's handles.</summary>
    private void ProcessDrag(GuiInputState input, float s)
    {
        var p = input.Pointer;
        if (!p.IsPresent) { if (dragging is not null) { dragging = null; SaveLayout(); } return; }
        float x = p.X / s, y = p.Y / s;
        if (dragging is { } d)
        {
            if (!p.IsPrimaryButtonDown) { dragging = null; SaveLayout(); return; }
            windowOffsets[d.Id] = (d.StartX + x - d.GrabX, d.StartY + y - d.GrabY);
            return;
        }
        if (!p.IsPrimaryButtonDown || p.WasPrimaryButtonDown) return;
        // Topmost first: handles were registered in draw order.
        for (int i = dragHandles.Count - 1; i >= 0; i--)
        {
            var (id, r) = dragHandles[i];
            if (x < r.X || x > r.X + r.Width || y < r.Y || y > r.Y + r.Height) continue;
            var (ox, oy) = windowOffsets.GetValueOrDefault(id);
            dragging = (id, x, y, ox, oy);
            return;
        }
    }

    // ==================================================================== chat
    public void AddChat(HudChatLine line)
    {
        chatLines.Add(line);
        if (chatLines.Count > 200) chatLines.RemoveAt(0);
    }

    /// <summary>Opens the chat with the text box focused (Enter).</summary>
    public void BeginChat() { ChatOpen = true; ChatTyping = true; }

    /// <summary>Keyboard while typing: characters edit, Enter sends, Tab switches channel, Esc stops typing.</summary>
    public void ChatKey(GuiKeyboardEvent key)
    {
        switch (key.Key)
        {
            case 0x1B: ChatTyping = false; return;
            case 0x09: ChatChannel = ChatChannel == ChatChannel.Local ? ChatChannel.World : ChatChannel.Local; return;
            case 0x0D:
                string text = chatInput.Value.Trim();
                if (text.Length > 0 && SendChat?.Invoke(ChatChannel, text) != false) chatInput.Set("");
                ChatTyping = false; return;
        }
        chatInput.Apply(key);
    }

    private void BuildChat(List<GuiElement> root, HudKit k, GuiRect r)
    {
        WindowFrame(root, k, r, "CHAT", "chat/close", Theme.Sky);
        string[] tabs = ["All", "Local", "World"];
        for (int i = 0; i < tabs.Length; i++)
        {
            ChatChannel? filter = i == 0 ? null : (ChatChannel)(i - 1);
            root.Add(k.Button("chat/tab/" + tabs[i].ToLowerInvariant(), tabs[i], new(r.X + 12 + i * 70, r.Y + 38, 64, 24), ChatFilter == filter, 8.5f));
        }
        var log = new GuiRect(r.X + 12, r.Y + 68, r.Width - 24, r.Height - 68 - 46);
        root.Add(k.Fill(log, new(0, 0, 0, 60), 8));
        float bottom = log.Height - 6;
        var style = new GuiTextStyle(Theme.Font, 8.5f, 1.18f, Theme.Ink);
        foreach (var line in Enumerable.Reverse(chatLines).Where(l => ChatFilter is null || l.Channel == ChatFilter || l.System))
        {
            string text = line.System ? $"{line.Time}  {line.Text}" : $"{line.Time}  {line.From}:  {line.Text}";
            float height = composer.Measure(text, style, new(log.Width - 16, float.PositiveInfinity), GuiTextWrapMode.Wrap).Height + 3;
            if (bottom - height < 4) break;
            bottom -= height;
            var color = line.System ? Theme.Accent : line.Channel == ChatChannel.World ? Theme.Warn : Theme.Ink;
            root.Add(k.Wrapped(log, text, 8, bottom, log.Width - 16, height, 8.5f, color));
        }
        if (chatLines.Count == 0) root.Add(k.Label(log, "No messages yet. Press Enter to talk to people nearby.", 8, 8, log.Width - 16, 8.5f, Theme.Faint));
        var box = new GuiRect(r.X + 82, r.Bottom - 38, r.Width - 82 - 80, 28);
        root.Add(k.Button("chat/channel", ChatChannel == ChatChannel.Local ? "LOCAL" : "WORLD", new(r.X + 12, r.Bottom - 38, 64, 28), size: 8));
        root.Add(new GuiButton { Id = "chat/input", Text = "", Translation = new(box.X * k.Scale, box.Y * k.Scale), Width = GuiLength.Pixels(box.Width * k.Scale),
            Height = GuiLength.Pixels(box.Height * k.Scale), CornerRadius = 7 * k.Scale, BackgroundColor = new(4, 12, 20, 240), HoverBackgroundColor = new(8, 22, 34, 245),
            PressedBackgroundColor = new(8, 22, 34, 255), BorderColor = ChatTyping ? Theme.Accent : Theme.GlassEdge, BorderThickness = Math.Max(1, k.Scale) });
        string shown = ChatTyping ? chatInput.Value + "|" : chatInput.Value.Length > 0 ? chatInput.Value : "Press Enter to chat…";
        root.Add(k.Label(box, shown, 10, 7, box.Width - 20, 8.5f, ChatTyping || chatInput.Value.Length > 0 ? Theme.Ink : Theme.Faint));
        root.Add(k.Button("chat/send", "SEND", new(r.Right - 74, r.Bottom - 38, 62, 28), size: 8.5f));
    }

    // ==================================================================== inventory (on the player) and containers (in the world)
    /// <summary>One grid of item tiles; <paramref name="prefix"/> names the buttons (inventory/item/n, container/item/n).</summary>
    private float ItemGrid(List<GuiElement> root, HudKit k, GuiRect r, float y, IReadOnlyList<HudItem> items, string prefix, int selection, string empty)
    {
        if (items.Count == 0) { root.Add(k.Label(r, empty, 14, y, r.Width - 28, 8.5f, Theme.Faint)); return y + 26; }
        for (int i = 0; i < items.Count; i++)
        {
            var tile = new GuiRect(r.X + 14 + i % 6 * 66, r.Y + y + i / 6 * 70, 60, 64);
            bool selected = i == selection;
            root.Add(new GuiButton { Id = prefix + i, Text = "", Translation = new(tile.X * k.Scale, tile.Y * k.Scale), Width = GuiLength.Pixels(tile.Width * k.Scale),
                Height = GuiLength.Pixels(tile.Height * k.Scale), CornerRadius = 9 * k.Scale, BackgroundColor = selected ? new(26, 92, 140, 250) : Theme.Tile,
                HoverBackgroundColor = new(34, 104, 156, 250), PressedBackgroundColor = new(10, 40, 70, 255), BorderColor = selected ? Theme.Accent : Theme.TileEdge,
                BorderThickness = Math.Max(1, (selected ? 2 : 1) * k.Scale) });
            root.Add(k.Wrapped(tile, items[i].Name.ToUpperInvariant(), 3, 22, 54, 34, 6.5f, Theme.Ink, GuiTextAlignment.Center));
        }
        return y + (items.Count + 5) / 6 * 70 + 8;
    }

    private void ItemDetail(List<GuiElement> root, HudKit k, GuiRect r, HudItem? item, string useId, string? verb, string hint)
    {
        var detail = new GuiRect(r.X + 12, r.Bottom - 74, r.Width - 24, 62);
        root.Add(k.Fill(detail, new(255, 255, 255, 10), 8));
        if (item is null) { root.Add(k.Label(detail, hint, 10, 22, detail.Width - 20, 9, Theme.Faint)); return; }
        root.Add(k.Label(detail, item.Name, 10, 6, detail.Width - 150, 11, Theme.Ink, bold: true));
        root.Add(k.Label(detail, item.Detail, 10, 30, detail.Width - 150, 8.5f, Theme.Muted));
        if (verb is not null) root.Add(k.Button(useId, verb, new(detail.Right - 130, detail.Y + 16, 118, 30), true, 9));
    }

    /// <summary>The Bag: only what the avatar has on them. Everything else lives in containers in the world.</summary>
    private void BuildInventory(List<GuiElement> root, HudKit k, GuiRect r, StormChaseTelemetry t)
    {
        WindowFrame(root, k, r, "BAG", "inventory/close", Theme.Warn, t.Campaign is { } c ? $"${c.Cash:#,0}" : null);
        var inv = Inventory ?? new HudInventory("Empty hands", []);
        root.Add(k.Label(r, "IN HAND", 14, 40, 120, 7.5f, Theme.Faint));
        root.Add(k.Label(r, inv.Carrying, 14, 54, r.Width - 28, 11, Theme.Ink, bold: true));
        root.Add(k.Label(r, $"ON YOU  ·  {inv.Items.Count}", 14, 82, 300, 8, Theme.Warn, bold: true));
        ItemGrid(root, k, r, 102, inv.Items, "inventory/item/", InventorySelection, "Your pockets are empty.");
        var item = inv.Items.ElementAtOrDefault(InventorySelection);
        ItemDetail(root, k, r, item, "inventory/use", item?.Kind == "phone" ? "OPEN" : null, "Select an item. Containers in the world open their own window.");
    }

    /// <summary>A container in the world, opened by interacting with it. Each container gets its own window.</summary>
    public HudContainer? Container { get; private set; }
    public int ContainerSelection { get; set; } = -1;
    public void OpenContainer(HudContainer container) { Container = container; ContainerSelection = -1; }
    /// <summary>Refreshes the open container's contents without closing it.</summary>
    public void UpdateContainer(HudContainer container) { if (Container?.Id == container.Id) Container = container; }

    private void BuildContainer(List<GuiElement> root, HudKit k, GuiRect r, HudContainer box)
    {
        WindowFrame(root, k, r, box.Title.ToUpperInvariant(), "container/close", Theme.Sky);
        root.Add(k.Label(r, box.Subtitle, 14, 40, r.Width - 28, 8.5f, Theme.Muted));
        root.Add(k.Label(r, $"CONTENTS  ·  {box.Items.Count}", 14, 62, 300, 8, Theme.Sky, bold: true));
        ItemGrid(root, k, r, 82, box.Items, "container/item/", ContainerSelection, "This container is empty.");
        ItemDetail(root, k, r, box.Items.ElementAtOrDefault(ContainerSelection), "container/take", box.TakeVerb, "Select an item to take it.");
    }
    /// <summary>The shared window frame: glass panel, title strip (the drag handle) and a close button.</summary>
    private void WindowFrame(List<GuiElement> root, HudKit k, GuiRect r, string title, string? closeId, GuiColor accent, string? right = null)
    {
        root.Add(k.Fill(r, Theme.Glass, 12, Theme.GlassEdge));
        root.Add(k.Fill(new(r.X + 1, r.Y + 1, r.Width - 2, TitleBarHeight), Theme.Header, 11));
        root.Add(k.Fill(new(r.X + 14, r.Y + 11, 8, 8), accent, 4));
        root.Add(k.Label(r, title, 30, 7, r.Width - 160, 10, Theme.Ink, bold: true));
        if (right is not null) root.Add(k.Label(r, right, r.Width - 190, 8, 140, 8.5f, accent, bold: true, align: GuiTextAlignment.Right));
        if (closeId is not null) root.Add(k.Button(closeId, "x", new(r.Right - 34, r.Y + 5, 24, 21), size: 9, fill: new(255, 255, 255, 14)));
    }

    /// <summary>HUD window buttons. Returns true when handled; <paramref name="request"/> asks the game for anything else.</summary>
    private bool HandleWindowAction(string action, out string? request)
    {
        if (HandleDockAction(action, out request)) return true;
        switch (action)
        {
            case "chat/close": ChatOpen = false; ChatTyping = false; return true;
            case "chat/input": ChatTyping = true; return true;
            case "chat/channel": ChatChannel = ChatChannel == ChatChannel.Local ? ChatChannel.World : ChatChannel.Local; return true;
            case "chat/send": ChatKey(new(0x0D, '\0', false)); return true;
            case "chat/tab/all": ChatFilter = null; return true;
            case "chat/tab/local": ChatFilter = ChatChannel.Local; return true;
            case "chat/tab/world": ChatFilter = ChatChannel.World; return true;
            case "hud/chat": ChatOpen = !ChatOpen; ChatTyping = ChatOpen && ChatTyping; return true;
            case "hud/inventory": InventoryOpen = !InventoryOpen; return true;
            case "inventory/close": InventoryOpen = false; return true;
            case "inventory/use":
                if (Inventory?.Items.ElementAtOrDefault(InventorySelection) is { Kind: "phone" }) { InventoryOpen = false; PhoneOpen = true; PhoneApp = null; }
                return true;
            case "container/close": Container = null; return true;
            case "container/take":
                if (Container is { } box && box.Items.ElementAtOrDefault(ContainerSelection) is { } taken)
                { request = "take:" + box.Id + ":" + taken.Id; Container = null; ContainerSelection = -1; }
                return true;
        }
        if (action.StartsWith("inventory/item/", StringComparison.Ordinal) && int.TryParse(action.AsSpan(15), out int slot)) { InventorySelection = slot; return true; }
        if (action.StartsWith("container/item/", StringComparison.Ordinal) && int.TryParse(action.AsSpan(15), out int boxSlot)) { ContainerSelection = boxSlot; return true; }
        return false;
    }
}
