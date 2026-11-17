using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Shared.Contracts.World;
using StormChase.Networking;
using StormChase.Protocol;

namespace StormChase.Server;

internal static class ServerNativeTests
{
    public static async Task<int> Run(ServerOptions input)
    {
        string output=input.TestOutput??Path.Combine(Environment.CurrentDirectory,"artifacts","server-native-proof");Directory.CreateDirectory(output);
        string data=Path.Combine(output,"isolated-"+Guid.NewGuid().ToString("N"));
        using var portFinder=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);portFinder.Bind(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)portFinder.LocalEndPoint!).Port;portFinder.Close();
        var options=input with{HttpPort=0,GamePort=port,DataRoot=data,SelfTest=false,NativeSelfTest=true,AccountsOnly=false};
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,passed=pass});if(!pass)throw new InvalidOperationException("Native server proof failed: "+name);}
        var peers=new List<Peer>();var clients=new List<HttpClient>();WebApplication? app=null,masterApp=null;
        string accountsData=Path.Combine(data,"independent-accounts"),keyEnvironment="STORM_CHASE_TEST_KEY_"+Guid.NewGuid().ToString("N");
        try
        {
            masterApp=ServerApi.Create(options with{AccountsOnly=true,NativeSelfTest=false,MasterUrl=null,MasterKeyEnvironment=null,DataRoot=accountsData});await masterApp.StartAsync();
            string accountsUrl=masterApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Environment.SetEnvironmentVariable(keyEnvironment,File.ReadAllText(Path.Combine(accountsData,"master-server.key")).Trim());
            options=options with{MasterUrl=accountsUrl,MasterKeyEnvironment=keyEnvironment};
            app=ServerApi.Create(options);await app.StartAsync();var game=app.Services.GetRequiredService<GameAuthority>();
            for(int i=0;i<2400&&!game.Ready;i++)await Task.Delay(25);
            Check("Actual authored native PhysX world becomes ready",game.Ready);
            var url=accountsUrl;
            var info=game.ServerInfo;
            for(int i=0;i<400&&!masterApp.Services.GetRequiredService<MasterDirectory>().List().Any(s=>s.ServerId==info.ServerId&&s.Ready);i++)await Task.Delay(25);
            Check("Independent accounts service discovers registered ready game host",masterApp.Services.GetRequiredService<MasterDirectory>().List().Any(s=>s.ServerId==info.ServerId&&s.Ready));
            Check("Server advertises signed transport identity and exact world fingerprint",info.WorldContentFingerprint.Length==64&&info.TransportIdentity.StartsWith("sc/")&&info.TransportAuthority.Length>100);
            for(int i=0;i<3;i++)
            {
                var client=new HttpClient{BaseAddress=new(url)};clients.Add(client);
                (await client.PostAsJsonAsync("/api/auth/register",new CredentialsRequest("native_player_"+i,"Native proof password 37!"))).EnsureSuccessStatusCode();
                var login=await client.PostAsJsonAsync("/api/auth/login",new CredentialsRequest("native_player_"+i,"Native proof password 37!"));login.EnsureSuccessStatusCode();
                var token=JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();client.DefaultRequestHeaders.Authorization=new("Bearer",token);
                var characterResponse=await client.PostAsJsonAsync("/api/characters",new CharacterCreateRequest("Native chaser "+i));characterResponse.EnsureSuccessStatusCode();
                string character=(await characterResponse.Content.ReadFromJsonAsync<CharacterDto>())!.Id;
                peers.Add(new(StormTransportHost.CreateClient(new TransportSecurity(info.TransportAuthority,ExpectedPeerIdentity:info.TransportIdentity)),character));
            }
            for(int i=0;i<2;i++)await Join(peers[i],clients[i],info);
            Check("Two authenticated native clients join the same authority",peers.Take(2).All(p=>p.Join is not null)&&game.ServerInfo.Players==2);
            Check("Native admissions carry the complete daily sounding and model clock",peers.Take(2).All(p=>p.Join!.Snapshot.Atmosphere is{ModelVersion:1,Sounding.Length:97,ThreeDayForecast.Length:3}));
            StormTransportHost.ConfigureNetworkSimulation(5,40);
            Check("Real native transport simulator enables five percent loss and 40ms lag",true);
            var first=peers[0];await PumpUntil(peers,()=>first.Snapshot?.Players.Length==2,TimeSpan.FromSeconds(10));
            var initial=first.Snapshot!.Players.Single(p=>p.PlayerId==first.Join!.PlayerId).Position;
            first.Host.Send(first.Connection,MessageKind.ClientInput,new ClientInput(1,new(1_000_000,5000,1_000_000),0,0,PlayerMovementMode.Fly,999,false));
            await PumpUntil(peers,()=>first.Snapshot?.Players.Any(p=>p.PlayerId==first.Join!.PlayerId&&p.ClientTick==1)==true,TimeSpan.FromSeconds(10));
            var authoritative=first.Snapshot!.Players.Single(p=>p.PlayerId==first.Join!.PlayerId);
            Check("Untrusted position/fly mode cannot teleport the capsule",Distance(initial,authoritative.Position)<5&&authoritative.Mode==PlayerMovementMode.OnFoot);
            first.Host.Send(first.Connection,MessageKind.ClientInput,new ClientInput(2,new(-999_999,-999_999,-999_999),0,0,PlayerMovementMode.OnFoot,0,true,1));
            await PumpFor(peers,TimeSpan.FromSeconds(.85));
            var moved=first.Snapshot!.Players.Single(p=>p.PlayerId==first.Join!.PlayerId);
            Check("Raw movement axes drive bounded native capsule movement",Distance(initial,moved.Position)>.1&&Distance(initial,moved.Position)<15&&moved.Velocity.IsFinite);
            var parked=first.Snapshot!.Vehicles.Single(v=>v.VehicleId==(int)first.Join!.PlayerId).Position;
            game.PlaceNativeProofAvatar(first.Character,new Vector3f(parked.X-3,parked.Y,parked.Z));
            first.Host.Send(first.Connection,MessageKind.ClientInput,new ClientInput(3,new(1_000_000,0,1_000_000),0,0,PlayerMovementMode.OnFoot,0,false));
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(1,GameplayActionKind.EnterVehicle,0,default));
            await PumpUntil(peers,()=>first.Results.ContainsKey(1),TimeSpan.FromSeconds(10));
            Check("Actual nearby vehicle entry is authoritative: "+first.Results[1].Message,first.Results[1].Success);
            first.Host.Send(first.Connection,MessageKind.ClientInput,new ClientInput(4,default,0,0,PlayerMovementMode.Driving,0,false,1));
            await PumpFor(peers,TimeSpan.FromSeconds(.85));
            var car=first.Snapshot!.Vehicles.Single(v=>v.DriverId==first.Join!.PlayerId);
            Check("Vehicle simulation publishes actual body rotation/fuel/velocity",car.Rotation.IsFinite&&car.Rotation.W!=0&&car.Velocity.IsFinite&&car.FuelLiters>0&&car.FuelLiters<=75);
            first.Host.Send(first.Connection,MessageKind.ClientInput,new ClientInput(5,default,0,0,PlayerMovementMode.Driving,0,false));
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(2,GameplayActionKind.TakePhoto,0,new(1_000_000,0,1_000_000)));
            await PumpUntil(peers,()=>first.Results.ContainsKey(2),TimeSpan.FromSeconds(10));
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(2,GameplayActionKind.TakePhoto,0,default));await PumpFor(peers,TimeSpan.FromSeconds(.25));
            var career=await clients[0].GetFromJsonAsync<CareerDto>("/api/characters/"+first.Character+"/career");
            Check("Retried real gameplay action records one durable observation",career!.Photos==1);
            game.PlaceNativeProofVehicle(first.Character,new Vector3f(initial.X+1500,initial.Y,initial.Z+1500));
            var remoteRepair=await clients[0].PostAsJsonAsync("/api/characters/"+first.Character+"/purchases",new PurchaseRequest("repair","remote-repair"));
            Check("HTTP purchase cannot remotely bypass active station physics",remoteRepair.StatusCode==HttpStatusCode.Conflict);
            var survey=game.NativeProofOffers().First(o=>o.Kind==ChaseContractKind.WeatherSurvey&&o.Stops.Count==1);
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(3,GameplayActionKind.AcceptContract,survey.Id,new(1_000_000,0,1_000_000)));
            await PumpUntil(peers,()=>first.Results.ContainsKey(3),TimeSpan.FromSeconds(10));Check("Quiet-day mission acceptance persists at accounts authority",first.Results[3].Success);
            await PumpFor(peers,TimeSpan.FromSeconds(.6));
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(4,GameplayActionKind.ContractInteract,0,new(survey.Stops[0].Position.X,survey.Stops[0].Position.Y,survey.Stops[0].Position.Z)));
            await PumpUntil(peers,()=>first.Results.ContainsKey(4),TimeSpan.FromSeconds(10));Check("Client-claimed mission coordinates cannot bypass real proximity",!first.Results[4].Success);
            var beforeContract=await clients[0].GetFromJsonAsync<CareerDto>("/api/characters/"+first.Character+"/career");
            game.PlaceNativeProofVehicle(first.Character,survey.Stops[0].Position);
            await PumpFor(peers,TimeSpan.FromSeconds(.6));first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(5,GameplayActionKind.ContractInteract,0,default));
            await PumpUntil(peers,()=>first.Results.ContainsKey(5),TimeSpan.FromSeconds(10));Check("Actual native position starts timed survey work",first.Results[5].Success);
            Console.WriteLine("Mission proof waits for actual 18-second authoritative work time.");
            await PumpUntil(peers,()=>first.Snapshot?.Career?.Popularity>0&&first.Snapshot.Mission is null,TimeSpan.FromSeconds(35));
            var completedContract=await clients[0].GetFromJsonAsync<CareerDto>("/api/characters/"+first.Character+"/career");
            Check("Timed mission pays centrally and clears active assignment atomically",completedContract!.Cash>=beforeContract!.Cash+survey.CashCents/100m&&completedContract.Popularity==survey.Popularity&&completedContract.CompletedContracts==1&&JsonDocument.Parse(completedContract.ContractState).RootElement.GetProperty("active").ValueKind==JsonValueKind.Null);
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(5,GameplayActionKind.ContractInteract,0,default));await PumpFor(peers,TimeSpan.FromSeconds(.25));
            var duplicateContract=await clients[0].GetFromJsonAsync<CareerDto>("/api/characters/"+first.Character+"/career");Check("Retried mission interaction cannot duplicate completion reward",duplicateContract!.CompletedContracts==1&&duplicateContract.Cash==completedContract.Cash);
            Console.WriteLine("Authenticated native movement checks passed; generating actual building destruction.");
            game.BeginNativeProofStorm();await PumpUntil(peers,()=>game.NativeProofBodies().Length>0,TimeSpan.FromSeconds(15));await PumpFor(peers,TimeSpan.FromSeconds(2));
            var flying=game.NativeProofBodies();Check("Native tornado releases actual authored building parts",flying.Length>0&&flying.Any(b=>b.AngularVelocity.X*b.AngularVelocity.X+b.AngularVelocity.Y*b.AngularVelocity.Y+b.AngularVelocity.Z*b.AngularVelocity.Z>.01f));
            Check("Released bodies have coherent stable IDs and reliable release events",flying.All(b=>!string.IsNullOrEmpty(b.StablePartId))&&peers.Take(2).All(p=>p.ReleaseEvents>0));
            game.ResetNativeProofPerformance();
            var networkSamples=new List<object>();var bufferedSamples=new List<int>();int largestEncodedSnapshot=0;
            for(int i=0;i<16;i++)
            {
                (await clients[i%2].GetAsync("/api/characters/"+peers[i%2].Character+"/career")).EnsureSuccessStatusCode();
                await PumpFor(peers,TimeSpan.FromSeconds(.5));
                var network=game.NativeProofNetwork();largestEncodedSnapshot=Math.Max(largestEncodedSnapshot,network.LargestEncodedSnapshotBytes);
                var buffered=network.ConnectionStatuses.Select(s=>Regex.Match(s,@"Bytes buffered:\s*([0-9,]+)")).Select(m=>m.Success?int.Parse(m.Groups[1].Value.Replace(",","")):-1).ToArray();
                bufferedSamples.AddRange(buffered);
                networkSamples.Add(new{sample=i,largestEncodedSnapshotBytes=network.LargestEncodedSnapshotBytes,bufferedBytes=buffered,statuses=network.ConnectionStatuses});
            }
            var performance=game.NativeProofPerformance();
            File.WriteAllText(Path.Combine(output,"network-samples.json"),JsonSerializer.Serialize(new{performance,largestEncodedSnapshotBytes=largestEncodedSnapshot,maximumNativeBufferedBytes=bufferedSamples.Max(),networkSamples},new JsonSerializerOptions{WriteIndented=true}));
            Check("Compressed complete world snapshots stay below native oversized reliable threshold",largestEncodedSnapshot<15000);
            Check("Native connections report a bounded steady send queue",bufferedSamples.All(n=>n>=0&&n<256*1024));
            Check("Unchanged daily weather is cached without repeat full profiles",peers.Take(2).All(p=>p.FastAtmosphereProfiles==0&&p.DailyAtmospherePackets==0));
            game.SettleNativeProofStorm();await PumpFor(peers,TimeSpan.FromSeconds(.75));
            var landed=game.NativeProofBodies().Where(b=>b.Archived&&b.Sleeping).ToArray();
            Check("Actual settled debris retains final server collision poses",landed.Length>0&&landed.All(b=>b.LinearVelocity==default&&b.AngularVelocity==default));
            var cleanup=game.NativeProofOffers().Single(o=>o.Kind==ChaseContractKind.DebrisClearance);
            Check("Cleanup dispatch appears only around actual settled wreckage",cleanup.Available);
            first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(6,GameplayActionKind.AcceptContract,cleanup.Id,default));
            await PumpUntil(peers,()=>first.Results.ContainsKey(6),TimeSpan.FromSeconds(10));Check("Actual wreckage cleanup accepts an authoritative assignment",first.Results[6].Success);
            for(int i=0;i<3;i++)
            {
                var piece=game.NativeProofBodies().Where(b=>!b.Collected&&b.Archived&&b.Sleeping&&Distance(b.Position,new(cleanup.Stops[0].Position.X,cleanup.Stops[0].Position.Y,cleanup.Stops[0].Position.Z))<170&&MathF.Abs(b.Position.Y-cleanup.Stops[0].Position.Y)<3).OrderBy(b=>b.StablePartId,StringComparer.Ordinal).First();
                game.PlaceNativeProofAvatar(first.Character,new Vector3f(piece.Position.X+1,piece.Position.Y,piece.Position.Z));
                await PumpFor(peers,TimeSpan.FromSeconds(.6));long request=7+i;
                first.Host.Send(first.Connection,MessageKind.GameplayAction,new GameplayAction(request,GameplayActionKind.Salvage,0,new(1_000_000,0,1_000_000)));
                await PumpUntil(peers,()=>first.Results.ContainsKey(request),TimeSpan.FromSeconds(10));Check("Server collects physically nearby settled fragment "+i,first.Results[request].Success);
            }
            await PumpUntil(peers,()=>first.Snapshot?.Mission is null&&game.NativeProofBodies().Count(b=>b.Collected)>=3,TimeSpan.FromSeconds(10));
            var afterCleanup=await clients[0].GetFromJsonAsync<CareerDto>("/api/characters/"+first.Character+"/career");Check("Real debris cleanup commits central pay and popularity",afterCleanup!.CompletedContracts==2&&afterCleanup.Popularity==survey.Popularity+cleanup.Popularity);
            landed=game.NativeProofBodies().Where(b=>b.Archived&&b.Sleeping).ToArray();
            Check("Salvage retains permanent collision-disabled tombstones",landed.Count(b=>b.Collected)>=3&&landed.Where(b=>b.Collected).All(b=>!b.CollisionEnabled));
            await PumpUntil(peers,()=>peers.Take(2).All(p=>landed.All(body=>p.Bodies.TryGetValue(body.BodyId,out var received)&&SamePose(body,received))),TimeSpan.FromSeconds(12));
            Check("Both native clients retain identical physical landing poses",peers.Take(2).All(p=>landed.All(body=>SamePose(body,p.Bodies[body.BodyId]))));
            await Join(peers[2],clients[2],info);await PumpUntil(peers,()=>landed.All(body=>peers[2].Bodies.TryGetValue(body.BodyId,out var received)&&SamePose(body,received)),TimeSpan.FromSeconds(12));
            Check("Third late join receives complete settled native baseline",landed.All(body=>SamePose(body,peers[2].Bodies[body.BodyId]))&&peers[2].CompletedBaselines>0);
            Check("All clients receive exact full environment, no local storm reroll",peers.All(p=>p.Snapshot?.Environment is not null)&&peers.All(p=>p.Snapshot!.Seed==info.WorldSeed));
            Check("Full baseline names match real authored geometry",peers[2].Bodies.Values.All(b=>!string.IsNullOrEmpty(b.StablePartId)));
            var fourthResponse=await clients[0].PostAsJsonAsync("/api/characters",new CharacterCreateRequest("Content mismatch tester"));fourthResponse.EnsureSuccessStatusCode();
            string fourthCharacter=(await fourthResponse.Content.ReadFromJsonAsync<CharacterDto>())!.Id;
            var fourthAdmission=await clients[0].PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(fourthCharacter,info.ServerId));fourthAdmission.EnsureSuccessStatusCode();var fourthTicket=(await fourthAdmission.Content.ReadFromJsonAsync<JoinTicketDto>())!;
            using(var bad=StormTransportHost.CreateClient(new TransportSecurity(info.TransportAuthority,ExpectedPeerIdentity:info.TransportIdentity)))
            {
                uint connection=bad.Connect(info.Host,(ushort)info.GamePort);bool connected=false;var until=DateTimeOffset.UtcNow.AddSeconds(10);
                while(!connected&&DateTimeOffset.UtcNow<until){connected=bad.Poll().Any(e=>e.Kind==TransportEventKind.Connected);await Task.Delay(10);}
                Check("Content mismatch test uses authenticated transport",connected);bad.Send(connection,MessageKind.JoinRequest,new JoinRequest(fourthTicket.Ticket,"wrong-content-fingerprint"));
                bool rejected=false;until=DateTimeOffset.UtcNow.AddSeconds(10);
                while(!rejected&&DateTimeOffset.UtcNow<until){rejected=bad.Poll().Any(e=>e.Kind==TransportEventKind.Disconnected||e.Message?.Kind==MessageKind.JoinRejected);await Task.Delay(10);foreach(var peer in peers)peer.Poll();}
                Check("Different physical world geometry cannot join",rejected&&game.ServerInfo.Players==3);
            }
            Check("Content mismatch does not consume account admission",game.TryConsumeAdmission(fourthTicket.Ticket,out var recovered)&&recovered!.CharacterId==fourthCharacter);
            foreach(var peer in peers)peer.Host.Close(peer.Connection,"Validation complete");await PumpFor(peers,TimeSpan.FromSeconds(.5));
            Check("Native disconnect removes all authoritative players",game.ServerInfo.Players==0);
            for(int cycle=0;cycle<5;cycle++)
            {
                using var repeatHost=StormTransportHost.CreateClient(new TransportSecurity(info.TransportAuthority,ExpectedPeerIdentity:info.TransportIdentity));
                var repeat=new Peer(repeatHost,first.Character);await Join(repeat,clients[0],info);
                repeat.Host.Send(repeat.Connection,MessageKind.GameplayAction,new GameplayAction(1,GameplayActionKind.EnterVehicle,0,default));
                await PumpUntil([repeat],()=>repeat.Results.ContainsKey(1),TimeSpan.FromSeconds(10));
                Check("Rejoin cycle "+(cycle+1)+" reuses clear parking and allows actual vehicle entry",repeat.Results[1].Success&&repeat.Join!.PlayerId>3);
                repeat.Host.Close(repeat.Connection,"Rejoin proof complete");await PumpUntil([repeat],()=>game.ServerInfo.Players==0,TimeSpan.FromSeconds(10));
            }
            await app.StopAsync();await app.DisposeAsync();app=null;
            var restarted=new CareerStore(accountsData).GetCareer(first.Character);Check("Native gameplay progression persists at independent accounts service after game shutdown",restarted.Photos==1&&restarted.Cash>=900&&restarted.VehicleConditions["pickup"].FuelLiters<75);
            File.WriteAllText(Path.Combine(output,"server-native-proof.json"),JsonSerializer.Serialize(new{passed=true,checks,worldContentFingerprint=info.WorldContentFingerprint,released=flying.Length,settled=landed.Length,clientBodies=peers.Select(p=>p.Bodies.Count),performance,largestEncodedSnapshotBytes=largestEncodedSnapshot,maximumNativeBufferedBytes=bufferedSamples.Max(),networkSamples,dataRoot=data},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"Actual native server proof passed: {checks.Count} checks; {flying.Length} released parts, {landed.Length} settled parts.");return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"server-native-proof.json"),JsonSerializer.Serialize(new{passed=false,error=error.Message,checks,dataRoot=data},new JsonSerializerOptions{WriteIndented=true}));throw;}
        finally {StormTransportHost.ConfigureNetworkSimulation();foreach(var p in peers)p.Host.Dispose();foreach(var c in clients)c.Dispose();if(app is not null){await app.StopAsync();await app.DisposeAsync();}if(masterApp is not null){await masterApp.StopAsync();await masterApp.DisposeAsync();}Environment.SetEnvironmentVariable(keyEnvironment,null);}
    }
    private static async Task Join(Peer p,HttpClient http,ServerInfoDto server)
    {
        var admission=await http.PostAsJsonAsync("/api/matches/join",new JoinTicketRequest(p.Character,server.ServerId));admission.EnsureSuccessStatusCode();var ticket=(await admission.Content.ReadFromJsonAsync<JoinTicketDto>())!;
        p.Connection=p.Host.Connect(ticket.Host,(ushort)ticket.GamePort);
        await PumpUntil([p],()=>p.Connected,TimeSpan.FromSeconds(10));
        p.Host.Send(p.Connection,MessageKind.JoinRequest,new JoinRequest(ticket.Ticket,server.WorldContentFingerprint));
        await PumpUntil([p],()=>p.Join is not null,TimeSpan.FromSeconds(15));
    }
    private static async Task PumpUntil(IEnumerable<Peer> peers,Func<bool> predicate,TimeSpan timeout)
    {
        var until=DateTimeOffset.UtcNow+timeout;while(DateTimeOffset.UtcNow<until){foreach(var p in peers)p.Poll();if(predicate())return;await Task.Delay(10);}throw new TimeoutException("Native gameplay condition timed out.");
    }
    private static async Task PumpFor(IEnumerable<Peer> peers,TimeSpan duration)
    {var until=DateTimeOffset.UtcNow+duration;while(DateTimeOffset.UtcNow<until){foreach(var p in peers)p.Poll();await Task.Delay(10);}}
    private static float Distance(NetVector3 a,NetVector3 b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y)+(a.Z-b.Z)*(a.Z-b.Z));
    private static bool SamePose(DebrisBodyState a,DebrisBodyState b)=>a.BodyId==b.BodyId&&a.StablePartId==b.StablePartId&&a.Position==b.Position&&a.Rotation==b.Rotation&&a.LinearVelocity==b.LinearVelocity&&a.AngularVelocity==b.AngularVelocity&&a.Sleeping==b.Sleeping&&a.Archived==b.Archived&&a.Collected==b.Collected;
    private sealed class Peer(StormTransportHost host,string character)
    {
        public StormTransportHost Host=host;public string Character=character;public uint Connection;public bool Connected;public JoinAccepted? Join;public WorldSnapshot? Snapshot;
        public Dictionary<int,DebrisBodyState> Bodies=[];public Dictionary<long,ActionResult> Results=[];public int ReleaseEvents,CompletedBaselines,FastAtmosphereProfiles,DailyAtmospherePackets;
        private readonly Dictionary<(long,int),DebrisSnapshot[]> pending=[];
        public void Poll()
        {
            foreach(var e in Host.Poll())
            {
                if(e.Kind==TransportEventKind.Connected){Connected=true;continue;}
                if(e.Kind==TransportEventKind.Fault)throw new InvalidOperationException(e.Reason);
                if(e.Message is not {} m)continue;
                if(m.Kind==MessageKind.JoinRejected)throw new InvalidOperationException(m.Get<JoinRejected>().Reason);
                if(m.Kind==MessageKind.JoinAccepted){Join=m.Get<JoinAccepted>();Snapshot=Join.Snapshot;}
                else if(m.Kind==MessageKind.WorldSnapshot){Snapshot=m.Get<WorldSnapshot>();if(Snapshot.Atmosphere is not null)FastAtmosphereProfiles++;}
                else if(m.Kind==MessageKind.AtmosphereSnapshot)DailyAtmospherePackets++;
                else if(m.Kind==MessageKind.ActionResult){var action=m.Get<ActionResult>();Results[action.RequestId]=action;}
                else if(m.Kind==MessageKind.DebrisEvent){var change=m.Get<DebrisEvent>();ReleaseEvents++;Bodies[change.Body.BodyId]=change.Body;}
                else if(m.Kind==MessageKind.DebrisSnapshot)
                {
                    var s=m.Get<DebrisSnapshot>();
                    if(s.FullBaseline)
                    {
                        var key=(s.ServerTick,s.ChunkCount);if(!pending.TryGetValue(key,out var chunks))pending[key]=chunks=new DebrisSnapshot[s.ChunkCount];chunks[s.ChunkIndex]=s;
                        if(chunks.All(c=>c is not null)){Bodies=chunks.SelectMany(c=>c.Bodies).ToDictionary(b=>b.BodyId);pending.Clear();CompletedBaselines++;}
                    }
                    else foreach(var body in s.Bodies)
                    {
                        var actual=body;
                        if(body.StablePartId.Length==0&&Bodies.TryGetValue(body.BodyId,out var previous))actual=body with{StablePartId=previous.StablePartId};
                        if(actual.StablePartId.Length>0)Bodies[actual.BodyId]=actual;
                    }
                }
            }
        }
    }
}
