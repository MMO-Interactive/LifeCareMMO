using System.Security.Claims;
namespace StormChase.Server;

internal static class ServerHousingApi
{
    public static void MapHousing(WebApplication app)
    {
        var options=app.Services.GetRequiredService<ServerOptions>();
        if(options.AccountsOnly)
        {
            var routes=app.MapGroup("/api/internal/housing");
            bool Leased(string id,HttpContext c,MasterDirectory d)=>d.HasLease(id,c.Request.Headers["X-StormChase-Game"].ToString());
            routes.MapPost("/sites",(HousingSitesRequest r,CareerStore s)=>Results.Ok(s.RegisterHousingSites(r)));
            routes.MapGet("/account",(string character,string world,CareerStore s)=>Results.Ok(s.GetHousing(character,world)));
            routes.MapGet("/claims",(string world,CareerStore s)=>Results.Ok(s.HouseClaims(world)));
            routes.MapGet("/furniture",(string world,CareerStore s)=>Results.Ok(s.WorldFurniture(world)));
            routes.MapPost("/buy-home",(RemoteHousingHome r,HttpContext c,MasterDirectory d,CareerStore s)=>Leased(r.CharacterId,c,d)?Results.Ok(s.BuyHome(r.CharacterId,r.Purchase)):Results.Unauthorized());
            routes.MapPost("/buy-furniture",(RemoteHousingFurniture r,HttpContext c,MasterDirectory d,CareerStore s)=>Leased(r.CharacterId,c,d)?Results.Ok(s.BuyFurniture(r.CharacterId,r.Purchase)):Results.Unauthorized());
            routes.MapPost("/pose",(RemoteHousingPose r,HttpContext c,MasterDirectory d,CareerStore s)=>Leased(r.CharacterId,c,d)?Results.Ok(s.SaveFurniturePose(r.CharacterId,r.Pose)):Results.Unauthorized());
            routes.MapPost("/visits",(RemoteHousingVisits r,HttpContext c,MasterDirectory d,CareerStore s)=>Leased(r.CharacterId,c,d)?Results.Ok(s.SetHomeVisits(r.CharacterId,r.Settings)):Results.Unauthorized());
        }
        var accounts=app.MapGroup("/api/characters/{character}/housing").RequireAuthorization().RequireRateLimiting("account");
        accounts.MapGet("",(string character,string world,ClaimsPrincipal user,CareerStore store)=>store.Owns(user.FindFirstValue(ClaimTypes.NameIdentifier)!,character)?Results.Ok(store.GetHousing(character,world)):Results.NotFound());
        // World changes are performed by the signed-in game action channel. The
        // account endpoint reads persistent deeds without accepting client poses.
    }
}
