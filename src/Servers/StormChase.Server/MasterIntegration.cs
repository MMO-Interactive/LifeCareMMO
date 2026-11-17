using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace StormChase.Server;

internal sealed class MasterAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder,CareerStore store)
    :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw=Request.Headers.Authorization.ToString();if(!AuthenticationHeaderValue.TryParse(raw,out var auth)||auth.Scheme!="Bearer"||auth.Parameter is null||auth.Parameter.Length>8192)return AuthenticateResult.NoResult();
        var remote=(RemoteCareerStore)store;using var request=new HttpRequestMessage(HttpMethod.Get,"/api/internal/introspect");request.Headers.Authorization=auth;
        using var result=await remote.Client.SendAsync(request,Context.RequestAborted);if(!result.IsSuccessStatusCode)return AuthenticateResult.Fail("Accounts service rejected bearer token.");
        var account=(await result.Content.ReadFromJsonAsync<AccountDto>(Context.RequestAborted))!;
        var identity=new ClaimsIdentity([new(ClaimTypes.NameIdentifier,account.AccountId),new(ClaimTypes.Name,account.Username)],Scheme.Name);
        return AuthenticateResult.Success(new(new(identity),Scheme.Name));
    }
}

internal sealed class MasterHeartbeat(ServerOptions options,CareerStore store,GameAuthority game,IServer httpServer,ILogger<MasterHeartbeat> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if(store is not RemoteCareerStore remote)return;
        while(!token.IsCancellationRequested)
        {
            try
            {
                var addresses=httpServer.Features.Get<IServerAddressesFeature>()?.Addresses;
                if(addresses?.Count>0)
                {
                    string url=options.ApiAddress??(options.HttpPort>0?(options.CertificatePath is null?"http":"https")+"://"+options.AdvertiseHost+":"+options.HttpPort:addresses.First());
                    using var response=await remote.Client.PostAsJsonAsync("/api/internal/servers/register",new ServerRegistration(url,game.ServerInfo,game.ActiveCharacterIds()),token);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning("Accounts directory heartbeat failed: {Reason}",error.Message);}
            await Task.Delay(TimeSpan.FromSeconds(5),token);
        }
    }
}

internal static class MasterApi
{
    public static void MapMaster(WebApplication app)
    {
        var routes=app.MapGroup("/api/internal");
        routes.MapPost("/servers/register",(ServerRegistration registration,HttpContext context,MasterDirectory directory)=>
        {
            if(context.Request.Headers["X-StormChase-Game"]!=registration.Server.ServerId)return Results.Unauthorized();directory.Register(registration);return Results.Ok(true);
        });
        routes.MapPost("/servers/stop",(HttpContext ctx,MasterDirectory directory)=>{directory.Stop(GameId(ctx));return Results.Ok(true);});
        routes.MapGet("/introspect",(ClaimsPrincipal user,CareerStore store)=>
        {
            var account=store.FindAccount(user.FindFirstValue(ClaimTypes.NameIdentifier)!);return account is null?Results.Unauthorized():Results.Ok(new AccountDto(account.Id,account.Username));
        }).RequireAuthorization();
        routes.MapGet("/accounts/{id}",(string id,CareerStore store)=>Results.Json(store.FindAccount(id) is{} account?account with{PasswordHash=""}:null));
        routes.MapGet("/characters",(string accountId,CareerStore store)=>Results.Ok(store.Characters(accountId)));
        routes.MapPost("/characters",(RemoteCharacterRequest request,CareerStore store)=>Results.Ok(store.CreateCharacter(request.AccountId,request.Name)));
        routes.MapGet("/ownership",(string accountId,string characterId,CareerStore store)=>Results.Ok(store.Owns(accountId,characterId)));
        routes.MapGet("/careers/{id}",(string id,CareerStore store)=>Results.Ok(store.GetCareer(id)));
        routes.MapPost("/admissions/redeem",(RedeemAdmissionRequest request,HttpContext context,MasterDirectory directory)=>
        {
            if(request.ServerId!=GameId(context))return Results.Unauthorized();var character=directory.Redeem(request);
            return character is null?Results.Text("null","application/json"):Results.Json(character);
        });
        routes.MapPost("/admissions/release",(CharacterIdRequest request,HttpContext context,MasterDirectory directory)=>{directory.Release(request.CharacterId,GameId(context));return Results.Ok(true);});
        routes.MapPost("/careers/purchase",(RemotePurchaseRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.Purchase(request.CharacterId,request.Purchase)):Results.Unauthorized());
        routes.MapPost("/careers/select",(RemoteSelectRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.SelectVehicle(request.CharacterId,request.VehicleId)):Results.Unauthorized());
        routes.MapPost("/careers/reward",(RemoteRewardRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.ApplyReward(request.CharacterId,request.EventId,request.Cash,request.Reputation,request.Photos,request.ResearchSamples,request.CompletedChases,request.Popularity,request.CompletedContracts)):Results.Unauthorized());
        routes.MapPost("/careers/condition",(RemoteConditionRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
        {if(!directory.HasLease(request.CharacterId,GameId(ctx)))return Results.Unauthorized();store.UpdateVehicle(request.CharacterId,request.FuelLiters,request.Health);return Results.Ok(store.GetCareer(request.CharacterId));});
        routes.MapPost("/careers/refuel",(RemoteRefuelRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.Refuel(request.CharacterId,request.RequestedLiters,request.CurrentFuel,request.CurrentHealth,request.Capacity)):Results.Unauthorized());
        routes.MapPost("/careers/recover",(CharacterIdRequest request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.Recover(request.CharacterId)):Results.Unauthorized());
        routes.MapPost("/careers/contracts-save",(RemoteContractSave request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.SaveContracts(request.CharacterId,request.Document)):Results.Unauthorized());
        routes.MapPost("/careers/contracts-commit",(RemoteContractCommit request,HttpContext ctx,CareerStore store,MasterDirectory directory)=>
            directory.HasLease(request.CharacterId,GameId(ctx))?Results.Ok(store.CommitContract(request.CharacterId,request.Receipt,request.ContractId,request.Cash,request.Reputation,request.Popularity,request.Document)):Results.Unauthorized());
    }
    public static void MapGame(WebApplication app)
    {
        app.MapPost("/api/internal/service-purchase",(RemotePurchaseRequest request,GameAuthority game)=>Results.Ok(game.Purchase(request.CharacterId,request.Purchase)));
        app.MapPost("/api/internal/service-select",(RemoteSelectRequest request,GameAuthority game)=>Results.Ok(game.SelectVehicle(request.CharacterId,request.VehicleId)));
        app.MapGet("/api/internal/lots",(GameAuthority game)=>Results.Ok(game.LotRevisions()));
        if(app.Services.GetRequiredService<ServerOptions>().StormTestRoutes)
        {
            app.MapPost("/api/internal/test/lot-tornado",(LotTornadoRequest request,GameAuthority game)=>game.StartLotTestStorm(request.LotId,request.Seconds,request.PeakMph,request.WidthMeters) is { } started?Results.Ok(started):Results.NotFound());
            app.MapGet("/api/internal/test/clear-site",(float? clearMeters,GameAuthority game)=>game.FindClearTestSite(clearMeters??1500) is { } site?Results.Ok(site):Results.NotFound());
        }
    }
    private static string GameId(HttpContext context)=>context.Request.Headers["X-StormChase-Game"].ToString();
    private sealed record CharacterIdRequest(string CharacterId);
    private sealed record LotTornadoRequest(string LotId,double Seconds=8,float PeakMph=220,float WidthMeters=160);
}
