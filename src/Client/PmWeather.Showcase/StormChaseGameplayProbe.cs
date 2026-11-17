using System.Numerics;
using System.Text.Json;
using Engine.Physics;
using Engine.Physics.Collision;
using Engine.Physics.Queries;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static partial class Showcase
{
    private static int VerifyCampaignGameplay(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        var world = new StormChaseWorld(CreateCatalog());
        string savePath = Path.Combine(output, "career-fixture-" + Guid.NewGuid().ToString("N") + ".json");
        var career = new StormChaseCareer(world, savePath);
        var point = FindWallCloudSourcePoints().Single(p => p.Name == "wall-04-mature-touchdown");
        var parent = point.State.ActiveStorms.Single();
        var sourceTornado = parent.Tornadoes.Single();
        var camera = ChaseCaptureCamera(parent.Position + new Vector3f(-450,35,-1600), parent.Position + new Vector3f(0,600,0));
        var frame = new DefaultRenderFrameBuilder().Build(1, DateTimeOffset.UnixEpoch, new(1280,720),
            new RenderScene { Camera = camera, Environment = point.State, AnimationTimeSeconds = 42 });
        var tornado = frame.Weather!.ActiveStorms.Single().Tornadoes.Single();
        var probePosition = tornado.Position + new Vector3f(tornado.BaseRadiusMeters,0,-2);
        Check(career.DeployProbe(probePosition,0), "A starter probe can be placed ahead of the actual source tornado.");
        Check(career.DeployProbe(probePosition + new Vector3f(3,0,0),0), "The starter carries two deployable pods.");
        Check(!career.DeployProbe(probePosition,0), "The vehicle rack enforces real probe capacity.");
        decimal before = career.Save.Cash;
        for (int sample=0; sample<120; sample++) career.Update(sample * .2, sample*.2, frame.Weather);
        Check(career.Save.ProbeEarnings > 500 && career.Save.Cash > before,
            "Actual mapped tornado winds pay research income to probes in the vortex path.");
        Check(career.Probes.All(p=>p.PeakMph>=80&&p.Samples>50), "Field pods retain measured peak winds and sample histories.");
        decimal earned = career.Save.Cash;
        var noTornado = frame.Weather with { Storms = frame.Weather.ActiveStorms.Select(s=>s with { Tornadoes = [] }).ToArray() };
        career.Update(24,24,noTornado);
        Check(career.Save.Cash == earned, "A thunderstorm with no tornado does not pay phantom probe income.");
        Check(!career.Purchase("probe",new(0,0,0),25), "Equipment transactions require a real service station.");
        var station = world.GasStations[0];
        Check(career.Purchase("probe",station.ParkingPosition,25) && career.Save.ProbeLevel==1,
            "Probe research earnings buy reinforced equipment at the city station.");
        decimal afterPurchase = career.Save.Cash;
        Check(career.Purchase("probe",station.ParkingPosition,25) && career.Save.Cash==afterPurchase,
            "Already owned equipment is selected without a second charge.");
        career.Condition.FuelLiters = 20;
        career.Refueling = true;
        decimal preFuel = career.Save.Cash;
        career.UpdateFuel(1,station.ParkingPosition,0,26);
        Check(Math.Abs(career.Condition.FuelLiters-25)<.01f&&Math.Abs((double)(preFuel-career.Save.Cash)-6.75)<.01,
            "A stationary vehicle gains five litres for $6.75 at a functioning service site.");
        career.UpdateFuel(1,station.ParkingPosition,2,27);
        Check(!career.Refueling&&Math.Abs(career.Condition.FuelLiters-25)<.01f,
            "The fuel pump stops when the vehicle moves.");
        career.Flush(); var reloaded = new StormChaseCareer(world,savePath);
        Check(reloaded.Save.Cash==career.Save.Cash&&reloaded.Save.ProbeLevel==1&&Math.Abs(reloaded.Condition.FuelLiters-25)<.01f,
            "Cash, purchased equipment and vehicle fuel survive a new career instance.");
        career.Save.Cash = 3.33m; // Explicit fractional-affordability fixture.
        career.Refueling = true;
        career.UpdateFuel(10,station.ParkingPosition,0,27.5);
        Check(career.Save.Cash == 0 && Math.Abs(career.Condition.FuelLiters-(25+3.33f/1.35f))<.001f,
            "Spending the final fractional fuel balance cannot create negative cash.");
        career.Flush(); var emptyBalanceReload = new StormChaseCareer(world,savePath);
        Check(emptyBalanceReload.Save.Cash == 0 && emptyBalanceReload.Save.ProbeLevel == 1,
            "An exhausted fuel budget reloads the existing career without losing purchased equipment.");
        Check(!career.Purchase("interceptor",station.ParkingPosition,28), "Unaffordable vehicle purchases cannot overdraw the balance.");
        var damagedServiceSites = new HashSet<string>(StringComparer.Ordinal) { station.BuildingId };
        career.DestroyedBuildings = () => damagedServiceSites;
        career.Refueling = true;
        float fuelAtDamagedStation = career.Condition.FuelLiters;
        career.UpdateFuel(1,station.ParkingPosition,0,28);
        Check(!career.Refueling && career.Condition.FuelLiters == fuelAtDamagedStation && !career.AtServiceStation(station.ParkingPosition),
            "Destroyed station buildings disable their fuel pump and service area.");
        Check(!career.Purchase("camera",station.ParkingPosition,28), "Destroyed stations cannot sell equipment.");
        damagedServiceSites.Clear();
        career.Save.Cash = 300;
        career.Condition.Health = 30;
        career.VehiclePosition = () => new(-2300,world.HeightAt(-2300,-1000),-1000);
        Check(!career.Purchase("repair",station.ParkingPosition,28) && career.Save.Cash == 300 && career.Condition.Health == 30,
            "A player at a station cannot repair a vehicle left at a remote location.");
        career.VehiclePosition = () => world.GasStations[1].ParkingPosition;
        Check(!career.Purchase("repair",station.ParkingPosition,28) && career.Save.Cash == 300 && career.Condition.Health == 30,
            "A vehicle parked at another town's station cannot be repaired through this garage.");
        career.VehiclePosition = () => station.ParkingPosition;
        Check(career.Purchase("repair",station.ParkingPosition,28) && career.Save.Cash == 50 && career.Condition.Health == 100,
            "Bringing the damaged vehicle to a working station repairs it for the listed price.");
        career.Save.Cash=40000; // Explicit transaction fixture; never applied to the player's save.
        Check(career.Purchase("suv",station.ParkingPosition,28)&&career.SelectedVehicle.Id=="suv"&&career.Save.Cash==32000,
            "Buying the scout debits its price and unlocks its distinct vehicle specification.");
        Check(career.Purchase("interceptor",station.ParkingPosition,28)&&career.SelectedVehicle.Id=="interceptor"&&career.Save.Cash==8000,
            "Buying the armored interceptor unlocks heavier mass, anchors and six-pod capacity.");
        career.Purchase("pickup",station.ParkingPosition,28);
        using var destruction = new StormChaseDestruction(world);
        using var vehicles = new StormChaseVehicles(world,destruction,career);
        destruction.PersistentPhysicsAnchor=()=>vehicles.Position;
        // Open prairie provides an unobstructed native terrain drive fixture.
        vehicles.Spawn(new(-2300,world.HeightAt(-2300,-1000),-1000));
        var look = new FlyCamera();
        look.SetPose(new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z),
            new(vehicles.Position.X,vehicles.Position.Y+1.72f,vehicles.Position.Z+5));
        Check(vehicles.Interact(look,30)&&vehicles.Driving, "Entering a nearby owned vehicle activates driving.");
        var origin = vehicles.Position;
        float originalFuel=career.Condition.FuelLiters;
        var vehicleTrace = new List<object>();
        string tracePath = Path.Combine(output,"vehicle-physics-trace.json");
        void Trace(string stage, int tick) => vehicleTrace.Add(new { Stage=stage, Tick=tick, PhysicsSteps=destruction.PhysicsStepCount,
            State=vehicles.NativeDiagnostics });
        void FlushTrace() => File.WriteAllText(tracePath,JsonSerializer.Serialize(vehicleTrace,JsonOptions));
        Trace("spawn",0);
        for(int tick=0;tick<600;tick++)
        {
            vehicles.SetInput(1,0,false,null,1f/60);
            destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);
            vehicles.AfterPhysics(30+tick/60d);
            if(tick%30==0)Trace("accelerating",tick);
        }
        Trace("accelerated",600); FlushTrace();
        float distance=StormChaseCareer.HorizontalDistance(origin,vehicles.Position);
        Check(distance>40&&vehicles.SpeedMps>5,"W drives the actual native PhysX chassis across cooked terrain.");
        Check(career.Condition.FuelLiters<originalFuel,"Native driving distance consumes stored fuel.");
        var tireClearances = new List<float>();
        foreach (var item in vehicles.CreateRenderItems())
        {
            if (!world.Models.TryGetPart(item,out var part) || !part.Name.StartsWith("tire",StringComparison.Ordinal)) continue;
            var hub = new Vector3(item.Position.X,item.Position.Y,item.Position.Z);
            Check(destruction.Physics.TryRaycast(new PhysicsRay(hub-Vector3.UnitY*.2f,-Vector3.UnitY),2,out var ground),
                "An authored tire remains above a native terrain contact.");
            tireClearances.Add(hub.Y-Math.Max(part.SizeMeters.X,part.SizeMeters.Z)*.5f-ground.Point.Y);
        }
        Check(tireClearances.Count==4&&tireClearances.All(clearance=>Math.Abs(clearance)<.1f),
            "The four visible Blender tires follow native terrain without sinking or hovering.");
        var trackingCamera = vehicles.Camera(look);
        Check(Math.Abs(MathF.IEEERemainder(MathF.Atan2(trackingCamera.Forward.X,trackingCamera.Forward.Z)-vehicles.YawRadians,MathF.Tau))<.01f,
            "Entering the vehicle aligns the chase camera with the actual chassis heading.");
        look.Look(160,-30);
        var orbitCamera = vehicles.Camera(look);
        float orbit = MathF.IEEERemainder(MathF.Atan2(orbitCamera.Forward.X,orbitCamera.Forward.Z)-vehicles.YawRadians,MathF.Tau);
        Check(Math.Abs(orbit-.4f)<.01f&&orbitCamera.Forward.Y>trackingCamera.Forward.Y,
            "Mouse input orbits and pitches the driving camera independently.");
        float yaw=vehicles.YawRadians;
        for(int tick=0;tick<180;tick++)
        {
            vehicles.SetInput(.5f,1,false,null,1f/60);
            destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces); vehicles.AfterPhysics(40+tick/60d);
            if(tick%30==0)Trace("steering",tick);
        }
        Trace("steered",180); FlushTrace();
        Check(Math.Abs(MathF.IEEERemainder(vehicles.YawRadians-yaw,MathF.Tau))>.2,
            "Steering torque turns the collision-resolved native vehicle.");
        var turnedCamera = vehicles.Camera(look);
        Check(Math.Abs(MathF.IEEERemainder(MathF.Atan2(turnedCamera.Forward.X,turnedCamera.Forward.Z)-vehicles.YawRadians-orbit,MathF.Tau))<.01f,
            "The mouse orbit stays attached to the chassis as native steering turns it.");
        var headlights = vehicles.CreateHeadlights();
        Check(headlights.Count==2&&headlights.All(light=>light.LightType==RenderLightType.Spot&&light.Range==150&&light.Intensity>0
            &&Math.Abs(MathF.IEEERemainder(MathF.Atan2(light.Direction.X,light.Direction.Z)-vehicles.YawRadians,MathF.Tau))<.01f),
            "Two active headlights aim along the actual turned chassis.");
        for(int tick=0;tick<180;tick++)
        {
            vehicles.SetInput(0,0,true,null,1f/60);
            destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces); vehicles.AfterPhysics(43+tick/60d);
            if(tick%30==0)Trace("braking",tick);
        }
        Trace("braked",180); FlushTrace();
        Check(vehicles.SpeedMps<2,"The handbrake stops the native vehicle.");
        Check(vehicles.Interact(look,47)&&!vehicles.Driving&&look.Grounded,"Exiting a stopped vehicle restores on-foot movement.");
        Check(vehicles.CreateHeadlights().Count==0,"The parked vehicle switches its driving headlights off.");

        // A real static collision in the shared scene must stop propulsion;
        // driving tests must never pass by integrating a visual transform.
        vehicles.Spawn(origin);
        look.SetPosition(new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z));
        Check(vehicles.Interact(look,48),"The stopped vehicle can be entered again.");
        float barrierZ=origin.Z+45;
        var barrier=destruction.Physics.CreateBody(new("fixture/vehicle-barrier",
            new(origin.X,world.HeightAt(origin.X,barrierZ)+4,barrierZ),Vector3.Zero,0,
            new BoxColliderShape(new(8,4,1)),PhysicsBodyType.Static));
        float furthestZ=origin.Z;
        try
        {
            for(int tick=0;tick<400;tick++)
            {
                vehicles.SetInput(1,0,false,null,1f/60);
                destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);
                furthestZ=Math.Max(furthestZ,vehicles.Position.Z);
                if(tick%30==0)Trace("native-collision",tick);
            }
            Trace("collision-resolved",400); FlushTrace();
            Check(furthestZ>origin.Z+30&&furthestZ<barrierZ-2&&vehicles.SpeedMps<1,
                "A native solid obstacle blocks the actual authored chassis while W remains held.");
        }
        finally { destruction.Physics.RemoveBody(barrier); }

        vehicles.Spawn(origin);
        for(int tick=0;tick<120;tick++)
        {
            vehicles.SetInput(0,0,false,null,1f/60);
            destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);
        }
        var windOrigin=vehicles.Position;
        float healthBeforeWind=career.Condition.Health;
        var relocatedAxis=windOrigin+new Vector3f(-tornado.BaseRadiusMeters,0,0);
        var windTornado=tornado with { Position=relocatedAxis,Velocity=default };
        var windWeather=frame.Weather with { Storms=[frame.Weather.ActiveStorms.Single() with
            { Position=relocatedAxis-tornado.LocalOffset,Velocity=default,Tornadoes=[windTornado] }] };
        float measuredWind=StormChaseDestruction.SampleWindSpeedMph(windOrigin,windWeather);
        for(int tick=0;tick<180;tick++)
        {
            vehicles.SetInput(0,0,false,windWeather,1f/60);
            destruction.Update(1f/60,windWeather,vehicles.Position,vehicles.StepForces);
            if(tick%30==0)Trace("tornado-wind",tick);
        }
        Trace("wind-driven",180); FlushTrace();
        float windDistance=StormChaseCareer.HorizontalDistance(windOrigin,vehicles.Position);
        Check(measuredWind>career.SelectedVehicle.WindToleranceMph&&windDistance>3&&vehicles.SpeedMps>2
            &&career.Condition.Health<healthBeforeWind,
            "Actual source tornado wind displaces and damages the native vehicle with no driving input.");
        // The Blender classic chase pickup: buy it, spawn it, drive it on the native terrain.
        var truckModel=world.Models.GetModel("chase-truck");
        float truckCollisionTop=truckModel.Parts.Where(part=>!part.Name.StartsWith("tire",StringComparison.Ordinal)&&!part.Name.StartsWith("hub",StringComparison.Ordinal)
            &&!part.Name.StartsWith("weather-",StringComparison.Ordinal)&&!part.Name.StartsWith("mirror",StringComparison.Ordinal)
            &&!part.Name.StartsWith("mud-flap",StringComparison.Ordinal))
            .Max(part=>part.LocalPositionMeters.Y+part.SizeMeters.Y/2);
        Check(truckModel.Parts.Count(part=>part.Name.StartsWith("tire",StringComparison.Ordinal))==4&&truckCollisionTop<2.05f
            &&truckModel.Parts.Any(part=>part.Name.StartsWith("weather-anemo",StringComparison.Ordinal)),
            "The chase pickup has four physics tires and keeps its roof mesonet out of the collision box.");
        career.Save.Cash+=5000;
        // Stop and step out of the wind-tested vehicle first (a moving vehicle cannot be exited).
        for(int tick=0;tick<240;tick++){vehicles.SetInput(0,0,true,null,1f/60);destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);}
        if(vehicles.Driving)Check(vehicles.Interact(look,60)&&!vehicles.Driving,"The previous vehicle is parked and exited before switching.");
        Check(career.Purchase("chase-truck",station.ParkingPosition,60)&&career.SelectedVehicle.Id=="chase-truck"&&career.Save.OwnedVehicles.Contains("chase-truck"),
            "The classic chase pickup is bought at a service station and becomes the active vehicle.");
        bool boughtMesonet=career.Save.Mesonet;career.Save.Mesonet=false;
        Check(career.HasMesonet,"The chase pickup's factory mesonet rack earns field data without the separate upgrade.");
        career.Save.Mesonet=boughtMesonet;
        vehicles.SwitchIfChanged(station.ParkingPosition,true);
        var truckItems=vehicles.CreateRenderItems();
        Check(truckItems.Count==truckModel.Parts.Count&&truckItems.All(item=>world.Models.TryGetPart(item,out var p)&&truckModel.Parts.Contains(p)),
            "The spawned vehicle renders every Blender part of the chase pickup.");
        for(int tick=0;tick<120;tick++){vehicles.SetInput(0,0,true,null,1f/60);destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);}
        look.SetPose(new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z),new(vehicles.Position.X,vehicles.Position.Y+1.72f,vehicles.Position.Z+5));
        Check(vehicles.Interact(look,61)&&vehicles.Driving,"The chase pickup can be entered.");
        var truckOrigin=vehicles.Position;
        for(int tick=0;tick<600;tick++)
        {
            vehicles.SetInput(1,0,false,null,1f/60);
            destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);
            vehicles.AfterPhysics(62+tick/60d);
        }
        float truckDistance=StormChaseCareer.HorizontalDistance(truckOrigin,vehicles.Position);
        var truckClearances=new List<float>();
        foreach(var item in vehicles.CreateRenderItems())
        {
            if(!world.Models.TryGetPart(item,out var part)||!part.Name.StartsWith("tire",StringComparison.Ordinal))continue;
            var hub=new Vector3(item.Position.X,item.Position.Y,item.Position.Z);
            if(destruction.Physics.TryRaycast(new PhysicsRay(hub-Vector3.UnitY*.2f,-Vector3.UnitY),2,out var ground))
                truckClearances.Add(hub.Y-Math.Max(part.SizeMeters.X,part.SizeMeters.Z)*.5f-ground.Point.Y);
        }
        Console.WriteLine($"Chase pickup: drove {truckDistance:0} m, {vehicles.SpeedMph:0} mph; tire clearances {string.Join(", ",truckClearances.Select(c=>c.ToString("0.00")))} m; collision top {truckCollisionTop:0.00} m.");
        Check(truckDistance>40&&vehicles.SpeedMps>5&&truckClearances.Count==4&&truckClearances.All(c=>Math.Abs(c)<.1f),
            "The chase pickup drives the native PhysX chassis with its four tires on the terrain.");
        // Player jobs: each department vehicle is issued at the station, drives, and flashes its beacons.
        var dutyReport=new List<string>();
        foreach(var job in StormChaseJobs.All.Where(j=>j.DutyVehicle is not null))
        {
            for(int tick=0;tick<240;tick++){vehicles.SetInput(0,0,true,null,1f/60);destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);}
            if(vehicles.Driving)Check(vehicles.Interact(look,70)&&!vehicles.Driving,"The previous vehicle is parked and exited before a job change.");
            Check(career.SelectJob(job.Id,station.ParkingPosition,70)&&career.SelectedVehicle.Id==job.DutyVehicle,
                $"Going on duty as {job.Title} issues the {job.DutyVehicle}.");
            vehicles.SwitchIfChanged(station.ParkingPosition);
            var dutyModel=world.Models.GetModel(job.DutyVehicle!);
            Check(vehicles.CreateRenderItems().Count==dutyModel.Parts.Count&&dutyModel.Parts.Count(p=>p.Name.StartsWith("tire",StringComparison.Ordinal))==4,
                $"The {job.DutyVehicle} spawns with every Blender part and four physics tires.");
            for(int tick=0;tick<120;tick++){vehicles.SetInput(0,0,true,null,1f/60);destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);}
            look.SetPose(new(vehicles.Position.X-4,vehicles.Position.Y+1.72f,vehicles.Position.Z),new(vehicles.Position.X,vehicles.Position.Y+1.72f,vehicles.Position.Z+5));
            Check(vehicles.Interact(look,71)&&vehicles.Driving,$"The {job.DutyVehicle} can be entered.");
            var dutyOrigin=vehicles.Position;
            for(int tick=0;tick<600;tick++)
            {
                vehicles.SetInput(1,0,false,null,1f/60);
                destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);
                vehicles.AfterPhysics(72+tick/60d);
            }
            float dutyDistance=StormChaseCareer.HorizontalDistance(dutyOrigin,vehicles.Position);
            var clearances=new List<float>();
            foreach(var item in vehicles.CreateRenderItems())
            {
                if(!world.Models.TryGetPart(item,out var part)||!part.Name.StartsWith("tire",StringComparison.Ordinal))continue;
                var hub=new Vector3(item.Position.X,item.Position.Y,item.Position.Z);
                if(destruction.Physics.TryRaycast(new PhysicsRay(hub-Vector3.UnitY*.2f,-Vector3.UnitY),2,out var ground))
                    clearances.Add(hub.Y-Math.Max(part.SizeMeters.X,part.SizeMeters.Z)*.5f-ground.Point.Y);
            }
            var lights=vehicles.CreateHeadlights();
            var beacons=lights.Where(l=>l.Id.StartsWith("vehicle-beacon",StringComparison.Ordinal)).ToArray();
            dutyReport.Add($"{job.DutyVehicle}: {dutyDistance:0} m at {vehicles.SpeedMph:0} mph, tires {string.Join("/",clearances.Select(c=>c.ToString("0.00")))} m, {beacons.Length} beacons");
            Check(dutyDistance>30&&vehicles.SpeedMps>5&&clearances.Count==4&&clearances.All(c=>Math.Abs(c)<.1f),
                $"The {job.DutyVehicle} drives the native PhysX chassis with its tires on the terrain.");
            Check(lights.Count==4&&beacons.Length==2&&beacons.Count(b=>b.Intensity>5)==1,
                $"The {job.DutyVehicle} flashes two alternating beacons beside its headlights.");
        }
        Console.WriteLine("Department vehicles: "+string.Join("; ",dutyReport));
        for(int tick=0;tick<240;tick++){vehicles.SetInput(0,0,true,null,1f/60);destruction.Update(1f/60,null,vehicles.Position,vehicles.StepForces);}
        if(vehicles.Driving)vehicles.Interact(look,90);
        Check(career.SelectJob(StormChaseJobs.Chaser,station.ParkingPosition,90)&&career.SelectedVehicle.Id=="chase-truck",
            "Going off duty returns the player's own chase pickup.");
        File.WriteAllText(Path.Combine(output,"campaign-gameplay.json"),JsonSerializer.Serialize(new
        {Checks=checks,Passed=checks.Count,ProbeEarnings=career.Save.ProbeEarnings,NativeDriveDistanceMeters=distance,
            TireClearancesMeters=tireClearances,NativeBarrierFurthestZ=furthestZ,NativeWindDistanceMeters=windDistance,
            NativeMeasuredWindMph=measuredWind,NativeVehicleTrace=tracePath,
            NativePhysicsProvider=destruction.NativeProvider,PhysicsSteps=destruction.PhysicsStepCount,FixtureSave=savePath,
            PhotoVerification="Paid native PNG receipts are verified by the interactive/photo capture probe, not this wind/transaction/drive fixture."},JsonOptions));
        Console.WriteLine($"Campaign gameplay checks passed: {checks.Count}; {output}");
        return 0;
    }
}
