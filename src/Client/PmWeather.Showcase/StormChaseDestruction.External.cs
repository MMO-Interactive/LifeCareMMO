using System.Numerics;
using Shared.Contracts.Rendering;

/// <summary>A destructible part that is not authored world content, such as a piece of a player-built LifeCare lot.</summary>
internal readonly record struct ExternalDestructiblePart(RenderItem Item, float ReleaseWindMph, float MassKilograms);

internal sealed partial class StormChaseDestruction
{
    private readonly Dictionary<string, HashSet<string>> externalGroups = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets the destructible parts of one external group (a lot), replacing its previous set. Parts keep their identity
    /// by motion-vector id: an unchanged standing part is left alone, a changed one is rebuilt, a removed one disappears.
    /// Parts the storm already tore loose stay debris until collected, whatever the group now says.
    /// </summary>
    public void SetExternalParts(string groupId, IEnumerable<ExternalDestructiblePart> definitions)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsReplicaMode) throw new InvalidOperationException("Replica destruction receives parts from the server.");
        var wanted = new Dictionary<string, ExternalDestructiblePart>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition.Item.MotionVectorId is not { Length: > 0 and <= 128 } id) throw new ArgumentException("External parts need a stable motion-vector id of at most 128 characters.");
            if (!float.IsFinite(definition.MassKilograms) || definition.MassKilograms <= 0 || float.IsNaN(definition.ReleaseWindMph)) throw new ArgumentException("External part mass and release wind must be valid: " + id);
            wanted[id] = definition;
        }
        if (!externalGroups.TryGetValue(groupId, out var members)) externalGroups[groupId] = members = new(StringComparer.Ordinal);
        foreach (var id in members.ToArray())
        {
            if (!partsById.TryGetValue(id, out var existing)) { members.Remove(id); continue; }
            if (existing.IsReleased) { wanted.Remove(id); continue; }
            if (wanted.TryGetValue(id, out var next) && next.Item == existing.Original && next.ReleaseWindMph == existing.BreakWindMph && next.MassKilograms == existing.Mass)
            { wanted.Remove(id); continue; }
            RemovePart(existing); members.Remove(id);
        }
        foreach (var (id, definition) in wanted)
        {
            if (partsById.ContainsKey(id)) throw new ArgumentException("An external part id collides with an existing part: " + id);
            var part = new Part(definition.Item, Vector3.Abs(ToNative(definition.Item.Scale)), definition.ReleaseWindMph, definition.MassKilograms);
            parts.Add(part); partsById.Add(id, part); members.Add(id);
            var cell = Cell(definition.Item.Position);
            if (!partGrid.TryGetValue(cell, out var bucket)) partGrid.Add(cell, bucket = []);
            bucket.Add(part);
        }
        streamCountdown = 0;
    }

    public bool IsExternalPart(string id) => externalGroups.Values.Any(group => group.Contains(id));

    private void RemovePart(Part part)
    {
        if (part.HasBody) { physics.RemoveBody(part.Body); part.HasBody = false; }
        staticParts.Remove(part); parts.Remove(part); partsById.Remove(part.Original.MotionVectorId!);
        if (partGrid.TryGetValue(Cell(part.Original.Position), out var bucket)) bucket.Remove(part);
    }
}
