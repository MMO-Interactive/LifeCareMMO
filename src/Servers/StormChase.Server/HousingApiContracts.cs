using StormChase.Protocol;

namespace StormChase.Server;

public sealed record HousingAccountDto(string World,HomeSiteState? Home,FurnitureState[] Furniture);
public sealed record HousingPurchaseDto(bool Purchased,bool Duplicate,string Message,CareerDto Career,HomeSiteState? Home=null,FurnitureState? Furniture=null);
public sealed record HousingSitesRequest(string World,HomeSiteState[] Sites);
public sealed record HousingBuyHomeRequest(string World,int HomeId,string OperationId);
public sealed record HousingBuyFurnitureRequest(string World,int OfferId,string OperationId);
public sealed record HousingVisitSettingsRequest(string World,bool Allowed,string OperationId);
public sealed record HousingPoseRequest(string World,FurnitureState State,long Revision);
public sealed record RemoteHousingHome(string CharacterId,HousingBuyHomeRequest Purchase);
public sealed record RemoteHousingFurniture(string CharacterId,HousingBuyFurnitureRequest Purchase);
public sealed record RemoteHousingVisits(string CharacterId,HousingVisitSettingsRequest Settings);
public sealed record RemoteHousingPose(string CharacterId,HousingPoseRequest Pose);
