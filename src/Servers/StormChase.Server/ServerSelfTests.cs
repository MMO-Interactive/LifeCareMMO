using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;

namespace StormChase.Server;

internal static class ServerSelfTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public static async Task<int> Run(ServerOptions input)
    {
        var checks=new List<object>();
        void Check(string name,bool passed){checks.Add(new{name,passed});if(!passed)throw new InvalidOperationException("Server self-test failed: "+name);}
        string output=input.TestOutput??Path.Combine(Environment.CurrentDirectory,"artifacts","server-http-proof");
        Directory.CreateDirectory(output);string data=Path.Combine(output,"isolated-"+Guid.NewGuid().ToString("N"));
        var options=input with{SelfTest=true,HttpPort=0,DataRoot=data,ControlKey=new string('a',64)};
        WebApplication? app=null;HttpClient? owner=null,other=null;
        try
        {
            app=ServerApi.Create(options);await app.StartAsync();
            var game=app.Services.GetRequiredService<GameAuthority>();
            for(int i=0;i<200&&!game.Ready;i++)await Task.Delay(25);
            Check("Headless weather service becomes ready",game.Ready);
            var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            owner=new(){BaseAddress=new(url)};other=new(){BaseAddress=new(url)};
            Check("Anonymous character access is rejected",(await owner.GetAsync("/api/characters")).StatusCode==HttpStatusCode.Unauthorized);
            var metadata=await owner.GetFromJsonAsync<ServerInfoDto>("/api/server");string serverId=metadata!.ServerId;
            Check("Server metadata and catalogue are public",metadata.Ready&&(await owner.GetFromJsonAsync<OfferDto[]>("/api/catalog"))!.Length==6);
            Check("Weak credentials are rejected",(await owner.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("owner","short"))).StatusCode==HttpStatusCode.BadRequest);
            Check("Account registration succeeds",(await owner.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("owner","Long password 19!"))).StatusCode==HttpStatusCode.Created);
            Check("Duplicate normalized account name conflicts",(await owner.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("OWNER","Long password 19!"))).StatusCode==HttpStatusCode.Conflict);
            Check("Incorrect password is rejected",(await owner.PostAsJsonAsync("/api/auth/login",new CredentialsRequest("owner","Wrong password 19!"))).StatusCode==HttpStatusCode.Unauthorized);
            var login=await owner.PostAsJsonAsync("/api/auth/login",new CredentialsRequest("owner","Long password 19!"));login.EnsureSuccessStatusCode();
            var tokens=await login.Content.ReadFromJsonAsync<TokenDto>();owner.DefaultRequestHeaders.Authorization=new("Bearer",tokens!.AccessToken);
            Check("Framework opaque access and refresh tokens are issued",tokens.TokenType=="Bearer"&&tokens.ExpiresIn==900&&tokens.AccessToken.Length>100&&tokens.RefreshToken.Length>100);
            var create=await owner.PostAsJsonAsync("/api/characters",new CharacterCreateRequest("Ari Chaser"));create.EnsureSuccessStatusCode();
            string character=(await create.Content.ReadFromJsonAsync<CharacterDto>())!.Id;
            var career=await owner.GetFromJsonAsync<CareerDto>("/api/characters/"+character+"/career");
            Check("Character starts with server-owned pickup and $900",career!.Cash==900&&career.OwnedVehicles.SequenceEqual(["pickup"]));
            Check("Character selection succeeds",(await owner.PostAsync("/api/characters/"+character+"/select",null)).IsSuccessStatusCode);
            (await other.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("other","Other password 20!"))).EnsureSuccessStatusCode();
            var foreignLogin=await other.PostAsJsonAsync("/api/auth/login",new CredentialsRequest("other","Other password 20!"));foreignLogin.EnsureSuccessStatusCode();
            other.DefaultRequestHeaders.Authorization=new("Bearer",(await foreignLogin.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
            Check("Foreign character read is rejected",(await other.GetAsync("/api/characters/"+character+"/career")).StatusCode==HttpStatusCode.NotFound);
            Check("Foreign purchase and admission are rejected",(await other.PostAsJsonAsync("/api/characters/"+character+"/purchases",new PurchaseRequest("repair","foreign"))).StatusCode==HttpStatusCode.NotFound&&
                (await other.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(character))).StatusCode==HttpStatusCode.NotFound);
            var purchase=await Purchase(owner,character,"repair","repair-one");Check("Purchase debits catalogue price",purchase.Purchased&&purchase.Career.Cash==650);
            var duplicate=await Purchase(owner,character,"repair","repair-one");Check("Retried purchase charges once",duplicate.Duplicate&&duplicate.Career.Cash==650);
            Check("Receipt cannot be reused for another SKU",(await owner.PostAsJsonAsync("/api/characters/"+character+"/purchases",new PurchaseRequest("suv","repair-one"))).StatusCode==HttpStatusCode.Conflict);
            var parallel=await Task.WhenAll(Purchase(owner,character,"repair","repair-two"),Purchase(owner,character,"repair","repair-three"));
            Check("Concurrent purchases serialize atomically",parallel.All(p=>p.Purchased)&&(await owner.GetFromJsonAsync<CareerDto>("/api/characters/"+character+"/career"))!.Cash==150);
            Check("Insufficient funds never produce negative cash",!(await Purchase(owner,character,"repair","repair-four")).Purchased);
            Check("Unknown SKU and malformed receipt fail validation",(await owner.PostAsJsonAsync("/api/characters/"+character+"/purchases",new PurchaseRequest("cash-grant","bad"))).StatusCode==HttpStatusCode.BadRequest&&
                (await owner.PostAsJsonAsync("/api/characters/"+character+"/purchases",new PurchaseRequest("repair","bad/receipt"))).StatusCode==HttpStatusCode.BadRequest);
            Check("Unowned vehicle selection is rejected",(await owner.PostAsJsonAsync("/api/characters/"+character+"/vehicle",new SelectVehicleRequest("interceptor"))).StatusCode==HttpStatusCode.BadRequest);
            Check("There is no client credit endpoint",(await owner.PostAsJsonAsync("/api/characters/"+character+"/rewards",new{cash=1000000})).StatusCode==HttpStatusCode.NotFound);
            var store=app.Services.GetRequiredService<CareerStore>();
            store.ApplyReward(character,"server-test-observation",1000,10,photos:1,researchSamples:1);
            var reward=store.ApplyReward(character,"server-test-observation",1000,10,photos:1,researchSamples:1);
            Check("Trusted observation reward is atomic and idempotent",reward.Cash==1150&&reward.Reputation==10&&reward.Photos==1&&reward.ResearchSamples==1);
            bool rejected=false;try{store.ApplyReward(character,"invalid-negative",-1,0);}catch(ArgumentException){rejected=true;}Check("Negative simulation reward is rejected",rejected);
            store.ApplyReward(character,"server-test-research",1000,15);
            var camera=await Purchase(owner,character,"telephoto-camera","camera-one");Check("Equipment ownership persists in server career",camera.Purchased&&camera.Career.CameraLevel==1&&camera.Career.Cash==750);
            store.UpdateVehicle(character,40,65);var pumped=store.Refuel(character,2,40,65,75);Check("Refuel debits actual pumped liters",pumped.Cash==747.30m&&pumped.VehicleConditions["pickup"].FuelLiters==42);
            rejected=false;try{store.Refuel(character,1,float.NaN,65,75);}catch(ArgumentException){rejected=true;}Check("Nonfinite authoritative fuel is rejected",rejected);
            var admissionResponse=await owner.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(character));admissionResponse.EnsureSuccessStatusCode();
            var admission=(await admissionResponse.Content.ReadFromJsonAsync<JoinTicketDto>())!;
            Check("Admission ticket binds account and selected character",game.TryConsumeAdmission(admission.Ticket,out var admitted)&&admitted!.CharacterId==character&&admission.ExpiresUtc>DateTimeOffset.UtcNow);
            Check("Admission ticket is consumed once",!game.TryConsumeAdmission(admission.Ticket,out _));
            var old=game.IssueAdmission(admitted!.AccountId,character)!;var newer=game.IssueAdmission(admitted.AccountId,character)!;
            Check("New admission supersedes old outstanding ticket",!game.TryConsumeAdmission(old.Ticket,out _)&&game.TryConsumeAdmission(newer.Ticket,out _));
            var expiring=game.IssueAdmission(admitted.AccountId,character)!;
            var refresh=await owner.PostAsJsonAsync("/api/auth/refresh",new{refreshToken=tokens.RefreshToken});Check("Refresh issues a new protected access token",refresh.IsSuccessStatusCode);
            Check("Tampered refresh token is rejected",(await owner.PostAsJsonAsync("/api/auth/refresh",new{refreshToken=tokens.RefreshToken+"tampered"})).StatusCode==HttpStatusCode.Unauthorized);
            Check("Wrong local control key cannot stop server",(await owner.PostAsync("/api/admin/shutdown",null)).StatusCode==HttpStatusCode.Unauthorized);
            Console.WriteLine("HTTP authority checks passed; waiting for the real 30-second admission expiry.");
            await Task.Delay(TimeSpan.FromSeconds(31));Check("Expired admission is rejected by server clock",!game.TryConsumeAdmission(expiring.Ticket,out _));
            var before=store.GetCareer(character);double elapsed=game.ServerInfo.CampaignSeconds;
            await app.StopAsync();await app.DisposeAsync();app=null;owner.Dispose();other.Dispose();
            app=ServerApi.Create(options);await app.StartAsync();game=app.Services.GetRequiredService<GameAuthority>();
            for(int i=0;i<200&&!game.Ready;i++)await Task.Delay(25);
            url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();owner=new(){BaseAddress=new(url)};
            owner.DefaultRequestHeaders.Authorization=new("Bearer",tokens.AccessToken);
            var after=await owner.GetFromJsonAsync<CareerDto>("/api/characters/"+character+"/career");
            Check("Restart retains protected token validity and complete career",after!.Cash==before.Cash&&after.CameraLevel==1&&after.Reputation==25&&after.Photos==1&&after.ResearchSamples==1&&after.VehicleConditions["pickup"].FuelLiters==42&&after.VehicleConditions["pickup"].Health==65);
            Check("Restart preserves server identity and weather clock",game.ServerId==serverId&&game.ServerInfo.CampaignSeconds>=elapsed);
            Check("Pending admission is invalid after server restart",!game.TryConsumeAdmission(expiring.Ticket,out _));
            var shutdown=new HttpRequestMessage(HttpMethod.Post,"/api/admin/shutdown");shutdown.Headers.Add("X-StormChase-Control",options.ControlKey);
            Check("Owned local host accepts graceful authenticated shutdown",(await owner.SendAsync(shutdown)).IsSuccessStatusCode);
            await app.StopAsync();await app.DisposeAsync();app=null;
            Check("External cleartext auth requires explicit trusted LAN override",Rejects(()=>ServerOptions.Parse(["--listen","0.0.0.0"])));
            using(var db=new SqliteConnection("Data Source="+Path.Combine(data,"career.sqlite")))
            {
                db.Open();using var query=db.CreateCommand();query.CommandText="SELECT password_hash FROM accounts WHERE username='owner'";
                string hash=(string)query.ExecuteScalar()!;Check("Database stores framework password hash only",hash!="Long password 19!"&&hash.Length>80);
            }
            int housingChecks=HousingSelfTests.Run(output);
            File.WriteAllText(Path.Combine(output,"server-http-proof.json"),JsonSerializer.Serialize(new{passed=true,checks,housingChecks,dataRoot=data},Json));
            Console.WriteLine($"Server HTTP proof passed: {checks.Count} HTTP and {housingChecks} housing persistence checks, "+Path.Combine(output,"server-http-proof.json"));return 0;
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"server-http-proof.json"),JsonSerializer.Serialize(new{passed=false,error=error.Message,checks,dataRoot=data},Json));throw;
        }
        finally {owner?.Dispose();other?.Dispose();if(app is not null){await app.StopAsync();await app.DisposeAsync();}}
    }
    private static bool Rejects(Action action){try{action();return false;}catch(ArgumentException){return true;}}
    private static async Task<PurchaseDto> Purchase(HttpClient http,string character,string sku,string operation)
    {var response=await http.PostAsJsonAsync("/api/characters/"+character+"/purchases",new PurchaseRequest(sku,operation));response.EnsureSuccessStatusCode();return(await response.Content.ReadFromJsonAsync<PurchaseDto>())!;}
    private sealed record TokenDto(string TokenType,string AccessToken,long ExpiresIn,string RefreshToken);
}
