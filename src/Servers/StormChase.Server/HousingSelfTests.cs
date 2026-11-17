using System.Text.Json;
using StormChase.Protocol;
namespace StormChase.Server;

internal static class HousingSelfTests
{
    public static int Run(string output)
    {
        string data=Path.Combine(output,"housing-isolated-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(data);var checks=new List<string>();
        void Check(bool pass,string message){if(!pass)throw new InvalidOperationException("Housing proof: "+message);checks.Add(message);}
        var store=new CareerStore(data);var account=store.Register("housing_chaser","Housing proof password!");var a=store.CreateCharacter(account.AccountId,"Home owner");var b=store.CreateCharacter(account.AccountId,"Visiting chaser");
        string world=new('a',64);HomeSiteState Site(int id)=>new(id,"building/rural/house/"+id,"Prairie home "+id,new(id*30,2,50),new(id*30,2,44),180000,"","",false,false);
        store.RegisterHousingSites(new(world,[Site(1),Site(2)]));Check(store.GetHousing(a.Id,world).Home is null,"New characters have no unearned free home.");
        Check(!store.BuyHome(a.Id,new(world,1,"poor-home")).Purchased&&store.GetCareer(a.Id).Cash==900,"Insufficient home purchase retains cash and deed availability.");
        store.ApplyReward(a.Id,"home-earnings",8000,0);store.ApplyReward(b.Id,"visit-earnings",8000,0);
        var home=store.BuyHome(a.Id,new(world,1,"buy-home"));Check(home.Purchased&&home.Career.Cash==7100&&home.Home!.Mine,"Home deed and exact currency debit commit together.");
        Check(store.BuyHome(a.Id,new(world,1,"buy-home")).Duplicate&&store.GetCareer(a.Id).Cash==7100,"Repeated home receipt does not charge twice.");
        bool conflict=false;try{store.BuyHome(a.Id,new(world,2,"buy-home"));}catch(CareerConflictException){conflict=true;}Check(conflict,"Reusing a purchase receipt for a different house is rejected.");
        Check(!store.BuyHome(b.Id,new(world,1,"claimed")).Purchased&&store.GetCareer(b.Id).Cash==8900,"A second character cannot claim the same actual house.");
        Check(!store.BuyHome(a.Id,new(world,2,"second-home")).Purchased,"A character cannot claim a second home in one world.");
        var lamp=store.BuyFurniture(a.Id,new(world,4,"buy-lamp"));Check(lamp.Purchased&&lamp.Furniture is {Placed:false}&&lamp.Career.Cash==7015,"A paid lamp starts in persistent inventory without a collider.");
        Check(store.BuyFurniture(a.Id,new(world,4,"buy-lamp")).Duplicate&&store.GetCareer(a.Id).Cash==7015,"Repeated furnishing receipt neither charges nor duplicates inventory.");
        Check(!store.BuyFurniture(b.Id,new(world,4,"homeless-lamp")).Purchased,"Furnishings require an owned house.");
        var pose=lamp.Furniture! with{Placed=true,Position=new(31,2,44),Rotation=new(0,.70710677f,0,.70710677f),Sleeping=true,Archived=true,CollisionEnabled=true};
        Check(store.SaveFurniturePose(a.Id,new(world,pose,20)),"Authoritative rotated placed pose persists.");
        Check(!store.SaveFurniturePose(a.Id,new(world,pose with{Position=new(999,0,999)},19)),"Older delayed pose updates cannot overwrite settled position.");
        bool theft=false;try{store.SaveFurniturePose(b.Id,new(world,pose,21));}catch(CareerConflictException){theft=true;}Check(theft,"Another character cannot move a furnishing it does not own.");
        Check(!store.SetHomeVisits(a.Id,new(world,false,"private-home")).Home!.VisitsAllowed,"Owner controls actual visit availability.");
        var reload=new CareerStore(data);var restored=reload.GetHousing(a.Id,world);Check(restored.Home is {HomeId:1,VisitsAllowed:false,Mine:true}&&restored.Furniture.Single().Position==pose.Position&&restored.Furniture.Single().Rotation==pose.Rotation&&!restored.Furniture.Single().CollisionEnabled,"Restart restores deed, privacy and exact rotated pose without stale native residency.");
        for(int i=0;i<23;i++)Check(store.BuyFurniture(a.Id,new(world,3,"chair-"+i)).Purchased,"Furnishing purchase "+(i+2)+" remains within per-home cap.");
        Check(!store.BuyFurniture(a.Id,new(world,3,"over-cap")).Purchased&&store.GetHousing(a.Id,world).Furniture.Length==24,"The 25th furnishing is rejected with a hard native actor/replication bound.");
        bool geometry=false;try{store.RegisterHousingSites(new(world,[Site(1) with{Entrance=new(2,3,4)}]));}catch(CareerConflictException){geometry=true;}Check(geometry,"A host cannot overwrite canonical home geometry under an existing world fingerprint.");
        Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"housing-persistence-proof.json"),JsonSerializer.Serialize(new{passed=true,checks=checks.Count,items=checks},new JsonSerializerOptions{WriteIndented=true}));return checks.Count;
    }
}
