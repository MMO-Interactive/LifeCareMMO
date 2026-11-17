using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace StormChase.Server;

public static class ServerApi
{
    public static WebApplication Create(ServerOptions options)
    {
        ServerDataAccess.SecureDirectory(options.DataRoot);
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=[],ContentRootPath=AppContext.BaseDirectory });
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server=>
        {
            server.Limits.MaxRequestBodySize=8192;
            server.Listen(options.ListenAddress=="localhost"?IPAddress.Loopback:IPAddress.Parse(options.ListenAddress),options.HttpPort,listen=>
            {
                if(options.CertificatePath is not null)
                {
                    var password=options.CertificatePasswordEnvironment is null ? null : Environment.GetEnvironmentVariable(options.CertificatePasswordEnvironment);
                    listen.UseHttps(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath,password));
                }
            });
        });
        var protection=builder.Services.AddDataProtection().SetApplicationName("MMOI.StormChase.Server.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataRoot,"keys")));
        if(OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        if(options.MasterUrl is null)builder.Services.AddAuthentication(IdentityConstants.BearerScheme).AddBearerToken(IdentityConstants.BearerScheme,bearer=>
        {
            bearer.BearerTokenExpiration=TimeSpan.FromMinutes(15);
            bearer.RefreshTokenExpiration=TimeSpan.FromDays(7);
        });
        else builder.Services.AddAuthentication("Master").AddScheme<AuthenticationSchemeOptions,MasterAuthenticationHandler>("Master",_=>{});
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CareerStore>(options.MasterUrl is null?new CareerStore(options.DataRoot):new RemoteCareerStore(options));
        if(options.AccountsOnly)builder.Services.AddSingleton<MasterDirectory>();
        builder.Services.AddSingleton<GameAuthority>();
        builder.Services.AddHostedService(sp=>sp.GetRequiredService<GameAuthority>());
        if(options.MasterUrl is not null)builder.Services.AddHostedService<MasterHeartbeat>();
        builder.Services.AddRateLimiter(rate=>
        {
            rate.RejectionStatusCode=429;
            rate.AddPolicy("auth",context=>RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions { PermitLimit=30,Window=TimeSpan.FromMinutes(1),QueueLimit=0 }));
            rate.AddPolicy("account",context=>RateLimitPartition.GetTokenBucketLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier)??context.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new TokenBucketRateLimiterOptions { TokenLimit=40,TokensPerPeriod=20,ReplenishmentPeriod=TimeSpan.FromSeconds(10),AutoReplenishment=true,QueueLimit=0 }));
        });
        var app=builder.Build();
        if(options.AccountsOnly)app.Services.GetRequiredService<MasterDirectory>();
        app.Use(async(context,next)=>
        {
            try { await next(context); }
            catch(CareerConflictException e) { context.Response.StatusCode=409; await context.Response.WriteAsJsonAsync(new ApiError(e.Message)); }
            catch(ArgumentException e) { context.Response.StatusCode=400; await context.Response.WriteAsJsonAsync(new ApiError(e.Message)); }
            catch(KeyNotFoundException) { context.Response.StatusCode=404; await context.Response.WriteAsJsonAsync(new ApiError("Character was not found.")); }
        });
        if(options.AccountsOnly||options.MasterUrl is not null)app.Use(async(context,next)=>
        {
            if(context.Request.Path.StartsWithSegments("/api/internal"))
            {
                bool trusted=options.AccountsOnly?context.RequestServices.GetRequiredService<MasterDirectory>().IsTrusted(context):((RemoteCareerStore)context.RequestServices.GetRequiredService<CareerStore>()).IsTrusted(context);
                if(!trusted){context.Response.StatusCode=401;return;}
            }
            await next(context);
        });
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapGet("/health",(GameAuthority game)=>Results.Ok(new { status=game.Ready?"ready":"starting",serverId=game.ServerId }));
        app.MapGet("/api/server",(GameAuthority game)=>Results.Ok(game.ServerInfo));
        if(options.AccountsOnly)app.MapGet("/api/servers",(MasterDirectory directory)=>Results.Ok(directory.List()));
        else app.MapGet("/api/servers",(GameAuthority game)=>Results.Ok(new[] { game.ServerInfo }));
        app.MapGet("/api/catalog",()=>Results.Ok(CareerStore.Offers));
        if(options.MasterUrl is null)
        {
        app.MapPost("/api/auth/register",(CredentialsRequest request,CareerStore store)=>Results.Json(store.Register(request.Username,request.Password),statusCode:201)).RequireRateLimiting("auth");
        app.MapPost("/api/auth/login",(CredentialsRequest request,CareerStore store)=>
        {
            var account=store.Login(request.Username,request.Password);
            if(account is null) return Results.Unauthorized();
            return Results.SignIn(Principal(account),authenticationScheme:IdentityConstants.BearerScheme);
        }).RequireRateLimiting("auth");
        app.MapPost("/api/auth/refresh",(RefreshRequest request,IOptionsMonitor<BearerTokenOptions> bearer,CareerStore store)=>
        {
            if(string.IsNullOrWhiteSpace(request.RefreshToken)||request.RefreshToken.Length>8192) return Results.Unauthorized();
            var ticket=bearer.Get(IdentityConstants.BearerScheme).RefreshTokenProtector.Unprotect(request.RefreshToken);
            var accountId=ticket?.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if(ticket?.Properties.ExpiresUtc is not { } expires||expires<=DateTimeOffset.UtcNow||accountId is null) return Results.Unauthorized();
            var account=store.FindAccount(accountId);
            return account is null ? Results.Unauthorized() : Results.SignIn(Principal(account),authenticationScheme:IdentityConstants.BearerScheme);
        }).RequireRateLimiting("auth");
        }
        var accountApi=app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("account");
        accountApi.MapGet("/me",(ClaimsPrincipal user,CareerStore store)=>
        {
            var account=store.FindAccount(AccountId(user)); return account is null ? Results.Unauthorized() : Results.Ok(new AccountDto(account.Id,account.Username));
        });
        accountApi.MapGet("/characters",(ClaimsPrincipal user,CareerStore store)=>Results.Ok(store.Characters(AccountId(user))));
        accountApi.MapPost("/characters",(CharacterCreateRequest request,ClaimsPrincipal user,CareerStore store)=>Results.Json(store.CreateCharacter(AccountId(user),request.Name),statusCode:201));
        accountApi.MapGet("/characters/{id}/career",(string id,ClaimsPrincipal user,CareerStore store)=>
            store.Owns(AccountId(user),id)?Results.Ok(store.GetCareer(id)):Results.NotFound());
        accountApi.MapPost("/characters/{id}/select",(string id,ClaimsPrincipal user,CareerStore store)=>
            store.Owns(AccountId(user),id)?Results.Ok(store.GetCareer(id)):Results.NotFound());
        accountApi.MapPost("/characters/{id}/purchases",async(string id,PurchaseRequest request,ClaimsPrincipal user,CareerStore store,GameAuthority game,HttpContext context)=>
        {
            if(!store.Owns(AccountId(user),id)) return Results.NotFound();
            if(options.AccountsOnly&&context.RequestServices.GetRequiredService<MasterDirectory>().ActiveServer(id) is{} server)
                return Results.Ok(await context.RequestServices.GetRequiredService<MasterDirectory>().Forward<PurchaseDto>(server,"/api/internal/service-purchase",new RemotePurchaseRequest(id,request)));
            return Results.Ok(game.Purchase(id,request));
        });
        accountApi.MapPost("/characters/{id}/vehicle",async(string id,SelectVehicleRequest request,ClaimsPrincipal user,CareerStore store,GameAuthority game,HttpContext context)=>
        {
            if(!store.Owns(AccountId(user),id)) return Results.NotFound();
            if(options.AccountsOnly&&context.RequestServices.GetRequiredService<MasterDirectory>().ActiveServer(id) is{} server)
                return Results.Ok(await context.RequestServices.GetRequiredService<MasterDirectory>().Forward<CareerDto>(server,"/api/internal/service-select",new RemoteSelectRequest(id,request.VehicleId)));
            return Results.Ok(game.SelectVehicle(id,request.VehicleId));
        });
        accountApi.MapPost("/matches/join",(JoinTicketRequest request,ClaimsPrincipal user,GameAuthority game,HttpContext context)=>
        {
            var ticket=options.AccountsOnly?context.RequestServices.GetRequiredService<MasterDirectory>().Issue(AccountId(user),request.CharacterId,request.ServerId):game.IssueAdmission(AccountId(user),request.CharacterId);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        });
        if(options.AccountsOnly)MasterApi.MapMaster(app);
        else if(options.MasterUrl is not null)MasterApi.MapGame(app);
        ServerHousingApi.MapHousing(app);
        if(options.ControlKey is not null)
            app.MapPost("/api/admin/shutdown",(HttpContext context,IHostApplicationLifetime lifetime)=>
            {
                var address=context.Connection.RemoteIpAddress;
                var supplied=context.Request.Headers["X-StormChase-Control"].ToString();
                if(address is null||!IPAddress.IsLoopback(address)||!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(options.ControlKey))) return Results.Unauthorized();
                lifetime.StopApplication(); return Results.Ok(new { stopping=true });
            }).RequireRateLimiting("auth");
        app.Lifetime.ApplicationStarted.Register(()=>Console.WriteLine("Storm Chase server ready: "+string.Join(", ",app.Urls)+"; game port "+options.GamePort));
        return app;
    }
    private static string AccountId(ClaimsPrincipal principal)=>principal.FindFirstValue(ClaimTypes.NameIdentifier)??throw new InvalidOperationException("Authenticated identity has no account.");
    private static ClaimsPrincipal Principal(AccountRecord account)=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,account.Id),new(ClaimTypes.Name,account.Username)],IdentityConstants.BearerScheme));
    private sealed record RefreshRequest(string RefreshToken);
}
