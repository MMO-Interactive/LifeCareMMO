namespace StormChase.Protocol;

public sealed record HomeSiteState(int HomeId,string BuildingId,string Name,NetVector3 Entrance,NetVector3 Interior,
    long PriceCents,string OwnerCharacterId,string OwnerName,bool Mine,bool Destroyed,bool VisitsAllowed=true);
public sealed record FurnitureOfferState(int Id,string Sku,string Name,long PriceCents,string Description,bool Lamp);
public sealed record FurnitureState(int FurnitureId,int HomeId,string OwnerCharacterId,string Sku,NetVector3 Position,
    NetQuaternion Rotation,NetVector3 LinearVelocity,NetVector3 AngularVelocity,bool Placed,bool Sleeping,bool CollisionEnabled,bool Archived);
public sealed record HomeVisitorState(uint PlayerId,string Name,int HomeId,bool AtHome,bool VisitsAllowed);
public sealed record HousingState(HomeSiteState? Home,HomeSiteState[] Homes,FurnitureOfferState[] Offers,FurnitureState[] Furniture,
    HomeVisitorState[] Visitors,int VisitTargetHomeId=0,NetVector3 VisitTarget=default,string Message="",int MaxFurniture=24);
