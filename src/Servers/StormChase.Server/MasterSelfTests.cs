using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace StormChase.Server;

internal static class MasterSelfTests
{
    public static async Task<int> Run(ServerOptions input)
    {
        string output=input.TestOutput??Path.Combine(Environment.CurrentDirectory,"artifacts","accounts-master-proof");Directory.CreateDirectory(output);
        string data=Path.Combine(output,"isolated-"+Guid.NewGuid().ToString("N")),env="STORM_CHASE_MASTER_TEST_"+Guid.NewGuid().ToString("N");var checks=new List<object>();
        void Check(string name,bool pass){checks.Add(new{name,passed=pass});if(!pass)throw new InvalidOperationException("Accounts proof failed: "+name);}
        WebApplication? master=null;var games=new List<WebApplication>();var http=new List<HttpClient>();
        try
        {
            master=ServerApi.Create(input with{AccountsOnly=true,SelfTest=false,NativeSelfTest=false,MasterUrl=null,HttpPort=0,DataRoot=Path.Combine(data,"accounts")});await master.StartAsync();
            var directory=master.Services.GetRequiredService<MasterDirectory>();string url=Url(master);var account=new HttpClient{BaseAddress=new(url)};http.Add(account);
            Check("Accounts starts without a game world or transport identity",master.Services.GetRequiredService<GameAuthority>().ServerInfo.WorldContentFingerprint==""&&master.Services.GetRequiredService<GameAuthority>().ServerInfo.TransportIdentity=="");
            Check("Accounts directory starts empty",(await account.GetFromJsonAsync<ServerInfoDto[]>("/api/servers"))!.Length==0);
            Check("Private host routes reject public callers",(await account.GetAsync("/api/internal/careers/unknown")).StatusCode==HttpStatusCode.Unauthorized);
            (await account.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("global_chaser","Global account password!"))).EnsureSuccessStatusCode();
            var login=await account.PostAsJsonAsync("/api/auth/login",new CredentialsRequest("global_chaser","Global account password!"));login.EnsureSuccessStatusCode();
            string token=JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;account.DefaultRequestHeaders.Authorization=new("Bearer",token);
            var characterResponse=await account.PostAsJsonAsync("/api/characters",new CharacterCreateRequest("Global Chaser"));characterResponse.EnsureSuccessStatusCode();string character=(await characterResponse.Content.ReadFromJsonAsync<CharacterDto>())!.Id;
            Check("Global account and character exist before any game host",(await account.GetFromJsonAsync<CareerDto>("/api/characters/"+character+"/career"))!.Cash==900);
            Environment.SetEnvironmentVariable(env,File.ReadAllText(Path.Combine(data,"accounts","master-server.key")).Trim());
            for(int i=0;i<2;i++)
            {
                var game=ServerApi.Create(input with{AccountsOnly=false,SelfTest=true,HttpPort=0,GamePort=7777+i,DataRoot=Path.Combine(data,"game-"+i),MasterUrl=url,MasterKeyEnvironment=env});games.Add(game);await game.StartAsync();
                var client=new HttpClient{BaseAddress=new(Url(game))};client.DefaultRequestHeaders.Authorization=new("Bearer",token);http.Add(client);
            }
            for(int i=0;i<400&&directory.List().Length!=2;i++)await Task.Delay(25);
            var hosts=directory.List();Check("Two independent game API hosts register with global browser",hosts.Length==2&&hosts.All(h=>h.ApiAddress.StartsWith("http://127.0.0.1:")));
            Check("Same protected global token validates at both game APIs",(await http[1].GetAsync("/api/me")).IsSuccessStatusCode&&(await http[2].GetAsync("/api/me")).IsSuccessStatusCode);
            Check("Both hosts read the same global character without registration",(await http[1].GetFromJsonAsync<CharacterDto[]>("/api/characters"))!.Single().Id==character&&(await http[2].GetFromJsonAsync<CharacterDto[]>("/api/characters"))!.Single().Id==character);
            var a=games[0].Services.GetRequiredService<GameAuthority>();var b=games[1].Services.GetRequiredService<GameAuthority>();
            var admissionResponse=await account.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(character,a.ServerId));admissionResponse.EnsureSuccessStatusCode();var ticket=(await admissionResponse.Content.ReadFromJsonAsync<JoinTicketDto>())!;
            Check("Global admission is explicitly bound to selected server",ticket.ServerId==a.ServerId&&ticket.ApiAddress==Url(games[0]));
            Check("Another game host cannot redeem the ticket",!b.TryConsumeAdmission(ticket.Ticket,out _));
            Check("Intended host redeems once with global career",a.TryConsumeAdmission(ticket.Ticket,out var admitted)&&admitted!.Career.Cash==900&&!a.TryConsumeAdmission(ticket.Ticket,out _));
            var remote=(RemoteCareerStore)games[0].Services.GetRequiredService<CareerStore>();remote.ApplyReward(character,"global-sample",1000,10,photos:1,popularity:10,completedContracts:1);remote.ApplyReward(character,"global-sample",1000,10,photos:1,popularity:10,completedContracts:1);
            var progression=await account.GetFromJsonAsync<CareerDto>("/api/characters/"+character+"/career");Check("Game rewards commit once to central durable progression",progression!.Cash==1900&&progression.Reputation==10&&progression.Photos==1&&progression.Popularity==10&&progression.CompletedContracts==1);
            Check("Active character cannot join another game simultaneously",(await account.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(character,b.ServerId))).StatusCode==HttpStatusCode.Conflict);
            remote.Release(character);
            var secondResponse=await account.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(character,b.ServerId));secondResponse.EnsureSuccessStatusCode();var secondTicket=(await secondResponse.Content.ReadFromJsonAsync<JoinTicketDto>())!;
            Check("Character moves between hosts retaining global progression",b.TryConsumeAdmission(secondTicket.Ticket,out var moved)&&moved!.Career.Cash==1900&&moved.Career.Popularity==10);
            bool denied=false;try{remote.ApplyReward(character,"wrong-game-credit",1000,0);}catch(HttpRequestException){denied=true;}Check("Previous host cannot award after character moved",denied);
            ((RemoteCareerStore)games[1].Services.GetRequiredService<CareerStore>()).Release(character);
            foreach(var game in games){await game.StopAsync();await game.DisposeAsync();}games.Clear();
            Check("Graceful game shutdown removes directory entries",directory.List().Length==0);
            await master.StopAsync();await master.DisposeAsync();master=null;
            var restored=new CareerStore(Path.Combine(data,"accounts")).GetCareer(character);Check("Central character persists independently of all game worlds",restored.Cash==1900&&restored.Popularity==10&&restored.CompletedContracts==1);
            File.WriteAllText(Path.Combine(output,"accounts-master-proof.json"),JsonSerializer.Serialize(new{passed=true,checks,dataRoot=data},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Independent accounts proof passed: "+checks.Count+" checks.");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"accounts-master-proof.json"),JsonSerializer.Serialize(new{passed=false,error=error.Message,checks,dataRoot=data},new JsonSerializerOptions{WriteIndented=true}));throw;}
        finally{foreach(var client in http)client.Dispose();foreach(var game in games){await game.StopAsync();await game.DisposeAsync();}if(master is not null){await master.StopAsync();await master.DisposeAsync();}Environment.SetEnvironmentVariable(env,null);}
    }
    private static string Url(WebApplication app)=>app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
}
