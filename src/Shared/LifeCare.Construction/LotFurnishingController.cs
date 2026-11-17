namespace LifeCare.Construction;

/// <summary>A delivered furniture package the player is carrying. Its id becomes the placed piece's instance id.</summary>
public sealed record FurnishingPackage(string PackageId, FurnishingDefinition Definition);

public enum FurnishingGesture { None, CarryingPackage, MovingPiece }

/// <summary>
/// Tiny Clinic-style furniture handling in the world, outside build mode. A carried package (ClinicPedizonFurnishingPlacementPreviewState)
/// or a placed piece being moved (ClinicWorldFurnishingMovePreviewState) follows the pointer as a ghost snapped to 0.5 m
/// sub-cells; <see cref="FurnishingEditor.Preview"/> gives the ghost's verdict, and setting it down sends Place or Move.
/// </summary>
public sealed class LotFurnishingController
{
    private readonly LotSession session;
    private (float X, float Z) pointer;
    private int yaw;
    private int level;

    public LotFurnishingController(LotSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        session.LotChanged += _ => { if (Gesture == FurnishingGesture.MovingPiece && Moving is { } m && session.Lot.Furnishings.All(f => f.InstanceId != m.InstanceId)) Cancel(); else UpdatePreview(); };
    }

    public LotSession Session => session;
    public FurnishingGesture Gesture { get; private set; }
    public FurnishingPackage? Carried { get; private set; }
    public PlacedFurnishing? Moving { get; private set; }
    /// <summary>The ghost, or null when nothing is carried or moved.</summary>
    public FurnishingPlacementPreview? Preview { get; private set; }

    /// <summary>Picks a delivered package up so its piece can be set down on the lot.</summary>
    public bool BeginCarry(FurnishingPackage package, int onLevel = 0)
    {
        if (Gesture != FurnishingGesture.None || session.HasPendingEdit) { session.Status = "Finish what you are holding first."; return false; }
        if (session.Lot.Furnishings.Any(f => f.InstanceId == package.PackageId)) { session.Status = "That package has already been unpacked."; return false; }
        Carried = package; Gesture = FurnishingGesture.CarryingPackage; yaw = 0; level = Math.Max(0, onLevel);
        session.Status = $"Carrying {package.Definition.Name}. Point where it should go, R to turn, click to set it down.";
        UpdatePreview(); return true;
    }

    /// <summary>Starts moving a placed piece (the pie menu's Move).</summary>
    public bool BeginMove(string instanceId)
    {
        if (Gesture != FurnishingGesture.None || session.HasPendingEdit) { session.Status = "Finish what you are holding first."; return false; }
        if (!session.IsOwner) { session.Status = "Only the lot's owner can rearrange furniture."; return false; }
        if (session.Lot.Furnishings.FirstOrDefault(f => f.InstanceId == instanceId) is not { } piece) { session.Status = "That furnishing is no longer present."; return false; }
        Moving = piece; Gesture = FurnishingGesture.MovingPiece; yaw = piece.YawQuarterTurns; level = piece.Level;
        session.Status = "Moving the piece. Click to set it down, R to turn, Esc to leave it where it was.";
        UpdatePreview(); return true;
    }

