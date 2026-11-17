using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using System.Text.Json;

namespace StormChase.Protocol;

/// <summary>A bounded, versioned envelope inside the encrypted transport. Never deserialize arbitrary type names.</summary>
public static class ProtocolCodec
{
    public const ushort Version = 3;
    public const uint Magic = 0x53434853;
    public const int HeaderBytes = 20;
    public const int MaxPayloadBytes = 256 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32, IncludeFields = true };
    /// <summary>Top bit of the sequence field: the sender asked for reliable delivery. Relays use it to forward a frame unchanged on the same channel.</summary>
    public const ulong ReliableFlag = 1UL << 63;

    /// <summary>Checks the envelope only (magic, version, kind, length) so a relay can forward a frame without decoding its payload.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> bytes, out MessageKind kind, out bool reliable)
    {
        kind = default; reliable = false;
        if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + MaxPayloadBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version) return false;
        kind = (MessageKind)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != bytes.Length - HeaderBytes || !Enum.IsDefined(kind)) return false;
        reliable = (BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) & ReliableFlag) != 0;
        return true;
    }

    public static byte[] Encode<T>(MessageKind kind, ulong sequence, T value)
    {
        if (PayloadType(kind) != typeof(T)) throw new ArgumentException("Payload type does not match message kind.", nameof(value));
        if (value is null || !Validate(value)) throw new ArgumentException("Payload contains invalid game state.", nameof(value));
        var payload = value is DebrisSnapshot debris ? DebrisBinaryCodec.Encode(debris) : JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(value), "Message exceeds the protocol payload limit.");
        if (kind is MessageKind.WorldSnapshot or MessageKind.JoinAccepted or MessageKind.AtmosphereSnapshot) payload = CompressSnapshot(payload);
        if (payload.Length > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(value), "Encoded message exceeds the protocol payload limit.");
        var data = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), payload.Length);
        payload.CopyTo(data.AsSpan(HeaderBytes));
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ProtocolMessage? message)
    {
        message = null;
        if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + MaxPayloadBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version) return false;
        var kind = (MessageKind)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        if (size != bytes.Length - HeaderBytes || !Enum.IsDefined(kind)) return false;
        try
        {
            object? value;
            if(kind==MessageKind.DebrisSnapshot) value=DebrisBinaryCodec.Decode(bytes[HeaderBytes..]);
            else if(kind is MessageKind.WorldSnapshot or MessageKind.JoinAccepted or MessageKind.AtmosphereSnapshot)
            {
                var json=DecompressSnapshot(bytes[HeaderBytes..]);
                if(json is null)return false;
                value=JsonSerializer.Deserialize(json,PayloadType(kind),JsonOptions);
            }
            else value=JsonSerializer.Deserialize(bytes[HeaderBytes..],PayloadType(kind),JsonOptions);
            if (value is null || !Validate(value)) return false;
            message = new(kind, BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), value);
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    // Lossless compression keeps complete render parameters and home/mission data
    // from turning repeated unreliable snapshots into oversized reliable messages.
    // The advertised raw size is checked before allocation, and trailing/truncated
    // streams are rejected. Both wire and decompressed data share the same bound.
    private static byte[] CompressSnapshot(byte[] json)
    {
        var buffer=new byte[4+BrotliEncoder.GetMaxCompressedLength(json.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer,json.Length);
        if(!BrotliEncoder.TryCompress(json,buffer.AsSpan(4),out int written,quality:1,window:22))throw new InvalidOperationException("Snapshot compression failed.");
        return buffer.AsSpan(0,4+written).ToArray();
    }
    private static byte[]? DecompressSnapshot(ReadOnlySpan<byte> compressed)
    {
        if(compressed.Length<=4)return null;
        int size=BinaryPrimitives.ReadInt32LittleEndian(compressed);
        if(size is <=0 or >MaxPayloadBytes)return null;
        var json=new byte[size];using var decoder=new BrotliDecoder();
        var status=decoder.Decompress(compressed[4..],json,out int consumed,out int written);
        return status==OperationStatus.Done&&consumed==compressed.Length-4&&written==size?json:null;
    }

    private static Type PayloadType(MessageKind kind) => kind switch
    {
        MessageKind.JoinRequest => typeof(JoinRequest), MessageKind.JoinAccepted => typeof(JoinAccepted), MessageKind.JoinRejected => typeof(JoinRejected),
        MessageKind.ClientInput => typeof(ClientInput), MessageKind.WorldSnapshot => typeof(WorldSnapshot), MessageKind.GameplayAction => typeof(GameplayAction),
        MessageKind.ActionResult => typeof(ActionResult), MessageKind.Ping or MessageKind.Pong => typeof(Ping), MessageKind.DebrisEvent => typeof(DebrisEvent), MessageKind.DebrisSnapshot => typeof(DebrisSnapshot),
        MessageKind.AtmosphereSnapshot => typeof(AtmosphereSnapshot), _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool Text(string? value, int limit) => value is not null && value.Length <= limit;
    private static bool Vector(NetVector3 value) => value.IsFinite && Math.Abs(value.X) <= 1_000_000 && Math.Abs(value.Y) <= 1_000_000 && Math.Abs(value.Z) <= 1_000_000;
    private static bool Body(DebrisBodyState? body) => body is not null && body.BodyId > 0 && Vector(body.Position) && Vector(body.LinearVelocity) && Vector(body.AngularVelocity) && body.Rotation.IsFinite && Text(body.StablePartId, 128) && (!body.Collected || (body.Archived && body.Sleeping && !body.CollisionEnabled));
    private static bool Debris(DebrisSnapshot? value) => value is not null && value.ServerTick >= 0 && value.BaselineRevision >= 0 && value.ChunkCount is >= 1 and <= 1024 && value.ChunkIndex >= 0 && value.ChunkIndex < value.ChunkCount && value.Bodies is { Length: <= 4096 } && value.Bodies.All(Body);
    private static bool Career(CareerProgress? value) => value is not null && value.CashCents >= 0 && value.Reputation >= 0 && value.Probes >= 0 && value.Photos >= 0 && value.CompletedChases >= 0 && value.Popularity >= 0 && Text(value.Rank, 64);
    private static bool Snapshot(WorldSnapshot? value) => value is not null && value.ServerTick >= 0 && double.IsFinite(value.SimulationSeconds) && value.SimulationSeconds>=0 &&
        value.Players is { Length: <= 256 } && value.Players.All(x => x is not null && Text(x.Name, 64) && Vector(x.Position) && Vector(x.Velocity) && float.IsFinite(x.Yaw) && float.IsFinite(x.Pitch) && x.ClientTick >= 0 && Enum.IsDefined(x.Mode)) &&
        value.Vehicles is { Length: <= 128 } && value.Vehicles.All(x => x is not null && Vector(x.Position) && Vector(x.Velocity) && Vector(x.AngularVelocity) && x.Rotation.IsFinite && float.IsFinite(x.Yaw) && float.IsFinite(x.Health) && float.IsFinite(x.FuelLiters)) &&
        value.Storms is { Length: <= 32 } && value.Storms.All(x => x is not null && Text(x.Stage, 48) && Vector(x.Position) && float.IsFinite(x.AgeSeconds) && float.IsFinite(x.Strength) && float.IsFinite(x.Radius) && float.IsFinite(x.Heading) && float.IsFinite(x.Speed) &&
            (x.Tornado is null || (Text(x.Tornado.Stage, 48) && Vector(x.Tornado.Position) && float.IsFinite(x.Tornado.Intensity) && float.IsFinite(x.Tornado.WindMph) && float.IsFinite(x.Tornado.Radius) && float.IsFinite(x.Tornado.FunnelReach) && float.IsFinite(x.Tornado.AgeSeconds)))) &&
        value.Destruction is { Length: <= 4096 } && value.Destruction.All(x => x is not null && Vector(x.Position) && Vector(x.Velocity)) &&
        value.Probes is { Length: <= 4096 } && value.Probes.All(x => x is not null && Vector(x.Position) && float.IsFinite(x.WindMph) && float.IsFinite(x.PressureMb)) && (value.Debris is null || Debris(value.Debris)) &&
        (value.Mission is null || Mission(value.Mission)) && (value.Career is null || Career(value.Career)) &&
        (value.Activities is null || (value.Activities.Length <= 8 && value.Activities.All(Activity))) && (value.Housing is null||Housing(value.Housing)) && (value.Atmosphere is null || Atmosphere(value.Atmosphere));
    private static bool Diagnostics(AtmosphereDiagnostics? d)=>d is not null && Vector(d.StormMotion) &&
        new[]{d.CapeJkg,d.CinJkg,d.Cape03KmJkg,d.LclMeters,d.LfcMeters,d.EquilibriumLevelMeters,
            d.Shear01KmMetersPerSecond,d.Shear06KmMetersPerSecond,d.Srh01KmM2S2,d.Srh03KmM2S2,d.Stp,d.VtpGame,
            d.SurfaceTemperatureCelsius,d.DewPointCelsius,d.LowLevelLapseRateCPerKm,d.InitiationPotential01,d.TornadoPotential01}.All(float.IsFinite)
        && d.CapeJkg is >=0 and <=20000 && d.Cape03KmJkg is >=0 and <=20000 && d.CinJkg is >=-15000 and <=0
        && d.LclMeters is >=0 and <=20000 && d.LfcMeters is >=0 and <=20000 && d.EquilibriumLevelMeters is >=0 and <=20000
        && d.Shear01KmMetersPerSecond is >=0 and <=400 && d.Shear06KmMetersPerSecond is >=0 and <=400
        && MathF.Abs(d.Srh01KmM2S2)<=10000 && MathF.Abs(d.Srh03KmM2S2)<=10000
        && MathF.Abs(d.StormMotion.X)<=300 && MathF.Abs(d.StormMotion.Y)<=300 && MathF.Abs(d.StormMotion.Z)<=300
        && d.SurfaceTemperatureCelsius is >=-150 and <=70 && d.DewPointCelsius is >=-150 and <=70
        && d.DewPointCelsius<=d.SurfaceTemperatureCelsius+.1f && d.LowLevelLapseRateCPerKm is >=-30 and <=30
        && d.InitiationPotential01 is >=0 and <=1 && d.TornadoPotential01 is >=0 and <=1
        && d.Stp is >=0 and <=1000 && d.VtpGame is >=0 and <=3000;
    private static bool Atmosphere(AtmosphereSnapshot a)=>a.ServerTick>=0&&double.IsFinite(a.CampaignSeconds)&&a.CampaignSeconds>=0
        &&a.ModelVersion==1&&float.IsFinite(a.CampaignStartHour)&&a.CampaignStartHour is >=0 and <24
        &&double.IsFinite(a.CampaignDayLengthSeconds)&&a.CampaignDayLengthSeconds is >=60 and <=86400
        &&a.DayNumber is >=1 and <=100000&&float.IsFinite(a.Hour)&&a.Hour is >=0 and <24&&Vector(a.SamplePosition)&&Diagnostics(a.Diagnostics)
        &&Sounding(a)
        &&!a.ThreeDayForecast.IsDefault&&a.ThreeDayForecast.Length<=3&&a.ThreeDayForecast.All(f=>f is not null&&f.DayNumber>0&&Text(f.Name,64)
            &&float.IsFinite(f.Risk)&&f.Risk is >=0 and <=1&&Diagnostics(f.Peak)&&Diagnostics(f.Overnight))
        &&a.ThreeDayForecast.Select((f,index)=>f.DayNumber==a.DayNumber+index).All(x=>x);
    private static bool Sounding(AtmosphereSnapshot a)
    {
        if(a.Sounding.IsDefault||a.Sounding.Length is <3 or >128)return false;
        for(int i=0;i<a.Sounding.Length;i++)
        {
            var level=a.Sounding[i];
            if(level is null||!new[]{level.HeightMeters,level.PressureHpa,level.TemperatureCelsius,level.DewPointCelsius,
                level.WindEastMetersPerSecond,level.WindNorthMetersPerSecond,level.ParcelTemperatureCelsius}.All(float.IsFinite)
                ||level.HeightMeters is <0 or >20000||level.PressureHpa is <=0 or >1200
                ||level.TemperatureCelsius is <-150 or >70||level.DewPointCelsius is <-150 or >70
                ||level.ParcelTemperatureCelsius is <-150 or >100||level.DewPointCelsius>level.TemperatureCelsius+.1f
                ||MathF.Abs(level.WindEastMetersPerSecond)>200||MathF.Abs(level.WindNorthMetersPerSecond)>200
                ||i>0&&(level.HeightMeters<=a.Sounding[i-1].HeightMeters||level.PressureHpa>=a.Sounding[i-1].PressureHpa))return false;
        }
        return a.Sounding[0].HeightMeters==0&&a.Sounding[^1].HeightMeters>=6000;
    }
    private static bool Home(HomeSiteState? home)=>home is not null&&home.HomeId>0&&Text(home.BuildingId,128)&&Text(home.Name,96)&&Vector(home.Entrance)&&Vector(home.Interior)&&home.PriceCents>=0&&Text(home.OwnerCharacterId,40)&&Text(home.OwnerName,64);
    private static bool Housing(HousingState housing)=>housing.MaxFurniture is >=1 and <=24&&Text(housing.Message,256)
        &&(housing.Home is null||Home(housing.Home))&&housing.Homes is {Length:<=24}&&housing.Homes.All(Home)
        &&housing.Offers is {Length:<=12}&&housing.Offers.All(o=>o is not null&&o.Id>0&&Text(o.Sku,32)&&Text(o.Name,64)&&o.PriceCents>=0&&Text(o.Description,160))
        &&housing.Furniture is {Length:<=96}&&housing.Furniture.All(f=>f is not null&&f.FurnitureId>0&&f.HomeId>0&&Text(f.OwnerCharacterId,40)&&Text(f.Sku,32)&&Vector(f.Position)&&Vector(f.LinearVelocity)&&Vector(f.AngularVelocity)&&f.Rotation.IsFinite&&f.Rotation.X*f.Rotation.X+f.Rotation.Y*f.Rotation.Y+f.Rotation.Z*f.Rotation.Z+f.Rotation.W*f.Rotation.W>.1f)
        &&housing.Visitors is {Length:<=64}&&housing.Visitors.All(v=>v is not null&&v.PlayerId>0&&Text(v.Name,64)&&v.HomeId>0)&&housing.VisitTargetHomeId>=0&&Vector(housing.VisitTarget);
    private static bool Activity(ActivityState? value) => value is not null && value.Id >= 0 && Text(value.Title, 80) && Text(value.Kind, 48) && Vector(value.Position) && value.StepCount is >= 1 and <= 64 && value.RewardCashCents >= 0 && value.RewardReputation >= 0 && Text(value.Description, 256);
    private static bool Mission(MissionState? value) => value is not null && value.Id >= 0 && Text(value.Title, 80) && Text(value.Kind, 48) && value.StepCount is >= 1 and <= 64 && value.Step >= 0 && value.Step <= value.StepCount && Text(value.Objective, 256) && Vector(value.TargetPosition) && float.IsFinite(value.WorkSeconds) && value.WorkSeconds >= 0 && float.IsFinite(value.RequiredWorkSeconds) && value.RequiredWorkSeconds >= 0;
    private static bool Validate(object value) => value switch
    {
        JoinRequest x => Text(x.Ticket, 512) && x.Ticket.Length > 0 && Text(x.WorldContentFingerprint, 128),
        JoinAccepted x => x.PlayerId > 0 && Career(x.Career) && Snapshot(x.Snapshot),
        JoinRejected x => Text(x.Reason, 256),
        ClientInput x => x.ClientTick >= 0 && Vector(x.Position) && float.IsFinite(x.Yaw) && float.IsFinite(x.Pitch) && Enum.IsDefined(x.Mode) &&
            float.IsFinite(x.Forward) && Math.Abs(x.Forward) <= 1 && float.IsFinite(x.Strafe) && Math.Abs(x.Strafe) <= 1 && float.IsFinite(x.Turn) && Math.Abs(x.Turn) <= 1,
        WorldSnapshot x => Snapshot(x),
        AtmosphereSnapshot x => Atmosphere(x),
        GameplayAction x => x.RequestId >= 0 && Enum.IsDefined(x.Kind) && Vector(x.Position)&&x.Slot is >=-1 and <=24&&float.IsFinite(x.PlacementYaw),
        ActionResult x => x.RequestId >= 0 && Text(x.Message, 256) && (x.Career is null || Career(x.Career)),
        DebrisEvent x => x.EventId >= 0 && x.BaselineRevision >= 0 && Enum.IsDefined(x.Kind) && Body(x.Body) && (x.Kind != DebrisEventKind.Removed || x.Body.Collected),
        DebrisSnapshot x => Debris(x), Ping => true, _ => false
    };
}
public sealed record ProtocolMessage(MessageKind Kind, ulong Sequence, object Payload)
{
    public T Get<T>() => Payload is T value ? value : throw new InvalidOperationException($"Message {Kind} is not {typeof(T).Name}.");
}
