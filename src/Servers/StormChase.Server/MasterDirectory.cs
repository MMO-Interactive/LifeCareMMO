using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StormChase.Server;

public sealed record ServerRegistration(string ApiAddress,ServerInfoDto Server,string[] ActiveCharacters);
public sealed record RedeemAdmissionRequest(string Ticket,string ServerId);
public sealed record RemotePurchaseRequest(string CharacterId,PurchaseRequest Purchase);
public sealed record RemoteSelectRequest(string CharacterId,string VehicleId);
public sealed record RemoteRewardRequest(string CharacterId,string EventId,decimal Cash,long Reputation,int Photos,int ResearchSamples,int CompletedChases,int Popularity=0,int CompletedContracts=0);
public sealed record RemoteConditionRequest(string CharacterId,float FuelLiters,float Health);
public sealed record RemoteRefuelRequest(string CharacterId,float RequestedLiters,float CurrentFuel,float CurrentHealth,float Capacity);
public sealed record RemoteCharacterRequest(string AccountId,string Name);
public sealed record RemoteContractSave(string CharacterId,string Document);
public sealed record RemoteContractCommit(string CharacterId,string Receipt,int ContractId,decimal Cash,int Reputation,int Popularity,string Document);

/// <summary>Independent accounts service: one identity/career authority and a private trusted-host directory.</summary>
public sealed class MasterDirectory
{
    private readonly CareerStore store;private readonly object gate=new();private readonly string key;
    private readonly Dictionary<string,Entry> servers=[];private readonly Dictionary<string,Ticket> tickets=[];private readonly Dictionary<string,(string ServerId,DateTimeOffset Expires)> active=[];
    public MasterDirectory(ServerOptions options,CareerStore store)
    {
        this.store=store;string path=Path.Combine(options.DataRoot,"master-server.key");
        if(!File.Exists(path))
        {
            try{using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);using var writer=new StreamWriter(file,new UTF8Encoding(false));writer.Write(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));}
            catch(IOException)when(File.Exists(path)){}
        }
        key=File.ReadAllText(path).Trim();
        if(key.Length!=64||key.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidDataException("Master registration key must contain exactly 32 bytes in hexadecimal.");
    }
    public bool IsTrusted(HttpContext context)
    {
        string supplied=context.Request.Headers["X-StormChase-Server"].ToString();
        return supplied.Length==key.Length&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(key));
    }
    public ServerInfoDto[] List(){lock(gate)return servers.Values.Where(e=>e.Seen>DateTimeOffset.UtcNow.AddSeconds(-35)).Select(e=>e.Server).OrderBy(e=>e.Name).ToArray();}
    public void Register(ServerRegistration registration)
    {
        if(!Uri.TryCreate(registration.ApiAddress,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||registration.ApiAddress.Length>256||registration.Server.ServerId.Length!=32||registration.ActiveCharacters.Length>64)throw new ArgumentException("Invalid game host registration.");
        if(uri.Scheme!="https"&&!(uri.Host=="localhost"||IPAddress.TryParse(uri.Host,out var address)&&IPAddress.IsLoopback(address)))throw new ArgumentException("Registered public game hosts require HTTPS.");
        lock(gate)
        {
            if(servers.Count>=128&&!servers.ContainsKey(registration.Server.ServerId))throw new CareerConflictException("Directory capacity reached.");
            var now=DateTimeOffset.UtcNow;servers[registration.Server.ServerId]=new(registration.Server with{ApiAddress=registration.ApiAddress.TrimEnd('/')},now);
            foreach(var character in registration.ActiveCharacters)
                if(active.TryGetValue(character,out var lease)&&lease.ServerId==registration.Server.ServerId)active[character]=(lease.ServerId,now.AddSeconds(35));
            foreach(var character in active.Where(a=>a.Value.ServerId==registration.Server.ServerId&&!registration.ActiveCharacters.Contains(a.Key)).Select(a=>a.Key).ToArray())active.Remove(character);
        }
    }
    public JoinTicketDto? Issue(string account,string character,string? target)
    {
        var owned=store.Character(account,character);if(owned is null)return null;
        lock(gate)
        {
            Prune();if(target is null||!servers.TryGetValue(target,out var host)||host.Seen<DateTimeOffset.UtcNow.AddSeconds(-35)||!host.Server.Ready)throw new CareerConflictException("Select a ready game server from the directory.");
            if(host.Server.Players>=host.Server.MaxPlayers)throw new CareerConflictException("Server is full.");
            if(active.ContainsKey(character))throw new CareerConflictException("Character is already connected to a game server.");
            if(tickets.Count>=1024)throw new CareerConflictException("Admission queue is full.");
            foreach(var previous in tickets.Where(t=>t.Value.Character.CharacterId==character).Select(t=>t.Key).ToArray())tickets.Remove(previous);
            var ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var expiry=DateTimeOffset.UtcNow.AddSeconds(30);
            tickets[ticket]=new(target,owned,expiry);return new(ticket,expiry,host.Server.Host,host.Server.GamePort,target,character,host.Server.ApiAddress);
        }
    }
    public AuthenticatedCharacter? Redeem(RedeemAdmissionRequest request)
    {
        lock(gate)
        {
            Prune();if(!tickets.TryGetValue(request.Ticket,out var ticket)||ticket.Server!=request.ServerId||active.ContainsKey(ticket.Character.CharacterId)||!servers.TryGetValue(request.ServerId,out var host)||!host.Server.Ready||host.Seen<DateTimeOffset.UtcNow.AddSeconds(-35))return null;
            tickets.Remove(request.Ticket);active[ticket.Character.CharacterId]=(request.ServerId,DateTimeOffset.UtcNow.AddSeconds(35));return ticket.Character with{Career=store.GetCareer(ticket.Character.CharacterId)};
        }
    }
    public bool HasLease(string character,string server){lock(gate){Prune();return active.TryGetValue(character,out var lease)&&lease.ServerId==server;}}
    public void Release(string character,string server){lock(gate)if(active.TryGetValue(character,out var lease)&&lease.ServerId==server)active.Remove(character);}
    public void Stop(string server){lock(gate){servers.Remove(server);foreach(var id in active.Where(a=>a.Value.ServerId==server).Select(a=>a.Key).ToArray())active.Remove(id);}}
    public ServerInfoDto? ActiveServer(string character){lock(gate){Prune();return active.TryGetValue(character,out var lease)&&servers.TryGetValue(lease.ServerId,out var entry)?entry.Server:null;}}
    public bool Registered(string server){lock(gate)return servers.TryGetValue(server,out var entry)&&entry.Seen>DateTimeOffset.UtcNow.AddSeconds(-35);}
    public async Task<T> Forward<T>(ServerInfoDto server,string route,object body)
    {
        using var client=new HttpClient{BaseAddress=new(server.ApiAddress),Timeout=TimeSpan.FromSeconds(10)};client.DefaultRequestHeaders.Add("X-StormChase-Server",key);
        var result=await client.PostAsJsonAsync(route,body);
        if(result.StatusCode==HttpStatusCode.Conflict)throw new CareerConflictException("Park at an intact service station on the game server.");
        result.EnsureSuccessStatusCode();return(await result.Content.ReadFromJsonAsync<T>())!;
    }
    private void Prune()
    {
        var now=DateTimeOffset.UtcNow;foreach(var t in tickets.Where(t=>t.Value.Expiry<=now).Select(t=>t.Key).ToArray())tickets.Remove(t);
        foreach(var t in active.Where(t=>t.Value.Expires<=now).Select(t=>t.Key).ToArray())active.Remove(t);
    }
    private sealed record Entry(ServerInfoDto Server,DateTimeOffset Seen);
    private sealed record Ticket(string Server,AuthenticatedCharacter Character,DateTimeOffset Expiry);
}