    public void SetPointer(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) return;
        pointer = (worldX, worldZ); UpdatePreview();
    }

    public void Rotate(int quarterTurns = 1) { yaw = ((yaw + quarterTurns) % 4 + 4) % 4; UpdatePreview(); }

    /// <summary>Sets the carried or moved piece down if the ghost is valid.</summary>
    public bool SetDown()
    {
        UpdatePreview();
        if (Preview is not { } ghost) return false;
        if (!ghost.IsValid) { session.Status = ghost.Status; return false; }
        string lotId = session.Lot.Lot.LotId; var piece = ghost.Candidate;
        bool sent = Gesture == FurnishingGesture.CarryingPackage
            ? session.Submit((a, revision) => a.PlaceAsync(lotId, new(session.CharacterId, piece, ExpectedRevision: revision)), "set down " + Carried!.Definition.Name)
            : session.Submit((a, revision) => a.MoveAsync(lotId, new(session.CharacterId, piece, ExpectedRevision: revision)), "move the piece");
        if (sent) { Gesture = FurnishingGesture.None; Carried = null; Moving = null; Preview = null; }
        return sent;
    }

    /// <summary>Picks a placed piece back up into its package (the pie menu's Pick Up).</summary>
    public bool PickUp(string instanceId)
    {
        if (!session.IsOwner) { session.Status = "Only the lot's owner can pick furniture up."; return false; }
        string lotId = session.Lot.Lot.LotId;
        return session.Submit((a, revision) => a.PickUpAsync(lotId, new(session.CharacterId, InstanceId: instanceId, ExpectedRevision: revision)), "pick the piece up");
    }

    /// <summary>Turns a placed piece a quarter turn in place (the pie menu's Rotate), keeping its footprint's corner.</summary>
    public bool RotateInPlace(string instanceId)
    {
        if (session.Lot.Furnishings.FirstOrDefault(f => f.InstanceId == instanceId) is not { } piece) { session.Status = "That furnishing is no longer present."; return false; }
        var turned = piece with { YawQuarterTurns = (piece.YawQuarterTurns + 1) % 4 };
        var verdict = FurnishingEditor.Preview(session.Lot.ToPlan(), session.CharacterId, turned);
        if (!verdict.IsValid) { session.Status = verdict.Status; return false; }
        string lotId = session.Lot.Lot.LotId;
        return session.Submit((a, revision) => a.MoveAsync(lotId, new(session.CharacterId, turned, ExpectedRevision: revision)), "turn the piece");
    }

    public bool Cancel()
    {
        if (Gesture == FurnishingGesture.None) return false;
        session.Status = Gesture == FurnishingGesture.CarryingPackage ? "Put the package away; it is still yours to unpack." : "Left the piece where it was.";
        Carried = null; Moving = null; Gesture = FurnishingGesture.None; Preview = null;
        return true;
    }

    /// <summary>The placed piece under a world point on a level, for opening its pie menu.</summary>
    public PlacedFurnishing? HitTest(float worldX, float worldZ, int onLevel) => HitTest(session.Lot, worldX, worldZ, onLevel);

    public static PlacedFurnishing? HitTest(LotDocument lot, float worldX, float worldZ, int onLevel)
    {
        int x = (int)MathF.Floor(worldX / BuildGrid.SubCellMeters), z = (int)MathF.Floor(worldZ / BuildGrid.SubCellMeters);
        return lot.Furnishings.FirstOrDefault(f => f.Level == onLevel && x >= f.MinimumX && x < f.MinimumX + f.SpanX && z >= f.MinimumZ && z < f.MinimumZ + f.SpanZ);
    }

    /// <summary>The piece whose footprint is centred as near the pointer as the 0.5 m sub-cell grid allows.</summary>
    public static PlacedFurnishing Snap(string instanceId, string definitionId, int widthSubCells, int depthSubCells, int level, float worldX, float worldZ, int yawQuarterTurns)
    {
        int spanX = (yawQuarterTurns & 1) == 0 ? widthSubCells : depthSubCells, spanZ = (yawQuarterTurns & 1) == 0 ? depthSubCells : widthSubCells;
        int minimumX = (int)MathF.Round(worldX / BuildGrid.SubCellMeters - spanX / 2f), minimumZ = (int)MathF.Round(worldZ / BuildGrid.SubCellMeters - spanZ / 2f);
        var tile = PlacedFurnishing.TileOf(minimumX, minimumZ);
        return new(instanceId, definitionId, level, tile.X, tile.Z, minimumX - tile.X * BuildGrid.SubCellsPerTile, minimumZ - tile.Z * BuildGrid.SubCellsPerTile,
            yawQuarterTurns, widthSubCells, depthSubCells);
    }

    private void UpdatePreview()
    {
        PlacedFurnishing? candidate = Gesture switch
        {
            FurnishingGesture.CarryingPackage when Carried is { } c => Snap(c.PackageId, c.Definition.DefinitionId, c.Definition.FootprintWidthSubCells, c.Definition.FootprintDepthSubCells, level, pointer.X, pointer.Z, yaw),
            FurnishingGesture.MovingPiece when Moving is { } m => Snap(m.InstanceId, m.DefinitionId, m.FootprintWidthSubCells, m.FootprintDepthSubCells, level, pointer.X, pointer.Z, yaw),
            _ => null,
        };
        Preview = candidate is null ? null : FurnishingEditor.Preview(session.Lot.ToPlan(), session.CharacterId, candidate);
    }
}
