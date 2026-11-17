using Shared.Contracts.World;
using StormChase.Protocol;

namespace StormChase.Server;

public sealed partial class GameAuthority
{
    private StormChaseHousing? housing;
    private StormChaseFurniturePhysics? furniturePhysics;
    private readonly Dictionary<string,HousingAccountDto> housingAccounts=[];
    private readonly Dictionary<int,HomeSiteState> housingClaims=[];
    private readonly Dictionary<uint,(int Home,string Message)> housingUi=[];
    private long lastHousingTick=-1,lastHousingRefresh=-100,lastHousingSave;
    private void InitHousing(StormChaseWorld source)
    {
        housing=new(source);foreach(var sites in housing.Homes.Chunk(10))store.RegisterHousingSites(new(source.ContentFingerprint,sites));
        foreach(var claim in store.HouseClaims(source.ContentFingerprint))housingClaims[claim.HomeId]=claim;
        furniturePhysics=new(source,destruction!,store.WorldFurniture(source.ContentFingerprint));
    }
    private HousingAccountDto Account(Player p,bool force=false)
    {
        string id=p.Character.CharacterId;
        if(force||!housingAccounts.TryGetValue(id,out var saved))
        {saved=store.GetHousing(id,world!.ContentFingerprint);housingAccounts[id]=saved;furniturePhysics!.AddUnknown(saved.Furniture);if(saved.Home is {} home)housingClaims[home.HomeId]=home with{Mine=false};}
        return saved;
    }
    private void HousingUpdate(Player p)
    {
        if(housing is null||furniturePhysics is null||world is null)return;
        _=Account(p);
        HousingWorldUpdate();
    }
    private void HousingWorldUpdate()
    {
        if(housing is null||furniturePhysics is null||world is null)return;
        if(lastHousingTick==tick)return;lastHousingTick=tick;
        if(tick-lastHousingRefresh>=100)
        {
            lastHousingRefresh=tick;
            foreach(var claim in store.HouseClaims(world.ContentFingerprint))housingClaims[claim.HomeId]=claim;
            foreach(var player in players.Values)_=Account(player,true);
        }
        furniturePhysics.Update(TickDelta,renderWeather,players.Values.SelectMany(v=>new[]{v.Feet,v.Vehicle.Position}).ToArray(),players.Values.Select(v=>v.Character.CharacterId).ToHashSet());
        if(tick-lastHousingSave>=100){lastHousingSave=tick;PersistHousing();}
    }
    private void PersistHousing()
    {
        if(world is null||furniturePhysics is null)return;
        var owners=players.Values.Select(p=>p.Character.CharacterId).ToHashSet();
        // Only placed objects need pose writes. Inventory/deed purchases already
        // committed atomically at the master. A character's active lease decides
        // which game host may persist its wind-driven objects.
        foreach(var state in furniturePhysics.States.Where(s=>s.Placed&&owners.Contains(s.OwnerCharacterId)))
            store.SaveFurniturePose(state.OwnerCharacterId,new(world.ContentFingerprint,state,DateTimeOffset.UtcNow.UtcTicks));
    }
    private void PersistPlayerHousing(Player p)
    {
        if(world is null||furniturePhysics is null)return;
        foreach(var state in furniturePhysics.States.Where(s=>s.Placed&&s.OwnerCharacterId==p.Character.CharacterId))
            store.SaveFurniturePose(p.Character.CharacterId,new(world.ContentFingerprint,state,DateTimeOffset.UtcNow.UtcTicks));
        housingAccounts.Remove(p.Character.CharacterId);housingUi.Remove(p.Id);
    }
    private bool HousingAction(Player p,GameplayAction a,out ActionResult result)
    {
        result=null!;if(a.Kind is not(GameplayActionKind.BuyHouse or GameplayActionKind.BuyFurniture or GameplayActionKind.PlaceFurniture or GameplayActionKind.VisitHome or GameplayActionKind.SetHomeVisits))return false;
        bool success=false;string message="Housing is loading.";
        if(housing is not null&&furniturePhysics is not null&&world is not null)
        {
            try
            {
                string owner=p.Character.CharacterId,receipt=$"housing:{bootId}:{p.Id}:{a.RequestId}";var account=Account(p);
                switch(a.Kind)
                {
                    case GameplayActionKind.BuyHouse:
                        var site=housing.Home(a.TargetId)??throw new ArgumentException("Unknown house.");
                        if(p.Vehicle.Driving||HousingDistance(p.Feet,site.Entrance)>18)message="Walk to this house's front entrance before buying it.";
                        else if(destruction!.IsBuildingDestroyed(site.BuildingId))message="This storm-damaged house cannot be purchased.";
                        else if(StormChaseDestruction.SampleWindSpeedMph(p.Feet,renderWeather)>45)message="Wait until dangerous winds pass before signing a deed.";
                        else
                        {p.Career.PersistVehicle();var bought=store.BuyHome(owner,new(world.ContentFingerprint,site.HomeId,receipt));success=bought.Purchased;message=bought.Message;_ =Account(p,true);p.Career.Refresh(false);}
                        break;
                    case GameplayActionKind.BuyFurniture:
                        p.Career.PersistVehicle();var purchase=store.BuyFurniture(owner,new(world.ContentFingerprint,a.TargetId,receipt));success=purchase.Purchased;message=purchase.Message;_=Account(p,true);p.Career.Refresh(false);
                        break;
                    case GameplayActionKind.PlaceFurniture:
                        var item=furniturePhysics.Get(a.TargetId);
                        if(account.Home is not {} home||item is null||item.OwnerCharacterId!=owner||item.HomeId!=home.HomeId)message="Choose furniture you own.";
                        else if(p.Vehicle.Driving||!housing.IsInsideHome(home.HomeId,p.Feet))message="Enter your own home to place furnishings.";
                        else if(destruction!.IsBuildingDestroyed(home.BuildingId))message="This house is storm damaged. Furnish an intact home.";
                        else if(StormChaseDestruction.SampleWindSpeedMph(p.Feet,renderWeather)>45)message="Wait for safe conditions before moving furniture.";
                        else if(housing.TryPlacement(home.HomeId,item.Sku,a.Slot,a.PlacementYaw,furniturePhysics.States,item.FurnitureId,out var pos,out var rotation,out message))
                        {
                            var placed=item with{Position=pos,Rotation=rotation,LinearVelocity=default,AngularVelocity=default,Placed=true,Sleeping=true,CollisionEnabled=false,Archived=true};
                            store.SaveFurniturePose(owner,new(world.ContentFingerprint,placed,DateTimeOffset.UtcNow.UtcTicks));furniturePhysics.Put(placed);success=true;
                        }
                        break;
                    case GameplayActionKind.VisitHome:
                        var destination=housing.Home(a.TargetId);
                        if(destination is null)message="Unknown house.";
                        else if(!housingClaims.TryGetValue(a.TargetId,out var visit))
                        {housingUi[p.Id]=(destination.HomeId,"Property marker set. Drive or walk to its front door.");success=true;message=housingUi[p.Id].Message;}
                        else if(visit.OwnerCharacterId!=owner&&(!visit.VisitsAllowed||!players.Values.Any(v=>v.Character.CharacterId==visit.OwnerCharacterId)))message="That host is offline or has closed their home to visits.";
                        else{housingUi[p.Id]=(visit.HomeId,"Visit marker set. Drive or walk to "+visit.OwnerName+"'s home.");success=true;message=housingUi[p.Id].Message;}
                        break;
                    case GameplayActionKind.SetHomeVisits:
                        var settings=store.SetHomeVisits(owner,new(world.ContentFingerprint,a.TargetId!=0,receipt));success=settings.Purchased;message=settings.Message;_=Account(p,true);break;
                }
            }
            catch(Exception e)when(e is ArgumentException or CareerConflictException or KeyNotFoundException){message=e.Message;}
        }
        var ui=housingUi.GetValueOrDefault(p.Id);housingUi[p.Id]=(ui.Home,message);result=new(a.RequestId,success,message,Progress(p));return true;
    }
    private WorldSnapshot HousingPersonalize(WorldSnapshot snapshot,Player p)
    {
        if(housing is null||furniturePhysics is null||world is null)return snapshot;
        var own=Account(p);HomeSiteState Decorate(HomeSiteState site)
        {var claimed=housingClaims.GetValueOrDefault(site.HomeId)??site;return claimed with{Mine=claimed.OwnerCharacterId==p.Character.CharacterId,Destroyed=destruction!.IsBuildingDestroyed(site.BuildingId)};}
        var connected=players.Values.Select(v=>v.Character.CharacterId).ToHashSet();
        // Decorate only the displayed shortlist; destruction checks should not
        // rescan every house for every player's 10 Hz personalized snapshot.
        var preferred=housingClaims.Values.Where(h=>h.OwnerCharacterId==p.Character.CharacterId);
        var nearby=housing.Homes.Where(h=>!housingClaims.ContainsKey(h.HomeId)).OrderBy(h=>HousingDistance(p.Feet,h.Entrance)).Take(16);
        var sites=preferred.Concat(nearby).DistinctBy(h=>h.HomeId).Take(16).Select(Decorate).ToArray();
        var visitors=players.Values.Select(v=>
        {
            var h=Account(v).Home;return new HomeVisitorState(v.Id,v.Character.Name,h?.HomeId??0,h is not null&&HousingDistance(v.Feet,h.Interior)<9,h?.VisitsAllowed??false);
        }).Where(v=>v.HomeId!=0).ToArray();
        var furniture=furniturePhysics.States.Where(f=>f.OwnerCharacterId==p.Character.CharacterId||f.Placed&&HousingDistance(p.Feet,f.Position)<280)
            .OrderBy(f=>f.OwnerCharacterId==p.Character.CharacterId?0:1).ThenBy(f=>HousingDistance(p.Feet,f.Position)).ThenBy(f=>f.FurnitureId).Take(96).ToArray();
        var ui=housingUi.GetValueOrDefault(p.Id);var target=housing.Home(ui.Home);
        return snapshot with{Housing=new(own.Home is {} home?Decorate(home):null,sites,StormChaseHousing.Catalogue.Select(c=>c.Offer).ToArray(),furniture,visitors,ui.Home,target?.Entrance??default,ui.Message??"Buy an available home, furnish it, and invite other chasers.")};
    }
    private void DisposeHousing()
    {PersistHousing();furniturePhysics?.Dispose();furniturePhysics=null;}
    private static float HousingDistance(Vector3f p,NetVector3 q)=>MathF.Sqrt((p.X-q.X)*(p.X-q.X)+(p.Z-q.Z)*(p.Z-q.Z));
}
