namespace LifeCare.Construction;

/// <summary>A wheel entry: a leaf with an action id, or a branch with children (Tiny Clinic's ClinicPieMenuAction).</summary>
public sealed record PieMenuAction(string Label, string Description, IReadOnlyList<PieMenuAction> Children, string? ActionId)
{
    public bool HasChildren => Children.Count > 0;
    public static PieMenuAction Leaf(string label, string description, string? actionId) => new(label, description, [], actionId);
}

/// <summary>What a chosen leaf asks the game to do.</summary>
public sealed record PieMenuInvocation(string TargetId, string TargetKind, string TargetName, string ActionId, string Label);

public readonly record struct PieRectangle(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// The interaction wheel opened on furniture and objects, ported from Tiny Clinic's ClinicFurnishingPieMenu: six slots
/// around a centre button (Back inside a branch, Close at the root), long lists paged with "More Actions", and a
/// detail card for the hovered or chosen entry. Pure layout and state; the client draws it and forwards clicks.
/// </summary>
public sealed class InteractionPieMenu
{
    public const float WheelSize = 340;
    public const int VisibleSlotCount = 6;
    public const string MoveAction = "lifecare.furnishing.move";
    public const string RotateAction = "lifecare.furnishing.rotate";
    public const string PickUpAction = "lifecare.furnishing.pickup";
    public const string InspectAction = "lifecare.object.inspect";
    private static readonly (float X, float Y)[] SlotOffsets = [(120, 4), (232, 70), (232, 216), (120, 282), (8, 216), (8, 70)];
    private readonly IReadOnlyList<PieMenuAction> rootActions;
    private readonly List<PieMenuAction> path = [];

    private InteractionPieMenu(float x, float y, string targetId, string targetKind, string displayName, IReadOnlyList<PieMenuAction> roots)
    { X = x; Y = y; TargetId = targetId; TargetKind = targetKind; DisplayName = displayName; rootActions = roots; }

    public float X { get; }
    public float Y { get; }
    public string TargetId { get; }
    public string TargetKind { get; }
    public string DisplayName { get; }
    public string Category => path.Count == 0 ? "INTERACT" : path[^1].Label.ToUpperInvariant();
    public IReadOnlyList<PieMenuAction> Actions => path.Count == 0 ? rootActions : path[^1].Children;
    public bool CanNavigateBack => path.Count > 0;
    public string? Feedback { get; private set; }

    /// <summary>Opens the wheel on any world object with the actions the caller offers, centred on the cursor and kept on screen.</summary>
    public static InteractionPieMenu Create(string targetId, string targetKind, string displayName, IEnumerable<PieMenuAction> actions,
        int cursorX, int cursorY, int viewportWidth, int viewportHeight)
    {
        float x = Math.Clamp(cursorX - WheelSize / 2, 8, Math.Max(8, viewportWidth - WheelSize - 8));
        float y = Math.Clamp(cursorY - WheelSize / 2, 8, Math.Max(8, viewportHeight - WheelSize - 8));
        return new(x, y, targetId, targetKind, displayName, Normalize(actions));
    }

    /// <summary>The furniture wheel: arranging for the lot's owner, and inspecting for anyone.</summary>
    public static InteractionPieMenu ForFurnishing(PlacedFurnishing piece, string displayName, bool isOwner, int cursorX, int cursorY, int viewportWidth, int viewportHeight,
        IEnumerable<PieMenuAction>? additionalActions = null)
    {
        var roots = new List<PieMenuAction>(additionalActions ?? []);
        if (isOwner)
            roots.Add(new("Arrange", $"Arrange the {displayName.ToLowerInvariant()}.",
            [
                PieMenuAction.Leaf("Move", "Pick it up and set it down somewhere else on your lot. The ghost shows where it fits.", MoveAction),
                PieMenuAction.Leaf("Rotate", "Turn it a quarter turn where it stands.", RotateAction),
                PieMenuAction.Leaf("Pick Up", "Pack it back into its package.", PickUpAction),
            ], null));
        roots.Add(PieMenuAction.Leaf("Inspect", $"{displayName}: {piece.SpanX * BuildGrid.SubCellMeters:0.#} m × {piece.SpanZ * BuildGrid.SubCellMeters:0.#} m.", InspectAction));
        return Create(piece.InstanceId, "furnishing", displayName, roots, cursorX, cursorY, viewportWidth, viewportHeight);
    }

    /// <summary>Chooses slot <paramref name="index"/>: a branch opens, a leaf returns its invocation.</summary>
    public PieMenuInvocation? SelectAction(int index)
    {
        if ((uint)index >= (uint)Actions.Count) return null;
        var action = Actions[index];
        if (action.HasChildren) { path.Add(action); Feedback = null; return null; }
        Feedback = action.Description;
        return action.ActionId is null ? null : new(TargetId, TargetKind, DisplayName, action.ActionId, action.Label);
    }

    public bool NavigateBack() { if (path.Count == 0) return false; path.RemoveAt(path.Count - 1); Feedback = null; return true; }

    /// <summary>Screen rectangle of slot <paramref name="index"/> (100 × 54 px, as in Tiny Clinic).</summary>
    public PieRectangle Slot(int index) => new(X + SlotOffsets[index].X, Y + SlotOffsets[index].Y, 100, 54);
    public PieRectangle Center => new(X + 118, Y + 118, 104, 104);

    /// <summary>Which slot a click lands on: 0..5, -1 for the centre (Back/Close), null outside the wheel.</summary>
    public int? HitTest(float screenX, float screenY)
    {
        if (Center.Contains(screenX, screenY)) return -1;
        for (int i = 0; i < Math.Min(Actions.Count, VisibleSlotCount); i++) if (Slot(i).Contains(screenX, screenY)) return i;
        return null;
    }

    /// <summary>
    /// Handles a click the way Tiny Clinic does: a slot selects, the centre goes back (or closes at the root), outside
    /// closes. Returns the invocation if a leaf with an action was chosen; <paramref name="close"/> says the wheel is done.
    /// </summary>
    public PieMenuInvocation? Click(float screenX, float screenY, out bool close)
    {
        close = false;
        switch (HitTest(screenX, screenY))
        {
            case null: close = true; return null;
            case -1: close = !NavigateBack(); return null;
            case int slot:
                var invocation = SelectAction(slot);
                close = invocation is not null;
                return invocation;
        }
    }

    /// <summary>Tiny Clinic's pagination: at most six entries per ring, the sixth becoming "More Actions" when needed.</summary>
    public static IReadOnlyList<PieMenuAction> Normalize(IEnumerable<PieMenuAction> actions) =>
        Page(actions.Select(a => a.HasChildren ? a with { Children = Normalize(a.Children) } : a).ToArray());

    private static IReadOnlyList<PieMenuAction> Page(IReadOnlyList<PieMenuAction> actions) => actions.Count <= VisibleSlotCount ? actions.ToArray()
        : actions.Take(VisibleSlotCount - 1).Append(new PieMenuAction("More Actions", "Show the remaining interactions for this menu.", Page(actions.Skip(VisibleSlotCount - 1).ToArray()), null)).ToArray();
}
