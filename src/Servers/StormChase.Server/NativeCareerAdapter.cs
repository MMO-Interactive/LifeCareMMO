using Shared.Contracts.World;
using StormChase.Server;

// The shared native vehicle source depends on this narrow career adapter. Online
// currency and ownership remain in CareerStore; no local player save is loaded.
internal sealed record ChaseVehicleSpec(string Id,string Name,decimal Price,float MassKg,float TopSpeedMps,
    float FuelCapacityLiters,float WindToleranceMph,int ProbeCapacity);
internal sealed class ChaseVehicleCondition { public float FuelLiters {get;set;} public float Health {get;set;}=100; }
internal sealed class StormChaseCareer
{
    private readonly StormChaseWorld world;
    private readonly CareerStore store;
    private readonly string characterId;
    private CareerDto state;
    private readonly Dictionary<string,ChaseVehicleCondition> conditions=[];
    private static readonly ChaseVehicleSpec[] Specs=
    [new("pickup","Pickup",0,1900,42,75,95,2),new("suv","Chase SUV",8000,2350,49,90,125,4),new("interceptor","Tornado Interceptor",24000,4800,36,125,175,6),
     new("chase-truck","Classic chase pickup",4500,2100,46,95,105,3),
     // Department vehicles (types 4-6), issued with a player job and never owned or persisted in CareerStore.
     new("police-cruiser","Sheriff cruiser",0,1800,58,70,90,2),new("fire-engine","Fire engine",0,14000,31,190,140,0),new("ambulance","Ambulance",0,4200,40,110,105,0)];
    public StormChaseCareer(StormChaseWorld world,CareerStore store,string characterId)
    { this.world=world;this.store=store;this.characterId=characterId;state=store.GetCareer(characterId);Refresh(); }
    /// <summary>On duty, the job's department vehicle replaces the player's own; its condition lives only in memory.</summary>
    public string? DutyVehicle {get;set;}
    private readonly Dictionary<string,ChaseVehicleCondition> dutyConditions=[];
    public ChaseVehicleSpec SelectedVehicle=>Specs.Single(v=>v.Id==(DutyVehicle??state.SelectedVehicle));
    public ChaseVehicleCondition Condition=>DutyVehicle is { } duty
        ? dutyConditions.TryGetValue(duty,out var issued)?issued:dutyConditions[duty]=new(){FuelLiters=SelectedVehicle.FuelCapacityLiters}
        : conditions[state.SelectedVehicle];
    /// <summary>A department vehicle is handed over fuelled and repaired.</summary>
    public void ServiceDutyVehicle(){ if(DutyVehicle is { } duty) dutyConditions[duty]=new(){FuelLiters=SelectedVehicle.FuelCapacityLiters,Health=100}; }
    public bool ShopOpen {get;set;}
    public bool Refueling {get;set;}
    public string Message {get;private set;}="";
    public decimal PendingResearchReward {get;private set;}
    public Func<IReadOnlySet<string>>? DestroyedBuildings {get;set;}
    public CareerDto State=>state;
    public void Refresh(bool preserveConditions=true)
    {
        state=store.GetCareer(characterId);
        foreach(var pair in state.VehicleConditions)
            if(!preserveConditions||!conditions.ContainsKey(pair.Key)) conditions[pair.Key]=new(){FuelLiters=pair.Value.FuelLiters,Health=pair.Value.Health};
    }
    public void Notify(string message,double seconds)=>Message=message;
    public bool AtServiceStation(Vector3f position)=>NearestStation(position) is { } station&&HorizontalDistance(position,station.Position)<=70;
    public StormChaseGasStation? NearestStation(Vector3f position)=>world.GasStations.Where(s=>DestroyedBuildings?.Invoke().Contains(s.BuildingId)!=true).OrderBy(s=>HorizontalDistance(position,s.Position)).FirstOrDefault();
    public void RecordDriving(float distance,float speed,float wind,float delta)
    {
        Condition.FuelLiters=Math.Max(0,Condition.FuelLiters-distance*.00013f-delta*.0025f);
        PendingResearchReward+=(decimal)(Math.Max(0,distance)*.025f);
        if((state.Mesonet||SelectedVehicle.Id=="chase-truck")&&wind>=40) PendingResearchReward+=(decimal)(Math.Max(0,delta)*(wind-25)*.025f);
    }
    public decimal DrainRewards() { var reward=decimal.Round(PendingResearchReward,2);PendingResearchReward-=reward;return reward; }
    public void UpdateFuel(float delta,Vector3f position,float speed,double seconds)
    {
        if(!Refueling)return;
        if(!AtServiceStation(position)||speed>.5f){Refueling=false;return;}
        if(DutyVehicle is not null)
        {   // The department pays for its own vehicles' fuel; nothing is charged or persisted.
            Condition.FuelLiters=Math.Min(SelectedVehicle.FuelCapacityLiters,Condition.FuelLiters+delta*5);
            if(Condition.FuelLiters>=SelectedVehicle.FuelCapacityLiters-.01f)Refueling=false;
            return;
        }
        state=store.Refuel(characterId,Math.Min(2,delta*5),Condition.FuelLiters,Condition.Health,SelectedVehicle.FuelCapacityLiters);
        Condition.FuelLiters=state.VehicleConditions[state.SelectedVehicle].FuelLiters;
        if(Condition.FuelLiters>=SelectedVehicle.FuelCapacityLiters-.01f||state.Cash<=0)Refueling=false;
    }
    public void Tow(Vector3f position,double seconds)
    {
        state=store.Recover(characterId);Refresh(false);
        Notify("Roadside recovery: up to $200.",seconds);
    }
    public void PersistVehicle(){ if(DutyVehicle is null) store.UpdateVehicle(characterId,Condition.FuelLiters,Condition.Health); }
    internal static float HorizontalDistance(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
}
