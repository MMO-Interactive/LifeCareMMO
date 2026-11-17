using System.Net;
using System.Net.Http.Json;

namespace StormChase.Server;

/// <summary>Game hosts retain world state locally; all account/career mutations commit at the independent master.</summary>
public sealed partial class RemoteCareerStore:CareerStore,IDisposable
{
    public HttpClient Client {get;}
    public string ServerId {get;}
    private readonly string key;
    private readonly Dictionary<string,(CareerDto Value,DateTimeOffset Expires)> cache=[];
    private readonly object cacheGate=new();
    public RemoteCareerStore(ServerOptions options):base(options.DataRoot)
    {
        ServerId=GetOrCreateServerId();Client=new(){BaseAddress=new(options.MasterUrl!),Timeout=TimeSpan.FromSeconds(10)};
        key=Environment.GetEnvironmentVariable(options.MasterKeyEnvironment!)??throw new InvalidOperationException("Missing master server key.");
        Client.DefaultRequestHeaders.Add("X-StormChase-Server",key);Client.DefaultRequestHeaders.Add("X-StormChase-Game",ServerId);
    }
    private T Get<T>(string path)=>Read<T>(Client.GetAsync(path).GetAwaiter().GetResult());
    public T Post<T>(string path,object request)=>Read<T>(Client.PostAsJsonAsync(path,request).GetAwaiter().GetResult());
    private static T Read<T>(HttpResponseMessage response)
    {
        using(response)
        {
            if(response.StatusCode==HttpStatusCode.Conflict)throw new CareerConflictException("Master rejected conflicting career/admission operation.");
            if(response.StatusCode==HttpStatusCode.BadRequest)throw new ArgumentException("Master rejected invalid career request.");
            if(response.StatusCode==HttpStatusCode.NotFound)throw new KeyNotFoundException();response.EnsureSuccessStatusCode();return response.Content.ReadFromJsonAsync<T>().GetAwaiter().GetResult()!;
        }
    }
    public override AccountRecord? FindAccount(string account)=>Get<AccountRecord?>("/api/internal/accounts/"+Uri.EscapeDataString(account));
    public override CharacterDto[] Characters(string account)=>Get<CharacterDto[]>("/api/internal/characters?accountId="+Uri.EscapeDataString(account));
    public override CharacterDto CreateCharacter(string account,string name)=>Post<CharacterDto>("/api/internal/characters",new RemoteCharacterRequest(account,name));
    public override bool Owns(string account,string character)=>Get<bool>("/api/internal/ownership?accountId="+Uri.EscapeDataString(account)+"&characterId="+Uri.EscapeDataString(character));
    public override AuthenticatedCharacter? Character(string account,string character)=>Owns(account,character)?new(account,character,Characters(account).Single(c=>c.Id==character).Name,GetCareer(character)):null;
    private CareerDto Remember(CareerDto state){lock(cacheGate)cache[state.CharacterId]=(state,DateTimeOffset.UtcNow.AddSeconds(1));return state;}
    public override CareerDto GetCareer(string character)
    {lock(cacheGate)if(cache.TryGetValue(character,out var saved)&&saved.Expires>DateTimeOffset.UtcNow)return saved.Value;return Remember(Get<CareerDto>("/api/internal/careers/"+Uri.EscapeDataString(character)));}
    public override PurchaseDto Purchase(string character,PurchaseRequest request){var result=Post<PurchaseDto>("/api/internal/careers/purchase",new RemotePurchaseRequest(character,request));Remember(result.Career);return result;}
    public override CareerDto SelectVehicle(string character,string vehicle)=>Remember(Post<CareerDto>("/api/internal/careers/select",new RemoteSelectRequest(character,vehicle)));
    public override CareerDto ApplyReward(string character,string receipt,decimal cash,long reputation,int photos=0,int researchSamples=0,int completedChases=0,int popularity=0,int completedContracts=0)=>Remember(Post<CareerDto>("/api/internal/careers/reward",new RemoteRewardRequest(character,receipt,cash,reputation,photos,researchSamples,completedChases,popularity,completedContracts)));
    public override void UpdateVehicle(string character,float fuel,float health)=>Remember(Post<CareerDto>("/api/internal/careers/condition",new RemoteConditionRequest(character,fuel,health)));
    public override CareerDto Refuel(string character,float requested,float fuel,float health,float capacity)=>Remember(Post<CareerDto>("/api/internal/careers/refuel",new RemoteRefuelRequest(character,requested,fuel,health,capacity)));
    public override CareerDto Recover(string character)=>Remember(Post<CareerDto>("/api/internal/careers/recover",new {characterId=character}));
    public override CareerDto SaveContracts(string character,string document)=>Remember(Post<CareerDto>("/api/internal/careers/contracts-save",new RemoteContractSave(character,document)));
    public override CareerDto CommitContract(string character,string receipt,int contractId,decimal cash,int reputation,int popularity,string document)=>Remember(Post<CareerDto>("/api/internal/careers/contracts-commit",new RemoteContractCommit(character,receipt,contractId,cash,reputation,popularity,document)));
    public AuthenticatedCharacter? Redeem(string ticket)=>Post<AuthenticatedCharacter?>("/api/internal/admissions/redeem",new RedeemAdmissionRequest(ticket,ServerId));
    public void Release(string character)=>Post<bool>("/api/internal/admissions/release",new{characterId=character});
    public bool IsTrusted(HttpContext context)=>context.Request.Headers["X-StormChase-Server"].ToString() is{} supplied&&supplied.Length==key.Length&&System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied),System.Text.Encoding.UTF8.GetBytes(key));
    public void Dispose()=>Client.Dispose();
}
