using StormChase.Protocol;
namespace StormChase.Server;

public sealed partial class RemoteCareerStore
{
    public override bool RegisterHousingSites(HousingSitesRequest request)=>Post<bool>("/api/internal/housing/sites",request);
    public override HousingAccountDto GetHousing(string character,string world)=>Get<HousingAccountDto>("/api/internal/housing/account?character="+Uri.EscapeDataString(character)+"&world="+world);
    public override HomeSiteState[] HouseClaims(string world)=>Get<HomeSiteState[]>("/api/internal/housing/claims?world="+world);
    public override FurnitureState[] WorldFurniture(string world)=>Get<FurnitureState[]>("/api/internal/housing/furniture?world="+world);
    public override HousingPurchaseDto BuyHome(string character,HousingBuyHomeRequest request)
    {var result=Post<HousingPurchaseDto>("/api/internal/housing/buy-home",new RemoteHousingHome(character,request));Remember(result.Career);return result;}
    public override HousingPurchaseDto BuyFurniture(string character,HousingBuyFurnitureRequest request)
    {var result=Post<HousingPurchaseDto>("/api/internal/housing/buy-furniture",new RemoteHousingFurniture(character,request));Remember(result.Career);return result;}
    public override bool SaveFurniturePose(string character,HousingPoseRequest request)=>Post<bool>("/api/internal/housing/pose",new RemoteHousingPose(character,request));
    public override HousingPurchaseDto SetHomeVisits(string character,HousingVisitSettingsRequest request)
    {var result=Post<HousingPurchaseDto>("/api/internal/housing/visits",new RemoteHousingVisits(character,request));Remember(result.Career);return result;}
}
