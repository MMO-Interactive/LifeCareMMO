using System.Collections.Immutable;
using StormChase.Networking;
using StormChase.Protocol;

/// <summary>UI-only reconstruction from a server-owned daily baseline and clock.
/// Storm generation, motion, damage and physics remain server state.</summary>
internal static class StormChaseOnlineAtmosphere
{
    public static AtmosphereSnapshot Reconstruct(WorldSnapshot world,AtmosphereSnapshot baseline)
    {
        if(baseline.ModelVersion!=1)throw new InvalidDataException("The server atmosphere model is unsupported.");
        double seconds=AuthoritativeAtmosphereReplica.SampleEpoch(world,baseline);
        if(seconds==baseline.CampaignSeconds)return baseline;
        double absoluteHour=baseline.CampaignStartHour+seconds/baseline.CampaignDayLengthSeconds*24;
        int day=Math.Max(baseline.DayNumber,(int)Math.Floor(absoluteHour/24)+1);
        float hour=Math.Clamp((float)(absoluteHour-(day-1)*24),0,MathF.BitDecrement(24f));
        var column=StormChaseAtmosphere.Sample(world.Seed,day,hour,baseline.SamplePosition);
        // A daily packet can arrive after the first fast packet after midnight.
        // The existing server outlook already includes the following two days;
        // discard yesterday rather than labelling its forecast as today's.
        var outlook=baseline.ThreeDayForecast.Where(f=>f.DayNumber>=day).ToImmutableArray();
        return baseline with{ServerTick=world.ServerTick,CampaignSeconds=seconds,DayNumber=day,Hour=hour,
            Sounding=column.Levels,Diagnostics=column.Diagnostics,ThreeDayForecast=outlook};
    }
}
