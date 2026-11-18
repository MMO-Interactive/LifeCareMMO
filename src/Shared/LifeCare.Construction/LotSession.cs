namespace LifeCare.Construction;

/// <summary>Where edits go. The game uses <see cref="ConstructionClient"/> (through the Gateway); tests and offline tools use <see cref="InMemoryLotAuthority"/>.</summary>
public interface ILotAuthority
{
    Task<LotDocument?> GetAsync(string lotId, CancellationToken cancel = default);
    Task<ChangeResult> BuildAsync(string lotId, BuildRequest request, CancellationToken cancel = default);
    Task<ChangeResult> PlaceAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default);
    Task<ChangeResult> MoveAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default);
    Task<ChangeResult> PickUpAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default);
    /// <summary>Opens or closes a door. Not owner-only: the service checks the character is near the door.</summary>
    Task<ChangeResult> SetDoorAsync(string lotId, DoorRequest request, CancellationToken cancel = default);
}

/// <summary>The same rules and revisions as the Construction service, held in memory.</summary>
public sealed class InMemoryLotAuthority : ILotAuthority
{
    private readonly Dictionary<string, LotDocument> lots = new(StringComparer.Ordinal);
    public LotDocument Deed(LotDeed deed) { lock (lots) return lots[deed.LotId] = LotDocument.From(BuildingPlan.Empty(deed), 0); }
    public Task<LotDocument?> GetAsync(string lotId, CancellationToken cancel = default) { lock (lots) return Task.FromResult(lots.GetValueOrDefault(lotId)); }
    public Task<ChangeResult> BuildAsync(string lotId, BuildRequest r, CancellationToken cancel = default) => Change(lotId, r.ExpectedRevision, p => BuildingEditor.Apply(p, r.CharacterId, r.Edit));
    public Task<ChangeResult> PlaceAsync(string lotId, FurnishingRequest r, CancellationToken cancel = default) => Change(lotId, r.ExpectedRevision, p => FurnishingEditor.Place(p, r.CharacterId, r.Piece!));
    public Task<ChangeResult> MoveAsync(string lotId, FurnishingRequest r, CancellationToken cancel = default) => Change(lotId, r.ExpectedRevision, p => FurnishingEditor.Move(p, r.CharacterId, r.Piece!));
    public Task<ChangeResult> PickUpAsync(string lotId, FurnishingRequest r, CancellationToken cancel = default) => Change(lotId, r.ExpectedRevision, p => FurnishingEditor.PickUp(p, r.CharacterId, r.InstanceId ?? ""));
    public Task<ChangeResult> SetDoorAsync(string lotId, DoorRequest r, CancellationToken cancel = default) => Change(lotId, null, p => LotDoors.SetOpen(p, r.CharacterId, r.DoorId, r.Open));
    private Task<ChangeResult> Change(string lotId, long? expected, Func<BuildingPlan, BuildResult> change)
    {
        lock (lots)
        {
            var current = lots[lotId];
            if (expected is { } e && e != current.Revision) return Task.FromResult(new ChangeResult(false, current, "stale_revision", "The lot changed since you last saw it. Look again and retry."));
            var result = change(current.ToPlan());
            if (!result.Accepted) return Task.FromResult(new ChangeResult(false, current, result.ErrorCode, result.Message));
            var next = lots[lotId] = LotDocument.From(result.Plan, current.Revision + 1);
            return Task.FromResult(new ChangeResult(true, next));
        }
    }
}

/// <summary>
/// One player's view of one lot, as in Tiny Clinic: the client never changes the lot itself. It sends one edit at a time
/// to the authority and renders whatever revision comes back; a reply or refresh only ever moves the revision forward.
/// </summary>
public sealed class LotSession(ILotAuthority authority, string characterId, LotDocument lot)
{
    private Task<ChangeResult>? pending;
    private string pendingAction = "";
    public ILotAuthority Authority { get; } = authority;
    public string CharacterId { get; } = characterId;
    public LotDocument Lot { get; private set; } = lot;
    public string Status { get; set; } = "Connected to the lot authority.";
    public bool HasPendingEdit => pending is not null;
    public bool IsOwner => Lot.Lot.OwnerCharacterId == CharacterId;
    /// <summary>Raised on the thread that calls <see cref="Tick"/> when a newer revision arrives.</summary>
    public event Action<LotDocument>? LotChanged;
    /// <summary>Raised when an edit this session sent is resolved: accepted or not, and the authority's message.</summary>
    public event Action<bool, string>? EditResolved;

    public bool Submit(Func<ILotAuthority, long, Task<ChangeResult>> send, string actionName)
    {
        if (pending is not null) { Status = "Waiting for the lot authority to finish the previous edit."; return false; }
        pendingAction = actionName; Status = "Sending " + actionName + "…";
        pending = send(Authority, Lot.Revision);
        return true;
    }

    /// <summary>Call once a frame. Applies a finished edit's reply.</summary>
    public void Tick()
    {
        if (pending is not { IsCompleted: true } done) return;
        pending = null;
        if (done.IsFaulted || done.IsCanceled)
        {
            Status = "The lot authority could not be reached: " + (done.Exception?.GetBaseException().Message ?? "cancelled");
            EditResolved?.Invoke(false, Status); return;
        }
        var result = done.Result;
        Accept(result.Lot);
        Status = result.Accepted ? char.ToUpperInvariant(pendingAction[0]) + pendingAction[1..] + " done." : result.Message;
        EditResolved?.Invoke(result.Accepted, Status);
    }

    /// <summary>Takes a lot document from a refresh or a reply if it is newer than what is shown.</summary>
    public bool Accept(LotDocument document)
    {
        if (document.Lot.LotId != Lot.Lot.LotId || document.Revision <= Lot.Revision) return false;
        Lot = document; LotChanged?.Invoke(document); return true;
    }

    /// <summary>Waits for the in-flight edit, for tools and tests.</summary>
    public async Task SettleAsync() { if (pending is { } p) { try { await p; } catch { } Tick(); } }
}
