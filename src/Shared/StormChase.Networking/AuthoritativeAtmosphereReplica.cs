using StormChase.Protocol;

namespace StormChase.Networking;

/// <summary>One connection's immutable server profile, carried across faster
/// movement snapshots. A delayed reliable profile cannot roll the cache back.</summary>
public sealed class AuthoritativeAtmosphereReplica
{
    private readonly Func<WorldSnapshot,AtmosphereSnapshot,AtmosphereSnapshot>? derive;
    private WorldSnapshot? latestWorld;
    private int? seed;
    private double sampledSeconds=double.NaN;
    private long sampledBaselineTick=-1;
    public AtmosphereSnapshot? Baseline { get; private set; }
    public AtmosphereSnapshot? Current { get; private set; }

    public AuthoritativeAtmosphereReplica(Func<WorldSnapshot,AtmosphereSnapshot,AtmosphereSnapshot>? derive=null)
    {this.derive=derive;}

    public bool Apply(AtmosphereSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ServerTick < 0 || profile.ModelVersion!=1 || Baseline is { } previous &&
            (profile.ServerTick <= previous.ServerTick||profile.DayNumber<previous.DayNumber||profile.CampaignSeconds<previous.CampaignSeconds)) return false;
        Baseline = profile; Current = profile; sampledSeconds=double.NaN;
        if(latestWorld is not null)Refresh(latestWorld);
        return true;
    }

    public WorldSnapshot Retain(WorldSnapshot world)
        => Merge(world,true);

    /// <summary>Attach an updated cache to a previously published client world;
    /// its derived UI profile must never be mistaken for a wire baseline.</summary>
    public WorldSnapshot AttachCurrent(WorldSnapshot world)
        => Merge(world,false);

    private WorldSnapshot Merge(WorldSnapshot world,bool acceptEmbeddedBaseline)
    {
        ArgumentNullException.ThrowIfNull(world);
        if(seed is {} expected&&world.Seed!=expected)throw new InvalidDataException("Server changed the atmospheric campaign seed inside a connection.");
        if(!double.IsFinite(world.SimulationSeconds)||world.SimulationSeconds<0||latestWorld is {} prior&&world.SimulationSeconds+1e-8<prior.SimulationSeconds)
            throw new InvalidDataException("Server atmospheric clock moved backwards.");
        seed=world.Seed;latestWorld=world;
        if (acceptEmbeddedBaseline&&world.Atmosphere is { } profile) Apply(profile);
        Refresh(world);
        return ReferenceEquals(world.Atmosphere, Current) ? world : world with { Atmosphere = Current };
    }

    private void Refresh(WorldSnapshot world)
    {
        if(derive is null||Baseline is not{} baseline)return;
        double epoch=SampleEpoch(world,baseline);
        if(epoch==sampledSeconds&&sampledBaselineTick==baseline.ServerTick)return;
        Current=derive(world,baseline);
        sampledSeconds=epoch;sampledBaselineTick=baseline.ServerTick;
    }

    public static double SampleEpoch(WorldSnapshot world,AtmosphereSnapshot baseline)
    {
        // The server updates its campaign on completed 20 Hz ticks; a continuous
        // clock just past midnight cannot precede the tick that starts the day.
        double completedTickSeconds=Math.Floor(world.SimulationSeconds*20+1e-8)/20;
        double absoluteHour=baseline.CampaignStartHour+completedTickSeconds/baseline.CampaignDayLengthSeconds*24;
        int day=(int)Math.Floor(absoluteHour/24)+1;
        double midnight=(day-1)*baseline.CampaignDayLengthSeconds-baseline.CampaignStartHour/24d*baseline.CampaignDayLengthSeconds;
        midnight=Math.Ceiling(midnight*20-1e-8)/20;
        // A fractional-second midnight is an additional cache boundary, so the
        // UI cannot display yesterday while awaiting the reliable daily packet.
        return Math.Max(baseline.CampaignSeconds,Math.Max(Math.Floor(world.SimulationSeconds+1e-8),midnight));
    }
}
