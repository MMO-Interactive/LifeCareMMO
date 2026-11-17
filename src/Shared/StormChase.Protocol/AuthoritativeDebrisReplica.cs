using System.Collections.ObjectModel;

namespace StormChase.Protocol;

public enum DebrisApplyResult { Ignored, Buffered, Applied, BaselineCommitted, Invalid }

/// <summary>Assembles a single coherent collision world from server data. It never integrates physics or extrapolates poses.</summary>
/// <remarks>Call under the client's state lock. Published Bodies arrays remain immutable after subsequent updates.</remarks>
public sealed class AuthoritativeDebrisReplica
{
    private readonly int _maxBodies;
    private readonly Dictionary<int, DebrisBodyState> _bodies = [];
    private readonly Dictionary<int, string> _mappings = [];
    private readonly Dictionary<string, int> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Motion> _motion = [];
    private readonly SortedDictionary<long, DebrisEvent> _journal = [];
    private readonly ReadOnlyDictionary<int, string> _mappingView;
    private DebrisBodyState[] _view = [];
    private bool _dirty;
    private PendingBaseline? _pending;
    private long _lastEvent = -1;
    public long BaselineRevision { get; private set; } = -1;
    public long LatestServerTick { get; private set; } = -1;
    public bool CompletedBaseline { get; private set; }
    public IReadOnlyList<DebrisBodyState> Bodies { get { if (_dirty) { _view = _bodies.Values.ToArray(); _dirty = false; } return _view; } }
    public IReadOnlyDictionary<int, string> StableMappings => _mappingView;

