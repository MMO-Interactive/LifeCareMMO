using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace StormChase.Server;

/// <summary>Server-only SQLite authority. Each mutation commits its receipt and progression atomically.</summary>
public partial class CareerStore
{
    private readonly string connectionString;
    private readonly PasswordHasher<AccountRecord> hasher=new();
    private readonly object mutationLock=new();
    partial void InitializeHousing(SqliteConnection db);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static readonly OfferDto[] Offers=
    [
        new("reinforced-probe","Reinforced probe",1600,"180 mph survivability and improved research income"),
        new("telephoto-camera","Telephoto camera",1400,"Longer range storm photography"),
        new("mobile-mesonet","Mobile mesonet",2400,"Vehicle mounted research equipment"),
        new("suv","Chase SUV",8000,"Larger probe rack and fuel capacity"),
        new("interceptor","Tornado Interceptor",24000,"Heavy protected intercept vehicle"),
        new("repair","Vehicle repair",250,"Restore the selected vehicle body"),
        // Index 6 matches the client's StormChaseCareer.Offers[6].
        new("chase-truck","Classic chase pickup",4500,"Built-in mobile mesonet rack and three probe slots")
    ];

    public CareerStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        connectionString=new SqliteConnectionStringBuilder { DataSource=Path.Combine(dataRoot,"career.sqlite"),Mode=SqliteOpenMode.ReadWriteCreate,Pooling=true,DefaultTimeout=10 }.ToString();
        using var db=Open();
        using var migration=db.CreateCommand();
        migration.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY,username TEXT NOT NULL,normalized_name TEXT NOT NULL UNIQUE,password_hash TEXT NOT NULL,created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS characters(id TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES accounts(id),name TEXT NOT NULL,normalized_name TEXT NOT NULL,created_utc TEXT NOT NULL,UNIQUE(account_id,normalized_name));
            CREATE TABLE IF NOT EXISTS careers(character_id TEXT PRIMARY KEY REFERENCES characters(id),cash_cents INTEGER NOT NULL CHECK(cash_cents>=0),reputation INTEGER NOT NULL CHECK(reputation>=0),version INTEGER NOT NULL,document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS operations(character_id TEXT NOT NULL REFERENCES characters(id),operation_id TEXT NOT NULL,kind TEXT NOT NULL,payload TEXT NOT NULL,result TEXT NOT NULL,created_utc TEXT NOT NULL,PRIMARY KEY(character_id,operation_id));
            CREATE TABLE IF NOT EXISTS server_state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            """;
        migration.ExecuteNonQuery();
        InitializeHousing(db);
    }

    private SqliteConnection Open()
    {
        var db=new SqliteConnection(connectionString); db.Open();
        using var command=db.CreateCommand(); command.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;"; command.ExecuteNonQuery();
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db,SqliteTransaction? transaction,string sql,params (string Name,object Value)[] values)
    {
        var command=db.CreateCommand(); command.Transaction=transaction; command.CommandText=sql;
        foreach(var item in values) command.Parameters.AddWithValue(item.Name,item.Value);
        return command;
    }
    public AccountDto Register(string username,string password)
    {
        ValidateUsername(username); ValidatePassword(password);
        var id=Guid.NewGuid().ToString("N"); var account=new AccountRecord(id,username,"");
        using var db=Open(); using var command=Command(db,null,"INSERT INTO accounts VALUES($id,$username,$normalized,$hash,$created)",
            ("$id",id),("$username",username),("$normalized",username.ToUpperInvariant()),("$hash",hasher.HashPassword(account,password)),("$created",DateTimeOffset.UtcNow.ToString("O")));
        try { command.ExecuteNonQuery(); } catch(SqliteException e) when(e.SqliteErrorCode==19) { throw new CareerConflictException("Account name is already registered."); }
        return new(id,username);
    }
    public AccountRecord? Login(string username,string password)
    {
        if(username is null || password is null || username.Length>32 || password.Length>128) return null;
        using var db=Open(); using var command=Command(db,null,"SELECT id,username,password_hash FROM accounts WHERE normalized_name=$name",("$name",username.ToUpperInvariant()));
        using var reader=command.ExecuteReader();
        AccountRecord? account=reader.Read() ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2)) : null;
        // Verify a fixed dummy hash for absent names to avoid a cheap username timing oracle.
        var dummy=new AccountRecord("dummy","dummy",DummyPasswordHash);
        var selected=account??dummy;
        var result=hasher.VerifyHashedPassword(selected,selected.PasswordHash,password);
        return account is not null && result!=PasswordVerificationResult.Failed ? account : null;
    }
    private static readonly string DummyPasswordHash=new PasswordHasher<AccountRecord>().HashPassword(new("dummy","dummy",""),"An absent-account sentinel that is never a credential");
    public virtual AccountRecord? FindAccount(string accountId)
    {
        using var db=Open(); using var command=Command(db,null,"SELECT id,username,password_hash FROM accounts WHERE id=$id",("$id",accountId));
        using var reader=command.ExecuteReader(); return reader.Read() ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2)) : null;
    }
    public virtual CharacterDto[] Characters(string accountId)
    {
        using var db=Open(); using var command=Command(db,null,"SELECT c.id,c.name,p.reputation,c.created_utc FROM characters c JOIN careers p ON p.character_id=c.id WHERE c.account_id=$id ORDER BY c.created_utc",("$id",accountId));
        using var reader=command.ExecuteReader(); var result=new List<CharacterDto>();
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture)));
        return result.ToArray();
    }
    public virtual CharacterDto CreateCharacter(string accountId,string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length is <2 or >32 || name.Any(c=>char.IsControl(c))) throw new ArgumentException("Character name must be 2..32 visible characters.");
        name=name.Trim();
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction();
            using var count=Command(db,transaction,"SELECT count(*) FROM characters WHERE account_id=$id",("$id",accountId));
            if(Convert.ToInt32(count.ExecuteScalar(),CultureInfo.InvariantCulture)>=4) throw new CareerConflictException("An account may own at most four characters.");
            var id=Guid.NewGuid().ToString("N"); var now=DateTimeOffset.UtcNow;
            using var insert=Command(db,transaction,"INSERT INTO characters VALUES($id,$account,$name,$normalized,$created)",("$id",id),("$account",accountId),("$name",name),("$normalized",name.ToUpperInvariant()),("$created",now.ToString("O")));
            try { insert.ExecuteNonQuery(); } catch(SqliteException e) when(e.SqliteErrorCode==19) { throw new CareerConflictException("That character name is already used by this account."); }
            var career=new CareerDto(id,900,0,0,false,["pickup"],"pickup",new() { ["pickup"]=new(75,100) },0,1);
            using var progress=Command(db,transaction,"INSERT INTO careers VALUES($id,$cash,0,1,$document)",("$id",id),("$cash",90000L),("$document",JsonSerializer.Serialize(career,Json)));
            progress.ExecuteNonQuery(); transaction.Commit(); return new(id,name,0,now);
        }
    }
    public virtual bool Owns(string accountId,string characterId)
    {
        using var db=Open(); using var command=Command(db,null,"SELECT EXISTS(SELECT 1 FROM characters WHERE id=$character AND account_id=$account)",("$character",characterId),("$account",accountId));
        return Convert.ToInt64(command.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;
    }
    public virtual AuthenticatedCharacter? Character(string accountId,string characterId)
    {
        if(!Owns(accountId,characterId)) return null;
        var character=Characters(accountId).First(c=>c.Id==characterId);
        return new(accountId,character.Id,character.Name,GetCareer(characterId));
    }
    public virtual CareerDto GetCareer(string characterId)
    {
        using var db=Open(); return ReadCareer(db,null,characterId);
    }
    private static CareerDto ReadCareer(SqliteConnection db,SqliteTransaction? transaction,string characterId)
    {
        using var command=Command(db,transaction,"SELECT cash_cents,reputation,version,document FROM careers WHERE character_id=$id",("$id",characterId));
        using var reader=command.ExecuteReader(); if(!reader.Read()) throw new KeyNotFoundException("Character was not found.");
        var state=JsonSerializer.Deserialize<CareerDto>(reader.GetString(3),Json)??throw new InvalidDataException("Career document is invalid.");
        return state with { Cash=reader.GetInt64(0)/100m,Reputation=reader.GetInt64(1),Version=reader.GetInt64(2) };
    }
    private static void WriteCareer(SqliteConnection db,SqliteTransaction transaction,CareerDto state)
    {
        using var command=Command(db,transaction,"UPDATE careers SET cash_cents=$cash,reputation=$reputation,version=$version,document=$document WHERE character_id=$id",
            ("$cash",checked((long)(state.Cash*100))), ("$reputation",state.Reputation),("$version",state.Version),("$document",JsonSerializer.Serialize(state,Json)),("$id",state.CharacterId));
        if(command.ExecuteNonQuery()!=1) throw new KeyNotFoundException("Character was not found.");
    }
    private static string? FindOperation(SqliteConnection db,SqliteTransaction transaction,string id,string operationId,string kind,string payload)
    {
        using var command=Command(db,transaction,"SELECT kind,payload,result FROM operations WHERE character_id=$id AND operation_id=$operation",("$id",id),("$operation",operationId));
        using var reader=command.ExecuteReader(); if(!reader.Read()) return null;
        if(reader.GetString(0)!=kind || reader.GetString(1)!=payload) throw new CareerConflictException("Operation ID already belongs to a different request.");
        return reader.GetString(2);
    }
    private static void AddOperation(SqliteConnection db,SqliteTransaction transaction,string id,string operationId,string kind,string payload,object result)
    {
        using var command=Command(db,transaction,"INSERT INTO operations VALUES($id,$operation,$kind,$payload,$result,$created)",("$id",id),("$operation",operationId),("$kind",kind),("$payload",payload),("$result",JsonSerializer.Serialize(result,Json)),("$created",DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
    }
    public virtual PurchaseDto Purchase(string characterId,PurchaseRequest request)
    {
        ValidateOperation(request.OperationId);
        var offer=Offers.FirstOrDefault(o=>o.Sku==request.Sku)??throw new ArgumentException("Unknown offer.");
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction();
            var previous=FindOperation(db,transaction,characterId,request.OperationId,"purchase",offer.Sku);
            if(previous is not null) return (JsonSerializer.Deserialize<PurchaseDto>(previous,Json)??throw new InvalidDataException()) with { Duplicate=true,Career=ReadCareer(db,transaction,characterId) };
            var state=ReadCareer(db,transaction,characterId);
            bool owned=offer.Sku switch { "reinforced-probe"=>state.ProbeLevel>0,"telephoto-camera"=>state.CameraLevel>0,"mobile-mesonet"=>state.Mesonet,"suv" or "interceptor" or "chase-truck"=>state.OwnedVehicles.Contains(offer.Sku),_=>false };
            PurchaseDto result;
            if(owned) result=new(false,false,"Already owned.",state);
            else if(state.Cash<offer.Price) result=new(false,false,"Insufficient funds.",state);
            else
            {
                var conditions=new Dictionary<string,VehicleConditionDto>(state.VehicleConditions);
                var ownedVehicles=state.OwnedVehicles;
                if(offer.Sku is "suv" or "interceptor" or "chase-truck") { ownedVehicles=[..ownedVehicles,offer.Sku]; conditions[offer.Sku]=new(offer.Sku switch{"suv"=>90,"chase-truck"=>95,_=>125},100); }
                if(offer.Sku=="repair") conditions[state.SelectedVehicle]=conditions[state.SelectedVehicle] with { Health=100 };
                state=state with { Cash=state.Cash-offer.Price,ProbeLevel=offer.Sku=="reinforced-probe"?1:state.ProbeLevel,
                    CameraLevel=offer.Sku=="telephoto-camera"?1:state.CameraLevel,Mesonet=offer.Sku=="mobile-mesonet"||state.Mesonet,
                    OwnedVehicles=ownedVehicles,SelectedVehicle=offer.Sku is "suv" or "interceptor" or "chase-truck"?offer.Sku:state.SelectedVehicle,
                    VehicleConditions=conditions,Version=state.Version+1 };
                WriteCareer(db,transaction,state); result=new(true,false,"Purchase completed.",state);
            }
            AddOperation(db,transaction,characterId,request.OperationId,"purchase",offer.Sku,result); transaction.Commit(); return result;
        }
    }
    public virtual CareerDto SelectVehicle(string characterId,string vehicleId)
    {
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction(); var state=ReadCareer(db,transaction,characterId);
            if(!state.OwnedVehicles.Contains(vehicleId)) throw new ArgumentException("Vehicle is not owned.");
            state=state with { SelectedVehicle=vehicleId,Version=state.Version+1 }; WriteCareer(db,transaction,state); transaction.Commit(); return state;
        }
    }
    /// <summary>Trusted simulation seam. There is deliberately no public HTTP credit endpoint.</summary>
    public virtual CareerDto ApplyReward(string characterId,string serverEventId,decimal cash,long reputation,int photos=0,int researchSamples=0,int completedChases=0,int popularity=0,int completedContracts=0)
    {
        ValidateOperation(serverEventId);
        if(cash is <0 or >10000 || decimal.Round(cash,2)!=cash || reputation is <0 or >1000 || photos is <0 or >1 || researchSamples is <0 or >1 || completedChases is <0 or >1||popularity is <0 or >100||completedContracts is <0 or >1) throw new ArgumentException("Reward exceeds the bounded simulation receipt.");
        string payload=cash.ToString(CultureInfo.InvariantCulture)+":"+reputation.ToString(CultureInfo.InvariantCulture)+":"+photos+":"+researchSamples+":"+completedChases+":"+popularity+":"+completedContracts;
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction();
            if(FindOperation(db,transaction,characterId,serverEventId,"reward",payload) is not null) return ReadCareer(db,transaction,characterId);
            var state=ReadCareer(db,transaction,characterId);
            state=state with { Cash=checked(state.Cash+cash),Reputation=checked(state.Reputation+reputation),Version=state.Version+1,
                Photos=checked(state.Photos+photos),ResearchSamples=checked(state.ResearchSamples+researchSamples),CompletedChases=checked(state.CompletedChases+completedChases),Popularity=checked(state.Popularity+popularity),CompletedContracts=checked(state.CompletedContracts+completedContracts) };
            WriteCareer(db,transaction,state); AddOperation(db,transaction,characterId,serverEventId,"reward",payload,state); transaction.Commit(); return state;
        }
    }
    public virtual void UpdateVehicle(string characterId,float fuelLiters,float health)
    {
        if(!float.IsFinite(fuelLiters)||!float.IsFinite(health)||fuelLiters<0||fuelLiters>125||health<0||health>100) throw new ArgumentException("Invalid authoritative vehicle state.");
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction(); var state=ReadCareer(db,transaction,characterId);
            var conditions=new Dictionary<string,VehicleConditionDto>(state.VehicleConditions) { [state.SelectedVehicle]=new(fuelLiters,health) };
            state=state with { VehicleConditions=conditions,Version=state.Version+1 }; WriteCareer(db,transaction,state); transaction.Commit();
        }
    }
    public virtual CareerDto Refuel(string characterId,float requestedLiters,float currentFuel,float currentHealth,float capacity)
    {
        if(!float.IsFinite(requestedLiters)||requestedLiters<0||requestedLiters>2||!float.IsFinite(capacity)||capacity is <1 or >125||!float.IsFinite(currentFuel)||currentFuel<0||currentFuel>capacity||!float.IsFinite(currentHealth)||currentHealth is <0 or >100) throw new ArgumentException("Invalid pump amount.");
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction(); var state=ReadCareer(db,transaction,characterId);
            decimal cost=Math.Min(state.Cash,decimal.Round((decimal)Math.Min(requestedLiters,Math.Max(0,capacity-currentFuel))*1.35m,2));
            var conditions=new Dictionary<string,VehicleConditionDto>(state.VehicleConditions) { [state.SelectedVehicle]=new(Math.Min(capacity,currentFuel+(float)(cost/1.35m)),currentHealth) };
            state=state with { Cash=state.Cash-cost,VehicleConditions=conditions,Version=state.Version+1 }; WriteCareer(db,transaction,state); transaction.Commit(); return state;
        }
    }
    public virtual CareerDto Recover(string characterId)
    {
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction(); var state=ReadCareer(db,transaction,characterId);
            var condition=state.VehicleConditions[state.SelectedVehicle];
            var conditions=new Dictionary<string,VehicleConditionDto>(state.VehicleConditions) { [state.SelectedVehicle]=new(Math.Max(8,condition.FuelLiters),Math.Max(50,condition.Health)) };
            state=state with { Cash=Math.Max(0,state.Cash-200),VehicleConditions=conditions,Version=state.Version+1 }; WriteCareer(db,transaction,state); transaction.Commit(); return state;
        }
    }
    public virtual CareerDto SaveContracts(string characterId,string document)
    {
        ValidateContracts(document);
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();var state=ReadCareer(db,tx,characterId);
            state=state with{ContractState=document,Version=state.Version+1};WriteCareer(db,tx,state);tx.Commit();return state;
        }
    }
    public virtual CareerDto CommitContract(string characterId,string receipt,int contractId,decimal cash,int reputation,int popularity,string completedDocument)
    {
        ValidateOperation(receipt);var completed=ValidateContracts(completedDocument);
        if(cash is <0 or >10000||decimal.Round(cash,2)!=cash||reputation is <0 or >1000||popularity is <0 or >100||completed.Active is not null||!completed.Completed.Contains(contractId))throw new ArgumentException("Invalid contract completion.");
        string payload=contractId+":"+cash.ToString(CultureInfo.InvariantCulture)+":"+reputation+":"+popularity;
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();if(FindOperation(db,tx,characterId,receipt,"contract",payload) is not null)return ReadCareer(db,tx,characterId);
            var state=ReadCareer(db,tx,characterId);var prior=ValidateContracts(state.ContractState);
            if(prior.Active is not{} run||run.OfferId!=contractId||receipt!="contract:"+run.InstanceId||prior.Completed.Contains(contractId)||completed.TotalCompleted!=prior.TotalCompleted+1)throw new CareerConflictException("Contract completion does not match active assignment.");
            state=state with{Cash=checked(state.Cash+cash),Reputation=checked(state.Reputation+reputation),Popularity=checked(state.Popularity+popularity),CompletedContracts=checked(state.CompletedContracts+1),ContractState=completedDocument,Version=state.Version+1};
            WriteCareer(db,tx,state);AddOperation(db,tx,characterId,receipt,"contract",payload,state);tx.Commit();return state;
        }
    }
    private static ChaseContractProgress ValidateContracts(string document)
    {
        if(document is null||document.Length>8192)throw new ArgumentException("Contract state exceeds its bounded save.");
        ChaseContractProgress progress;
        try{progress=document.Length==0?new():JsonSerializer.Deserialize<ChaseContractProgress>(document,Json)??throw new ArgumentException("Invalid contract state.");}catch(JsonException){throw new ArgumentException("Invalid contract state.");}
        if(progress.TotalCompleted<0||progress.Completed.Count>256||progress.Active is{} run&&(run.OfferId<1||run.Day is <1 or >100000||run.InstanceId.Length!=32||!Guid.TryParseExact(run.InstanceId,"N",out _)||run.Step is <0 or >32||!float.IsFinite(run.WorkSeconds)||run.WorkSeconds is <0 or >3600||run.ClearedPieces is <0 or >3||run.CleanupOrigin is{} point&&(!float.IsFinite(point.X)||!float.IsFinite(point.Y)||!float.IsFinite(point.Z))))throw new ArgumentException("Invalid contract progression.");
        return progress;
    }
    public string GetOrCreateServerId()
    {
        lock(mutationLock)
        {
            using var db=Open(); using var transaction=db.BeginTransaction();
            using var get=Command(db,transaction,"SELECT value FROM server_state WHERE key='server-id'");
            var found=get.ExecuteScalar() as string; if(found is not null) return found;
            var id=Guid.NewGuid().ToString("N"); using var put=Command(db,transaction,"INSERT INTO server_state VALUES('server-id',$value)",("$value",id));
            put.ExecuteNonQuery(); transaction.Commit(); return id;
        }
    }
    public double LoadCampaignSeconds()
    {
        using var db=Open(); using var get=Command(db,null,"SELECT value FROM server_state WHERE key='campaign-seconds'");
        return double.TryParse(get.ExecuteScalar() as string,NumberStyles.Float,CultureInfo.InvariantCulture,out var elapsed)&&elapsed>=0&&double.IsFinite(elapsed)?elapsed:0;
    }
    public void SaveCampaignSeconds(double elapsed)
    {
        using var db=Open(); using var put=Command(db,null,"INSERT INTO server_state VALUES('campaign-seconds',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$value",elapsed.ToString("R",CultureInfo.InvariantCulture))); put.ExecuteNonQuery();
    }
    public static void ValidateUsername(string username)
    {
        if(string.IsNullOrWhiteSpace(username)||username.Length is <3 or >32||username.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='_'))) throw new ArgumentException("Account names must contain 3..32 ASCII letters, digits or underscores.");
    }
    private static void ValidatePassword(string password)
    {
        if(string.IsNullOrEmpty(password)||password.Length is <10 or >128) throw new ArgumentException("Passwords must contain 10..128 characters.");
    }
    public static void ValidateOperation(string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>96||id.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or ':' or '.'))) throw new ArgumentException("Operation ID must contain 1..96 ASCII letters, digits, dash, underscore, colon or period.");
    }
}
public sealed class CareerConflictException(string message):Exception(message);
