using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StormChase.Protocol;

namespace StormChase.Server;

public partial class CareerStore
{
    partial void InitializeHousing(SqliteConnection db)
    {
        using var c=Command(db,null,"""
            CREATE TABLE IF NOT EXISTS housing_sites(world TEXT NOT NULL,home_id INTEGER NOT NULL,building_id TEXT NOT NULL,document TEXT NOT NULL,PRIMARY KEY(world,home_id),UNIQUE(world,building_id));
            CREATE TABLE IF NOT EXISTS housing_deeds(world TEXT NOT NULL,home_id INTEGER NOT NULL,character_id TEXT NOT NULL REFERENCES characters(id),owner_name TEXT NOT NULL,visits INTEGER NOT NULL DEFAULT 1,PRIMARY KEY(world,home_id),UNIQUE(world,character_id),FOREIGN KEY(world,home_id) REFERENCES housing_sites(world,home_id));
            CREATE TABLE IF NOT EXISTS housing_furniture(id INTEGER PRIMARY KEY AUTOINCREMENT,world TEXT NOT NULL,home_id INTEGER NOT NULL,character_id TEXT NOT NULL REFERENCES characters(id),sku TEXT NOT NULL,document TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(world,home_id) REFERENCES housing_deeds(world,home_id));
            CREATE INDEX IF NOT EXISTS housing_furniture_owner ON housing_furniture(world,character_id);
            """);c.ExecuteNonQuery();
    }
    /// <summary>
    /// Carries one home's deed and furniture over from an earlier world version whose existing houses (building ids,
    /// HomeIds and placement) are unchanged in the current one, so a world update does not strand owned homes.
    /// Rows are copied, never moved (an older server build still finds its own), only when the same home id names the
    /// same building in both worlds, and once per home: a server_state marker stops a later sale being resurrected.
    /// </summary>
    private static void CopyCompatibleHome(SqliteConnection db,SqliteTransaction tx,string from,string to,HomeSiteState site)
    {
        if(from==to)return;
        string marker="housing-migrated/"+from+"/"+to+"/"+site.HomeId.ToString(CultureInfo.InvariantCulture);
        using(var seen=Command(db,tx,"SELECT 1 FROM server_state WHERE key=$key",("$key",marker)))if(seen.ExecuteScalar() is not null)return;
        using(var deed=Command(db,tx,"""
            INSERT OR IGNORE INTO housing_deeds(world,home_id,character_id,owner_name,visits)
            SELECT $to,d.home_id,d.character_id,d.owner_name,d.visits FROM housing_deeds d
            JOIN housing_sites s ON s.world=d.world AND s.home_id=d.home_id AND s.building_id=$building
            WHERE d.world=$from AND d.home_id=$id
            """,("$to",to),("$from",from),("$id",site.HomeId),("$building",site.BuildingId)))
            if(deed.ExecuteNonQuery()>0)
            {
                using var furniture=Command(db,tx,"""
                    INSERT INTO housing_furniture(world,home_id,character_id,sku,document,revision)
                    SELECT $to,f.home_id,f.character_id,f.sku,f.document,f.revision FROM housing_furniture f
                    JOIN housing_deeds n ON n.world=$to AND n.home_id=f.home_id AND n.character_id=f.character_id
                    WHERE f.world=$from AND f.home_id=$id ORDER BY f.id
                    """,("$to",to),("$from",from),("$id",site.HomeId));
                furniture.ExecuteNonQuery();
            }
        using var mark=Command(db,tx,"INSERT OR IGNORE INTO server_state(key,value) VALUES($key,$value)",("$key",marker),("$value",DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture)));
        mark.ExecuteNonQuery();
    }
    private static void ValidateHousingWorld(string world)
    {if(world is null||world.Length!=64||world.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException("Invalid world content fingerprint.");}
    public virtual bool RegisterHousingSites(HousingSitesRequest request)
    {
        ValidateHousingWorld(request.World);if(request.Sites is null||request.Sites.Length is <1 or >10)throw new ArgumentException("Register 1..10 house sites per request.");
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();
            foreach(var site in request.Sites)
            {
                string[] path=site.BuildingId.Split('/');
                if(site.HomeId is <1 or >4096||path.Length!=4||path[0]!="building"||path[2]!="house"||!int.TryParse(path[3],NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<0
                    ||site.Name.Length is <1 or >96||site.OwnerCharacterId!=""||site.OwnerName!=""||site.Mine||site.Destroyed||!site.VisitsAllowed
                    ||site.PriceCents!=StormChaseHousing.PriceFor(path[1])||!HousingVector(site.Entrance)||!HousingVector(site.Interior))throw new ArgumentException("Invalid canonical house site.");
                string document=JsonSerializer.Serialize(site,Json);
                using var read=Command(db,tx,"SELECT document FROM housing_sites WHERE world=$world AND home_id=$id",("$world",request.World),("$id",site.HomeId));
                string? prior=read.ExecuteScalar() as string;
                if(prior is not null&&prior!=document)throw new CareerConflictException("World house geometry changed without a new fingerprint.");
                using var insert=Command(db,tx,"INSERT OR IGNORE INTO housing_sites VALUES($world,$id,$building,$document)",("$world",request.World),("$id",site.HomeId),("$building",site.BuildingId),("$document",document));insert.ExecuteNonQuery();
                foreach(string earlier in StormChaseWorld.HousingCompatibleFingerprints)CopyCompatibleHome(db,tx,earlier,request.World,site);
            }
            tx.Commit();return true;
        }
    }
    private static bool HousingVector(NetVector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)&&MathF.Abs(p.X)<=60000&&MathF.Abs(p.Z)<=60000&&MathF.Abs(p.Y)<=20000;
    private static bool HousingRotation(NetQuaternion p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)&&float.IsFinite(p.W)&&MathF.Abs(p.X*p.X+p.Y*p.Y+p.Z*p.Z+p.W*p.W-1)<.02f;
    private static HomeSiteState? HousingHome(SqliteConnection db,SqliteTransaction? tx,string world,int homeId,string? mine=null)
    {
        using var c=Command(db,tx,"SELECT s.document,COALESCE(d.character_id,''),COALESCE(d.owner_name,''),COALESCE(d.visits,1) FROM housing_sites s LEFT JOIN housing_deeds d ON d.world=s.world AND d.home_id=s.home_id WHERE s.world=$world AND s.home_id=$home",("$world",world),("$home",homeId));
        using var r=c.ExecuteReader();if(!r.Read())return null;var h=JsonSerializer.Deserialize<HomeSiteState>(r.GetString(0),Json)!;
        return h with{OwnerCharacterId=r.GetString(1),OwnerName=r.GetString(2),Mine=r.GetString(1)==mine,VisitsAllowed=r.GetInt32(3)!=0};
    }
    private static int OwnHomeId(SqliteConnection db,SqliteTransaction? tx,string world,string character)
    {using var c=Command(db,tx,"SELECT home_id FROM housing_deeds WHERE world=$world AND character_id=$character",("$world",world),("$character",character));return c.ExecuteScalar() is long id?(int)id:0;}
    private static FurnitureState[] ReadFurniture(SqliteConnection db,SqliteTransaction? tx,string world,string? character=null)
    {
        using var c=Command(db,tx,"SELECT document FROM housing_furniture WHERE world=$world"+(character is null?"":" AND character_id=$character")+" ORDER BY id",("$world",world),("$character",character??""));using var r=c.ExecuteReader();var states=new List<FurnitureState>();
        while(r.Read())states.Add(JsonSerializer.Deserialize<FurnitureState>(r.GetString(0),Json)!);return states.ToArray();
    }
    public virtual HousingAccountDto GetHousing(string character,string world)
    {ValidateHousingWorld(world);using var db=Open();_ =ReadCareer(db,null,character);int id=OwnHomeId(db,null,world,character);return new(world,id==0?null:HousingHome(db,null,world,id,character),ReadFurniture(db,null,world,character));}
    public virtual HomeSiteState[] HouseClaims(string world)
    {
        ValidateHousingWorld(world);using var db=Open();using var c=Command(db,null,"SELECT s.document,d.character_id,d.owner_name,d.visits FROM housing_deeds d JOIN housing_sites s ON s.world=d.world AND s.home_id=d.home_id WHERE d.world=$world ORDER BY d.home_id",("$world",world));using var r=c.ExecuteReader();var states=new List<HomeSiteState>();
        while(r.Read())states.Add(JsonSerializer.Deserialize<HomeSiteState>(r.GetString(0),Json)! with{OwnerCharacterId=r.GetString(1),OwnerName=r.GetString(2),VisitsAllowed=r.GetInt32(3)!=0});return states.ToArray();
    }
    public virtual FurnitureState[] WorldFurniture(string world){ValidateHousingWorld(world);using var db=Open();return ReadFurniture(db,null,world);}
    public virtual HousingPurchaseDto BuyHome(string character,HousingBuyHomeRequest request)
    {
        ValidateHousingWorld(request.World);ValidateOperation(request.OperationId);string payload=request.World+":"+request.HomeId.ToString(CultureInfo.InvariantCulture);
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();string? prior=FindOperation(db,tx,character,request.OperationId,"housing/home",payload);var career=ReadCareer(db,tx,character);
            if(prior is not null)return JsonSerializer.Deserialize<HousingPurchaseDto>(prior,Json)! with{Duplicate=true,Career=career};
            var home=HousingHome(db,tx,request.World,request.HomeId,character)??throw new ArgumentException("Unknown enterable house.");
            string message=home.OwnerCharacterId!=""?"That house is already owned.":OwnHomeId(db,tx,request.World,character)!=0?"You already own a home in this region.":career.Cash*100<home.PriceCents?"Insufficient funds.":"Home purchased. Enter through the front door and furnish it.";
            bool purchased=message.StartsWith("Home purchased",StringComparison.Ordinal);
            if(purchased)
            {
                using var name=Command(db,tx,"SELECT name FROM characters WHERE id=$id",("$id",character));string owner=(string)name.ExecuteScalar()!;
                using var insert=Command(db,tx,"INSERT INTO housing_deeds VALUES($world,$id,$character,$name,1)",("$world",request.World),("$id",home.HomeId),("$character",character),("$name",owner));insert.ExecuteNonQuery();
                career=career with{Cash=career.Cash-home.PriceCents/100m,Version=career.Version+1};WriteCareer(db,tx,career);home=home with{OwnerCharacterId=character,OwnerName=owner,Mine=true};
            }
            var result=new HousingPurchaseDto(purchased,false,message,career,home);AddOperation(db,tx,character,request.OperationId,"housing/home",payload,result);tx.Commit();return result;
        }
    }
    public virtual HousingPurchaseDto BuyFurniture(string character,HousingBuyFurnitureRequest request)
    {
        ValidateHousingWorld(request.World);ValidateOperation(request.OperationId);var offer=StormChaseHousing.Furniture(request.OfferId)?.Offer??throw new ArgumentException("Unknown furniture offer.");string payload=request.World+":"+offer.Sku;
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();string? prior=FindOperation(db,tx,character,request.OperationId,"housing/furniture",payload);var career=ReadCareer(db,tx,character);
            if(prior is not null)return JsonSerializer.Deserialize<HousingPurchaseDto>(prior,Json)! with{Duplicate=true,Career=career};
            int homeId=OwnHomeId(db,tx,request.World,character);var home=homeId==0?null:HousingHome(db,tx,request.World,homeId,character);var owned=ReadFurniture(db,tx,request.World,character);
            string message=home is null?"Buy a home before purchasing furniture.":owned.Length>=StormChaseHousing.MaximumFurniturePerHome?"Your home has reached its 24 item limit.":career.Cash*100<offer.PriceCents?"Insufficient funds.":"Furniture purchased. Enter your home and select an open slot.";bool purchased=message.StartsWith("Furniture purchased",StringComparison.Ordinal);FurnitureState? furniture=null;
            if(purchased)
            {
                using var insert=Command(db,tx,"INSERT INTO housing_furniture(world,home_id,character_id,sku,document) VALUES($world,$home,$character,$sku,'{}');SELECT last_insert_rowid();",("$world",request.World),("$home",homeId),("$character",character),("$sku",offer.Sku));int id=checked((int)(long)insert.ExecuteScalar()!);
                furniture=new(id,homeId,character,offer.Sku,home!.Interior,NetQuaternion.Identity,default,default,false,true,false,true);
                using var save=Command(db,tx,"UPDATE housing_furniture SET document=$document WHERE id=$id",("$id",id),("$document",JsonSerializer.Serialize(furniture,Json)));save.ExecuteNonQuery();
                career=career with{Cash=career.Cash-offer.PriceCents/100m,Version=career.Version+1};WriteCareer(db,tx,career);
            }
            var result=new HousingPurchaseDto(purchased,false,message,career,home,furniture);AddOperation(db,tx,character,request.OperationId,"housing/furniture",payload,result);tx.Commit();return result;
        }
    }
    public virtual bool SaveFurniturePose(string character,HousingPoseRequest request)
    {
        ValidateHousingWorld(request.World);var s=request.State;if(request.Revision<1||!HousingVector(s.Position)||!HousingRotation(s.Rotation)||!HousingVector(s.LinearVelocity)||!HousingVector(s.AngularVelocity))throw new ArgumentException("Invalid authoritative furniture pose.");
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var read=Command(db,tx,"SELECT world,home_id,character_id,sku,revision FROM housing_furniture WHERE id=$id",("$id",s.FurnitureId));
            using(var r=read.ExecuteReader()){if(!r.Read())throw new KeyNotFoundException();if(r.GetString(0)!=request.World||r.GetInt32(1)!=s.HomeId||r.GetString(2)!=character||s.OwnerCharacterId!=character||r.GetString(3)!=s.Sku)throw new CareerConflictException("Furniture does not belong to this character and home.");if(r.GetInt64(4)>=request.Revision)return false;}
            using var save=Command(db,tx,"UPDATE housing_furniture SET document=$document,revision=$revision WHERE id=$id",("$document",JsonSerializer.Serialize(s with{CollisionEnabled=false},Json)),("$revision",request.Revision),("$id",s.FurnitureId));save.ExecuteNonQuery();tx.Commit();return true;
        }
    }
    public virtual HousingPurchaseDto SetHomeVisits(string character,HousingVisitSettingsRequest request)
    {
        ValidateHousingWorld(request.World);ValidateOperation(request.OperationId);string payload=request.World+":"+request.Allowed;
        lock(mutationLock)
        {
            using var db=Open();using var tx=db.BeginTransaction();var career=ReadCareer(db,tx,character);string? prior=FindOperation(db,tx,character,request.OperationId,"housing/visits",payload);if(prior is not null)return JsonSerializer.Deserialize<HousingPurchaseDto>(prior,Json)! with{Duplicate=true,Career=career};
            int id=OwnHomeId(db,tx,request.World,character);if(id==0)throw new CareerConflictException("You do not own a home.");using var change=Command(db,tx,"UPDATE housing_deeds SET visits=$allowed WHERE world=$world AND character_id=$character",("$world",request.World),("$character",character),("$allowed",request.Allowed?1:0));change.ExecuteNonQuery();
            var result=new HousingPurchaseDto(true,false,request.Allowed?"Home visits enabled.":"Home visits disabled.",career,HousingHome(db,tx,request.World,id,character));AddOperation(db,tx,character,request.OperationId,"housing/visits",payload,result);tx.Commit();return result;
        }
    }
}
