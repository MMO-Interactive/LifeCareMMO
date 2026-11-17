using System.Text.Json;
using StormChase.Networking;
using StormChase.Protocol;

internal static class StormChaseOnlineAtmosphereProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks=new List<object>();var samples=new List<object>();
        void Check(bool value,string name)
        {checks.Add(new{Name=name,Passed=value});if(!value){File.WriteAllText(Path.Combine(output,"online-atmosphere-reconstruction.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Samples=samples},new JsonSerializerOptions{WriteIndented=true}));throw new InvalidOperationException(name);}}
        string Json<T>(T value)=>JsonSerializer.Serialize(value,ProtocolCodec.JsonOptions);
        foreach(float start in new[]{8f,8.01f,8.02f,13.37f})
        {
            string[] args=["--campaign-seed","73019","--campaign-start-hour",start.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            double midnight=(24-start)/24d*StormChaseCampaignWeather.DefaultDayLengthSeconds;
            var campaign=new StormChaseCampaignWeather(new(args),args,Math.Max(0,midnight-1.25));
            var baseline=campaign.Atmosphere with{ServerTick=100};
            int calls=0;
            var replica=new AuthoritativeAtmosphereReplica((world,context)=>{calls++;return StormChaseOnlineAtmosphere.Reconstruct(world,context);});
            WorldSnapshot world=new(100,campaign.ElapsedSeconds,campaign.Seed,[],[],[],[],[],Environment:campaign.Current,Atmosphere:baseline);
            var published=replica.Retain(world);
            Check(Json(published.Atmosphere!.Sounding)==Json(baseline.Sounding)&&Json(published.Atmosphere.Diagnostics)==Json(baseline.Diagnostics),
                "Admission sample matches the complete authoritative profile at start hour "+start);
            bool matched=true,validHours=true,oldDayHidden=true;int frames=0,rolloverFrame=-1;AtmosphereSnapshot? rollover=null;
            for(int i=1;i<=80;i++)
            {
                campaign.Update(.05);
                world=world with{ServerTick=100+i,SimulationSeconds=campaign.ElapsedSeconds,Environment=campaign.Current,Atmosphere=null};
                published=replica.Retain(world);var current=published.Atmosphere!;frames++;
                bool frameMatches=Json(current.Sounding)==Json(campaign.Atmosphere.Sounding)&&Json(current.Diagnostics)==Json(campaign.Atmosphere.Diagnostics);
                if(matched&&!frameMatches)samples.Add(new{StartHour=start,FirstMismatchFrame=i,ElapsedSeconds=world.SimulationSeconds,Epoch=AuthoritativeAtmosphereReplica.SampleEpoch(world,replica.Baseline!),Client=current,Server=campaign.Atmosphere});
                matched&=frameMatches;
                validHours&=current.Hour is >=0 and <24&&current.DayNumber==campaign.DayNumber;
                oldDayHidden&=current.ThreeDayForecast.All(f=>f.DayNumber>=current.DayNumber);
                if(campaign.DayNumber>baseline.DayNumber&&rollover is null)
                {rollover=campaign.Atmosphere with{ServerTick=world.ServerTick};rolloverFrame=i;}
                // The actual reliable daily baseline is deliberately delayed behind
                // newer fast frames, mimicking lag/reordering between the lanes.
                if(rollover is not null&&i==rolloverFrame+24)
                {
                    Check(replica.Current!.ServerTick>rollover.ServerTick&&replica.Apply(rollover),
                        "A delayed daily baseline remains admissible after newer derived UI samples at "+start);
                    published=replica.AttachCurrent(published);
                    Check(replica.Baseline!.ThreeDayForecast.Length==3&&published.Atmosphere!.ThreeDayForecast.Length==3,
                        "Attaching a derived world cannot replace the newly delivered complete daily outlook at "+start);
                    Check(!replica.Apply(baseline with{ServerTick=9999}),
                        "A previous-day profile cannot undo the rollover even with a later transport timestamp at "+start);
                }
            }
            Check(matched,"Every reconstructed 97-level column and diagnostic matches the actual server campaign across 20 Hz midnight frames at "+start);
            Check(validHours&&oldDayHidden,"Fractional midnight immediately uses the correct day, bounded hour and current/future forecasts at "+start);
            Check(calls<=8,"Eighty fast packets invoke the actual sounding solver only at coarse sample/daily boundaries at "+start);
            samples.Add(new{StartHour=start,Frames=frames,ModelSamples=calls,MidnightSeconds=midnight,RolloverFrame=rolloverFrame});
        }
        foreach(double elapsed in new[]{1200.35,2399.999,3600.05,1000000.75,9999999.5})
        {
            const float start=13.37f;const int seed=73019;
            double seconds=Math.Floor(elapsed+1e-8),absolute=start+seconds/1200*24;
            int day=(int)Math.Floor(absolute/24)+1;
            float hour=Math.Clamp((float)(absolute-Math.Floor(absolute/24)*24),0,MathF.BitDecrement(24f));
            var baseline=StormChaseAtmosphere.Snapshot(seed,day,hour,seconds,100,new(6000,0,7000)) with{CampaignStartHour=start};
            var world=new WorldSnapshot(101,elapsed,seed,[],[],[],[],[],Atmosphere:baseline);
            var profile=StormChaseOnlineAtmosphere.Reconstruct(world,baseline);
            Check(profile.Hour is >=0 and <24&&Json(profile.Sounding)==Json(baseline.Sounding)&&Json(profile.Diagnostics)==Json(baseline.Diagnostics),
                "Resumed multi-day/large campaign clocks preserve exact sampled profiles at "+elapsed);
        }
        var guard=StormChaseAtmosphere.Snapshot(73019,1,8,0,0,new(6000,0,7000));
        var guardWorld=new WorldSnapshot(0,0,73019,[],[],[],[],[],Atmosphere:guard);
        var guarded=new AuthoritativeAtmosphereReplica(StormChaseOnlineAtmosphere.Reconstruct);guarded.Retain(guardWorld);
        bool seedRejected=false;try{guarded.Retain(guardWorld with{Seed=73020});}catch(InvalidDataException){seedRejected=true;}
        Check(seedRejected,"A connection cannot silently switch the seed used to reconstruct server profiles");
        guarded.Retain(guardWorld with{ServerTick=20,SimulationSeconds=1,Atmosphere=null});
        bool timeRejected=false;try{guarded.Retain(guardWorld with{ServerTick=21,SimulationSeconds=.5,Atmosphere=null});}catch(InvalidDataException){timeRejected=true;}
        Check(timeRejected,"A newer movement packet cannot roll the atmospheric campaign clock backwards");
        Check(!guarded.Apply(guard with{ServerTick=30,ModelVersion=2}),"An unsupported model version cannot replace the authoritative daily context");
        string path=Path.Combine(output,"online-atmosphere-reconstruction.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{Passed=true,Checks=checks,Samples=samples,
            Scope="Actual shared atmosphere solver compared with a live server campaign; UI reconstruction never generates authoritative storms or physics."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Online atmosphere reconstruction checks passed: {checks.Count}; {path}");return 0;
    }
}
