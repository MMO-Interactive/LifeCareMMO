using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal sealed partial class ChaseCareerSave
{
    public string HousingWorld {get;set;}="";
    public string HousingOwnerId {get;set;}=Guid.NewGuid().ToString("N");
    public string HousingOwnerName {get;set;}="Your chaser";
    public int OwnedHomeId {get;set;}
    public bool HomeVisitsAllowed {get;set;}=true;
    public List<FurnitureState> Furniture {get;set;}=[];
    public HashSet<string> HousingReceipts {get;set;}=[];
}

internal sealed partial class StormChaseCareer
{
    private int housingVisitTarget;
    public string HousingOwnerId=>Save.HousingOwnerId;
    public string HousingOwnerName {get=>Save.HousingOwnerName;set=>Save.HousingOwnerName=string.IsNullOrWhiteSpace(value)?"Your chaser":value[..Math.Min(value.Length,32)];}
    public IEnumerable<FurnitureState> SavedFurniture=>world.IsHousingCompatible(Save.HousingWorld)?Save.Furniture:[];
    public void SaveFurniturePoses(IEnumerable<FurnitureState> poses,bool flush=false)
    {
        if(!world.IsHousingCompatible(Save.HousingWorld))return;
        Save.Furniture=poses.Where(f=>f.OwnerCharacterId==Save.HousingOwnerId&&f.HomeId==Save.OwnedHomeId).Take(24).Select(f=>f with{CollisionEnabled=false}).ToList();if(flush)Flush();
    }
    public HousingState HousingSnapshot(StormChaseHousing housing,Vector3f observer,IEnumerable<FurnitureState>? live=null)
    {
        HomeSiteState Decorate(HomeSiteState h)=>h.HomeId==Save.OwnedHomeId&&world.IsHousingCompatible(Save.HousingWorld)?
            h with{OwnerCharacterId=Save.HousingOwnerId,OwnerName=Save.HousingOwnerName,Mine=true,VisitsAllowed=Save.HomeVisitsAllowed,Destroyed=DestroyedBuildings?.Invoke().Contains(h.BuildingId)==true}:h with{Destroyed=DestroyedBuildings?.Invoke().Contains(h.BuildingId)==true};
        var own=housing.Home(Save.OwnedHomeId);var target=housing.Home(housingVisitTarget);
        var nearby=housing.Homes.OrderBy(h=>HorizontalDistance(observer,new(h.Entrance.X,h.Entrance.Y,h.Entrance.Z))).Take(16);
        var homes=(own is not null?new[]{own}.Concat(nearby):nearby).DistinctBy(h=>h.HomeId).Take(16).Select(Decorate).ToArray();
        return new(own is not null&&world.IsHousingCompatible(Save.HousingWorld)?Decorate(own):null,homes,StormChaseHousing.Catalogue.Select(c=>c.Offer).ToArray(),(live??SavedFurniture).ToArray(),[],housingVisitTarget,target?.Entrance??default,Message);
    }
    public bool HousingAction(StormChaseHousing housing,StormChaseFurniturePhysics physics,GameplayActionKind kind,int target,int slot,float yaw,
        Vector3f feet,bool driving,RenderWeatherState? weather,double seconds,string receipt)
    {
        if(receipt.Length is <1 or >96)return false;
        if(Save.HousingReceipts.Contains(receipt)){Notify("This housing action was already completed.",seconds);return true;}
        if(Save.HousingWorld!=""&&!world.IsHousingCompatible(Save.HousingWorld)){Notify("This character's home belongs to a different world version.",seconds);return false;}
        var home=housing.Home(Save.OwnedHomeId);decimal cash=Save.Cash;int homeId=Save.OwnedHomeId;string oldWorld=Save.HousingWorld;bool visits=Save.HomeVisitsAllowed;var oldFurniture=Save.Furniture;
        string message="Choose an available housing action.";bool success=false;FurnitureState? newPose=null;
        float Range(NetVector3 p)=>HorizontalDistance(feet,new(p.X,p.Y,p.Z));
        switch(kind)
        {
            case GameplayActionKind.BuyHouse:
                var site=housing.Home(target);
                if(site is null)message="Unknown house.";
                else if(homeId!=0)message="You already own a home.";
                else if(driving||Range(site.Entrance)>18)message="Walk to the front entrance to buy this house.";
                else if(DestroyedBuildings?.Invoke().Contains(site.BuildingId)==true)message="This house is storm damaged.";
                else if(StormChaseDestruction.SampleWindSpeedMph(feet,weather)>45)message="Wait for dangerous winds to pass.";
                else if(Save.Cash*100<site.PriceCents)message="Insufficient funds. Field contracts and storm research pay for your home.";
                else{Save.Cash-=site.PriceCents/100m;Save.OwnedHomeId=site.HomeId;Save.HousingWorld=world.ContentFingerprint;success=true;message="Home purchased. Enter through its front door to furnish it.";}
                break;
            case GameplayActionKind.BuyFurniture:
                var offer=StormChaseHousing.Furniture(target)?.Offer;
                if(home is null)message="Buy a home before purchasing furniture.";
                else if(offer is null)message="Unknown furnishing.";
                else if(Save.Furniture.Count>=24)message="This home has reached its 24 item furnishing limit.";
                else if(Save.Cash*100<offer.PriceCents)message="Insufficient funds.";
                else
                {
                    int id=Save.Furniture.Count==0?1:Save.Furniture.Max(f=>f.FurnitureId)+1;
                    var item=new FurnitureState(id,home.HomeId,Save.HousingOwnerId,offer.Sku,home.Interior,NetQuaternion.Identity,default,default,false,true,false,true);
                    Save.Cash-=offer.PriceCents/100m;Save.Furniture=[..Save.Furniture,item];newPose=item;success=true;message="Furniture purchased. Enter your home and choose a clear slot.";
                }
                break;
            case GameplayActionKind.PlaceFurniture:
                var furnishing=physics.Get(target);
                if(home is null||furnishing is null||furnishing.OwnerCharacterId!=Save.HousingOwnerId)message="Choose furniture you own.";
                else if(driving||!housing.IsInsideHome(home.HomeId,feet))message="Enter your own house to place furniture.";
                else if(DestroyedBuildings?.Invoke().Contains(home.BuildingId)==true)message="The house is storm damaged.";
                else if(StormChaseDestruction.SampleWindSpeedMph(feet,weather)>45)message="Wait for safe conditions before moving furniture.";
                else if(housing.TryPlacement(home.HomeId,furnishing.Sku,slot,yaw,physics.States,target,out var position,out var rotation,out message))
                {
                    newPose=furnishing with{Position=position,Rotation=rotation,LinearVelocity=default,AngularVelocity=default,Placed=true,Sleeping=true,Archived=true,CollisionEnabled=false};
                    Save.Furniture=Save.Furniture.Select(f=>f.FurnitureId==target?newPose:f).ToList();success=true;
                }
                break;
            case GameplayActionKind.VisitHome:
                if(housing.Home(target) is {} destination){housingVisitTarget=destination.HomeId;success=true;message="Home marker set. Walk or drive to the front door.";}break;
            case GameplayActionKind.SetHomeVisits:
                if(home is null)message="You do not own a home.";else{Save.HomeVisitsAllowed=target!=0;success=true;message=target!=0?"This solo home allows visits. Host a multiplayer career to invite other chasers.":"Home visits disabled.";}break;
        }
        if(success)
        {
            Save.HousingReceipts.Add(receipt);
            try{Flush();if(newPose is {} pose)physics.Put(pose);}
            catch(IOException){Save.Cash=cash;Save.OwnedHomeId=homeId;Save.HousingWorld=oldWorld;Save.HomeVisitsAllowed=visits;Save.Furniture=oldFurniture;Save.HousingReceipts.Remove(receipt);success=false;message="The housing purchase could not be saved. Your funds were retained.";}
        }
        Notify(message,seconds);return success;
    }
}