    public AuthoritativeDebrisReplica(int maxBodies = 65536)
    {
        if (maxBodies is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxBodies));
        _maxBodies = maxBodies; _mappingView = new(_mappings);
    }

    public DebrisApplyResult Apply(DebrisSnapshot snapshot)
    {
        if (snapshot.ServerTick < 0 || snapshot.BaselineRevision < 0 || snapshot.Bodies is null || snapshot.Bodies.Length > _maxBodies || snapshot.ChunkCount is < 1 or > 1024 || snapshot.ChunkIndex < 0 || snapshot.ChunkIndex >= snapshot.ChunkCount || snapshot.Bodies.Any(x => !Valid(x))) return DebrisApplyResult.Invalid;
        if (snapshot.BaselineRevision < BaselineRevision) return DebrisApplyResult.Ignored;
        if (!snapshot.FullBaseline)
        {
            if (snapshot.Bodies.Select(x => x.BodyId).Distinct().Count() != snapshot.Bodies.Length || _motion.Count + snapshot.Bodies.Count(x => !_motion.ContainsKey(x.BodyId)) > _maxBodies || snapshot.Bodies.Any(x => _mappings.TryGetValue(x.BodyId, out var id) && x.StablePartId.Length > 0 && x.StablePartId != id)) return DebrisApplyResult.Invalid;
            var changed = false;
            foreach (var body in snapshot.Bodies)
            {
                if (_motion.TryGetValue(body.BodyId, out var previous) && previous.Tick >= snapshot.ServerTick) continue;
                _motion[body.BodyId] = new(snapshot.ServerTick, snapshot.BaselineRevision, body);
                if (CompletedBaseline && _bodies.TryGetValue(body.BodyId, out var existing))
                {
                    if (existing.Collected && !body.Collected) continue;
                    _bodies[body.BodyId] = body with { StablePartId = existing.StablePartId }; changed = true;
                }
            }
            LatestServerTick = Math.Max(LatestServerTick, snapshot.ServerTick); _dirty |= changed;
            return changed ? DebrisApplyResult.Applied : DebrisApplyResult.Buffered;
        }
        if (CompletedBaseline && snapshot.BaselineRevision == BaselineRevision && snapshot.ServerTick <= LatestServerTick && _pending is null) return DebrisApplyResult.Ignored;
        if (_pending is null || snapshot.BaselineRevision > _pending.Revision || (snapshot.BaselineRevision == _pending.Revision && snapshot.ServerTick > _pending.Tick)) _pending = new(snapshot.BaselineRevision, snapshot.ServerTick, snapshot.ChunkCount);
        if (snapshot.BaselineRevision != _pending.Revision || snapshot.ServerTick != _pending.Tick) return DebrisApplyResult.Ignored;
        if (snapshot.ChunkCount != _pending.Chunks.Length) return DebrisApplyResult.Invalid;
        if (_pending.Chunks[snapshot.ChunkIndex] is not null) return DebrisApplyResult.Ignored;
        if (_pending.BodyCount + snapshot.Bodies.Length > _maxBodies) { _pending = null; return DebrisApplyResult.Invalid; }
        _pending.Chunks[snapshot.ChunkIndex] = snapshot.Bodies.ToArray(); _pending.BodyCount += snapshot.Bodies.Length; ++_pending.Received;
        if (_pending.Received != _pending.Chunks.Length) return DebrisApplyResult.Buffered;

        var assembled = new Dictionary<int, DebrisBodyState>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in _pending.Chunks) foreach (var body in chunk!)
        {
            if (string.IsNullOrWhiteSpace(body.StablePartId) || !assembled.TryAdd(body.BodyId, body) || !names.Add(body.StablePartId)) { _pending = null; return DebrisApplyResult.Invalid; }
        }
        // A baseline can spend time in reliable transit while newer motion/events arrive.
        foreach (var id in assembled.Keys.ToArray()) if (_motion.TryGetValue(id, out var motion) && motion.Revision >= _pending.Revision && motion.Tick > _pending.Tick && !assembled[id].Collected)
        {
            if (motion.Body.StablePartId.Length > 0 && motion.Body.StablePartId != assembled[id].StablePartId) { _pending = null; return DebrisApplyResult.Invalid; }
            assembled[id] = motion.Body with { StablePartId = assembled[id].StablePartId };
        }
        foreach (var value in _journal.Values.Where(x => x.BaselineRevision > _pending.Revision))
        {
            if (value.Kind == DebrisEventKind.Removed) assembled[value.Body.BodyId] = value.Body;
            else if (!string.IsNullOrWhiteSpace(value.Body.StablePartId))
            {
                var body = value.Body;
                if (_motion.TryGetValue(body.BodyId, out var motion) && motion.Revision >= value.BaselineRevision && !body.Collected) body = motion.Body with { StablePartId = body.StablePartId };
                assembled[body.BodyId] = body;
            }
        }
        if (assembled.Count > _maxBodies || assembled.Values.Any(x => string.IsNullOrWhiteSpace(x.StablePartId)) || assembled.Values.Select(x => x.StablePartId).Distinct(StringComparer.Ordinal).Count() != assembled.Count) { _pending = null; return DebrisApplyResult.Invalid; }
        _bodies.Clear(); _mappings.Clear(); _parts.Clear(); foreach (var pair in assembled) { _bodies[pair.Key] = pair.Value; _mappings[pair.Key] = pair.Value.StablePartId; _parts[pair.Value.StablePartId] = pair.Key; }
        BaselineRevision = _pending.Revision; LatestServerTick = Math.Max(LatestServerTick, _pending.Tick); CompletedBaseline = true; _dirty = true; _pending = null;
        foreach (var key in _journal.Where(x => x.Value.BaselineRevision <= BaselineRevision).Select(x => x.Key).ToArray()) _journal.Remove(key);
        return DebrisApplyResult.BaselineCommitted;
    }

    public DebrisApplyResult Apply(DebrisEvent value)
    {
        if (value.EventId < 0 || value.BaselineRevision < 0 || !Enum.IsDefined(value.Kind) || !Valid(value.Body) || (value.Kind == DebrisEventKind.Removed && !value.Body.Collected)) return DebrisApplyResult.Invalid;
        if (value.EventId <= _lastEvent || value.BaselineRevision < BaselineRevision) return DebrisApplyResult.Ignored;
        if (_journal.Count >= _maxBodies) return DebrisApplyResult.Invalid;
        if (string.IsNullOrWhiteSpace(value.Body.StablePartId) || (_mappings.TryGetValue(value.Body.BodyId, out var existing) && existing != value.Body.StablePartId) || (_parts.TryGetValue(value.Body.StablePartId, out var owner) && owner != value.Body.BodyId) || (!_bodies.ContainsKey(value.Body.BodyId) && _bodies.Count >= _maxBodies)) return DebrisApplyResult.Invalid;
        _lastEvent = value.EventId; _journal[value.EventId] = value;
        if (!CompletedBaseline) return DebrisApplyResult.Buffered;
        var body = value.Body;
        if (value.Kind != DebrisEventKind.Removed)
        {
            if (_motion.TryGetValue(body.BodyId, out var motion) && motion.Revision >= value.BaselineRevision && !body.Collected) body = motion.Body with { StablePartId = body.StablePartId };
        }
        _bodies[body.BodyId] = body; _mappings[body.BodyId] = body.StablePartId; _parts[body.StablePartId] = body.BodyId;
        _dirty = true; return DebrisApplyResult.Applied;
    }

    public void Reset()
    {
        _bodies.Clear(); _mappings.Clear(); _parts.Clear(); _motion.Clear(); _journal.Clear(); _view = []; _dirty = false; _pending = null; _lastEvent = -1;
        BaselineRevision = -1; LatestServerTick = -1; CompletedBaseline = false;
    }
    private static bool Valid(DebrisBodyState? body) => body is not null && body.BodyId > 0 && body.Position.IsFinite && body.Rotation.IsFinite && body.LinearVelocity.IsFinite && body.AngularVelocity.IsFinite && body.StablePartId is { Length: <= 128 } && (!body.Collected || (body.Archived && body.Sleeping && !body.CollisionEnabled));
    private sealed record Motion(long Tick, long Revision, DebrisBodyState Body);
    private sealed class PendingBaseline(long revision, long tick, int chunks)
    {
        internal long Revision { get; } = revision;
        internal long Tick { get; } = tick;
        internal DebrisBodyState[]?[] Chunks { get; } = new DebrisBodyState[chunks][];
        internal int Received, BodyCount;
    }
}
