using System.Collections.Concurrent;
using System.Text.Json;
using LifeCare.Construction;

namespace LifeCare.Construction.Service;

/// <summary>
/// The Construction service's authority over lots. Each lot changes under its own lock and is written to disk before the
/// change is acknowledged, so every player and district host reading a revision sees the same building.
/// </summary>
public sealed class LotStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string root;
    private readonly ConcurrentDictionary<string, Entry> lots = new(StringComparer.Ordinal);

    public LotStore(string dataRoot)
    {
        root = Path.Combine(dataRoot, "lots"); Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var doc = JsonSerializer.Deserialize<LotDocument>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Invalid lot file " + file);
            lots[doc.Lot.LotId] = new(doc);
        }
    }

    public LotDocument? Get(string lotId) => lots.TryGetValue(lotId, out var e) ? Volatile.Read(ref e.Document) : null;

    /// <summary>Every lot with its current revision, in id order.</summary>
    public IReadOnlyList<LotSummary> List() => lots.Values.Select(e => Volatile.Read(ref e.Document)).OrderBy(d => d.Lot.LotId, StringComparer.Ordinal)
        .Select(d => new LotSummary(d.Lot, d.Revision)).ToArray();

    public LotDocument Create(LotDeed deed)
    {
        if (!ValidId(deed.LotId) || deed.MinimumTileX > deed.MaximumTileX || deed.MinimumTileZ > deed.MaximumTileZ
            || (long)(deed.MaximumTileX - deed.MinimumTileX + 1) * (deed.MaximumTileZ - deed.MinimumTileZ + 1) > 4096)
            throw new ArgumentException("A lot needs a safe id and at most 4096 tiles.");
        var entry = lots.GetOrAdd(deed.LotId, _ => new(LotDocument.From(BuildingPlan.Empty(deed), 0)));
        lock (entry) { if (entry.Document.Revision == 0) Save(entry.Document); }
        return entry.Document;
    }

    /// <summary>Applies one change atomically. <paramref name="expectedRevision"/> rejects edits made against a stale view.</summary>
    public ChangeResult Change(string lotId, long? expectedRevision, Func<BuildingPlan, BuildResult> change)
    {
        if (!lots.TryGetValue(lotId, out var entry)) throw new KeyNotFoundException();
        lock (entry)
        {
            var current = entry.Document;
            if (expectedRevision is { } expected && expected != current.Revision) return new(false, current, "stale_revision", "The lot changed since you last saw it. Look again and retry.");
            var result = change(current.ToPlan());
            if (!result.Accepted) return new(false, current, result.ErrorCode, result.Message);
            var next = LotDocument.From(result.Plan, current.Revision + 1);
            Save(next); Volatile.Write(ref entry.Document, next);
            return new(true, next);
        }
    }

    private void Save(LotDocument doc)
    {
        string path = Path.Combine(root, doc.Lot.LotId + ".json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, Json));
        File.Move(temp, path, true);
    }

    private static bool ValidId(string id) => id.Length is >= 3 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private sealed class Entry(LotDocument document) { public LotDocument Document = document; }
}
