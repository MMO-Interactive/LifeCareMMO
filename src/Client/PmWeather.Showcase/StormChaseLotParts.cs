using LifeCare.Construction;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;

/// <summary>
/// Lot pieces as render items, shared by the client (drawing) and the district host (PhysX destruction), so the
/// host's debris and every client's copy name and size each piece identically.
/// </summary>
internal static class StormChaseLotParts
{
    public static readonly AssetHandle Cube = new("mesh", "lot/cube");

    public static RenderItem Item(LotPiece piece, string? material = null) => new("lot/" + (material ?? piece.Material), Cube, new AssetHandle("material", "lot/" + (material ?? piece.Material)),
        new(piece.CenterX, piece.CenterY, piece.CenterZ), new(piece.SizeX, piece.SizeY, piece.SizeZ), piece.YawDegrees, true,
        MotionVectorId: piece.PieceId, AllowStaticInstancing: material is null);

    public static ExternalDestructiblePart Destructible(LotPiece piece) => new(Item(piece), piece.ReleaseWindMph, piece.MassKilograms);
}
