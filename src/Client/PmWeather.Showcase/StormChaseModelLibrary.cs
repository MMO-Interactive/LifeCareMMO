using System.Numerics;
using System.Text.Json;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal sealed record StormChaseModelPart(string Name,AssetHandle Mesh,AssetHandle Material,
    Vector3f LocalPositionMeters,Vector3f LocalRotationRadians,Vector3f SizeMeters,
    bool Destructible,string Category,float ReleaseWindMph);
internal sealed record StormChaseModelDefinition(string Id,string DisplayName,Vector3f FootprintMeters,
    IReadOnlyList<StormChaseModelPart> Parts);

/// <summary>Blender-authored original rigid modules, preserving exact native mesh/collision transforms.</summary>
internal sealed partial class StormChaseModelLibrary
{
    private readonly Dictionary<string,StormChaseModelDefinition> models=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AssetHandle,StormChaseModelPart> meshParts=[];
    public static IReadOnlyList<string> ModelIds {get;}=["house","store","cityhall","warehouse","gas-station","barn","silo","pickup","suv","interceptor","probe","chase-truck",
        "police-cruiser","fire-engine","ambulance","fire-station","police-station"];
    /// <summary>The ten Blender field-probe types (tools/storm-chase-models/build_probes.py).</summary>
    public static IReadOnlyList<string> ProbeModelIds {get;}=["probe-tripod","probe-turtle","probe-hardened","probe-sled","probe-video",
        "probe-sonde","probe-impact","probe-hailpad","probe-mesonet","probe-lidar"];
    /// <summary>Static street furniture (tools/storm-chase-models/build_street_furniture.py); merged per area by the client.</summary>
    public static IReadOnlyList<string> StreetModelIds {get;}=["stop-sign","street-name-post","street-light","power-pole","power-pole-transformer"];
    public IReadOnlyCollection<StormChaseModelDefinition> Models=>models.Values;
    public int PartCount=>models.Values.Sum(m=>m.Parts.Count);
    public int VertexCount {get;private set;}
    public int TriangleCount {get;private set;}
    public string ContentDirectory {get;}

    public StormChaseModelLibrary(MutableRenderAssetCatalog catalog,string? contentRoot=null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ContentDirectory=LocateContent(contentRoot);
        RegisterMaterials(catalog);
        foreach(string file in new[]{"storm-chase-models.json","storm-chase-probes.json","storm-chase-truck.json","storm-chase-emergency.json","storm-chase-firestation.json","storm-chase-policestation.json","storm-chase-street.json"})
        {
        using var document=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContentDirectory,file)));
        if(document.RootElement.GetProperty("schema").GetInt32()!=1)
            throw new InvalidDataException("Unsupported Blender chase model schema: "+file);
        foreach(var model in document.RootElement.GetProperty("models").EnumerateArray())
        {
            string id=model.GetProperty("id").GetString()!;
            var parts=new List<StormChaseModelPart>();
            foreach(var node in model.GetProperty("parts").EnumerateArray())
            {
                string name=node.GetProperty("name").GetString()!;
                var meshHandle=new AssetHandle("mesh","chase/model/"+id+"/"+name);
                var materialHandle=new AssetHandle("material","chase/model/"+node.GetProperty("material").GetString());
                var vertices=new List<RenderMeshVertex>();
                foreach(var vertex in node.GetProperty("vertices").EnumerateArray())
                {
                    var p=new Vector3f(vertex[0].GetSingle(),vertex[1].GetSingle(),vertex[2].GetSingle());
                    var n=new Vector3(vertex[3].GetSingle(),vertex[4].GetSingle(),vertex[5].GetSingle());
                    if(!Finite(p)||!float.IsFinite(n.LengthSquared())||n.LengthSquared()<.5f)
                        throw new InvalidDataException("Invalid exported Blender vertex: "+meshHandle);
                    var tangent=Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y)>.95f?Vector3.UnitZ:Vector3.UnitY,n));
                    vertices.Add(new(p,new(1,1,1,1),vertex[6].GetSingle(),vertex[7].GetSingle(),
                        new(n.X,n.Y,n.Z),new(tangent.X,tangent.Y,tangent.Z)));
                }
                var indices=node.GetProperty("indices").EnumerateArray().Select(i=>i.GetUInt32()).ToArray();
                if(indices.Length%3!=0||indices.Any(i=>i>=vertices.Count))
                    throw new InvalidDataException("Invalid exported Blender index range: "+meshHandle);
                var data=new RenderMeshData {Handle=meshHandle,Vertices=vertices,WideIndices=indices};
                catalog.UpsertMesh(data);
                var part=new StormChaseModelPart(name,meshHandle,materialHandle,
                    Vector(node.GetProperty("localPositionMeters")),Vector(node.GetProperty("localRotationRadians")),
                    Vector(node.GetProperty("sizeMeters")),node.GetProperty("destructible").GetBoolean(),
                    node.GetProperty("category").GetString()!,node.GetProperty("releaseWindMph").GetSingle());
                if(!Finite(part.SizeMeters)||part.SizeMeters.X<=0||part.SizeMeters.Y<=0||part.SizeMeters.Z<=0)
                    throw new InvalidDataException("Invalid authored part size: "+meshHandle);
                parts.Add(part);meshParts.Add(meshHandle,part);
                VertexCount+=vertices.Count;TriangleCount+=indices.Length/3;
            }
            models.Add(id,new(id,model.GetProperty("displayName").GetString()!,
                Vector(model.GetProperty("footprintMeters")),parts.ToArray()));
        }
        }
        foreach(string id in ModelIds.Concat(ProbeModelIds).Concat(StreetModelIds))
            if(!models.ContainsKey(id))throw new InvalidDataException("Required Blender chase model absent: "+id);
        RegisterAccessibleModules(catalog);
    }

    public StormChaseModelDefinition GetModel(string id)=>models.TryGetValue(id,out var model)?model:
        throw new ArgumentException("Unknown chase model: "+id,nameof(id));
    public IReadOnlyList<StormChaseModelPart> GetParts(string id)=>GetModel(id).Parts;
    public bool TryGetPart(RenderItem item,out StormChaseModelPart part)=>meshParts.TryGetValue(item.Mesh,out part!);

    /// <param name="yawRadians">World-space yaw in radians; RenderItem receives converted degrees internally.</param>
    public IReadOnlyList<RenderItem> CreateItems(string id,Vector3f worldBase,float yawRadians,string idPrefix,float scale=1)
    {
        if(!Finite(worldBase)||!float.IsFinite(yawRadians)||!float.IsFinite(scale)||scale<=0)
            throw new ArgumentOutOfRangeException(nameof(scale),"Model transforms must be finite and scale positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(idPrefix);
        float c=MathF.Cos(yawRadians),s=MathF.Sin(yawRadians);
        var yaw=Matrix4x4.CreateRotationY(yawRadians);
        return GetModel(id).Parts.Select(part=>
        {
            var p=part.LocalPositionMeters*scale;
            var position=worldBase+new Vector3f(p.X*c+p.Z*s,p.Y,-p.X*s+p.Z*c);
            var r=part.LocalRotationRadians;
            var rotation=Matrix4x4.CreateRotationX(r.X)*Matrix4x4.CreateRotationY(r.Y)*Matrix4x4.CreateRotationZ(r.Z)*yaw;
            var euler=EulerDegrees(rotation);
            return new RenderItem(part.Material.Id,part.Mesh,part.Material,position,part.SizeMeters*scale,
                yawRadians*180/MathF.PI,true,WritesMotionVectors:true,RotationEulerDegrees:euler,
                MotionVectorId:idPrefix+"/"+part.Name,AllowStaticInstancing:false);
        }).ToArray();
    }

    private void RegisterMaterials(MutableRenderAssetCatalog catalog)
    {
        using var image=Image.Load<Rgba32>(Path.Combine(ContentDirectory,"weathered-brick.png"));
        byte[] pixels=new byte[image.Width*image.Height*4];image.CopyPixelDataTo(pixels);
        var mip=TextureMipChainGenerator.Generate((uint)image.Width,(uint)image.Height,1,pixels,TextureMipSemantic.Color,true);
        var brick=new AssetHandle("texture","chase/model/weathered-brick");
        catalog.UpsertTexture(new(){Handle=brick,Width=(uint)image.Width,Height=(uint)image.Height,
            MipLevels=mip.MipLevels,ColorSpace="srgb",PixelDataRgba8=mip.PixelDataRgba8});
        var colors=new Dictionary<string,ColorRgba>
        {
            ["brick"]=new(1,1,1,1),["siding"]=new(.68f,.67f,.52f,1),["roof"]=new(.17f,.21f,.25f,1),
            ["trim"]=new(.88f,.86f,.75f,1),["glass"]=new(.055f,.16f,.22f,1),["wood"]=new(.31f,.19f,.09f,1),
            ["metal"]=new(.39f,.44f,.47f,1),["pump"]=new(.74f,.12f,.06f,1),["rubber"]=new(.025f,.028f,.032f,1),
            ["paint"]=new(.17f,.29f,.33f,1),["paint-suv"]=new(.37f,.42f,.20f,1),["armor"]=new(.31f,.33f,.29f,1),
            ["lamp"]=new(.9f,.78f,.43f,1),["barn"]=new(.48f,.09f,.065f,1),["concrete"]=new(.44f,.46f,.43f,1),
            ["orange"]=new(.95f,.32f,.04f,1),
            // Street furniture (build_street_furniture.py).
            ["sign-red"]=new(.62f,.04f,.04f,1),["sign-green"]=new(.03f,.30f,.15f,1),
            // Field-probe palette (build_probes.py).
            ["yellow"]=new(.93f,.70f,.06f,1),["white"]=new(.84f,.86f,.84f,1),["red"]=new(.68f,.07f,.05f,1),
            ["solar"]=new(.04f,.07f,.17f,1),["chrome"]=new(.74f,.75f,.77f,1),
            // Emergency vehicles (build_emergency_vehicles.py); beacons are emissive.
            ["black"]=new(.02f,.022f,.026f,1),["police-blue"]=new(.05f,.13f,.42f,1),
            ["beacon-red"]=new(1f,.06f,.04f,1),["beacon-blue"]=new(.08f,.25f,1f,1)
        };
        foreach(var pair in colors)
        {
            bool metal=pair.Key is "metal" or "armor" or "roof" or "chrome";
            catalog.UpsertMaterial(new(){Handle=new("material","chase/model/"+pair.Key),
                ShaderProgramId=BuiltInShaderLibrary.PbrLit.Id,LightingProfile=RenderMaterialLightingProfile.MetallicRoughness,
                Tint=pair.Value,AlbedoTexture=pair.Key=="brick"?brick:null,
                RoughnessStrength=pair.Key is "glass" or "solar" or "chrome"?.16f:pair.Key.StartsWith("paint",StringComparison.Ordinal)?.32f:metal?.5f:.85f,
                MetalnessStrength=metal?.65f:pair.Key.StartsWith("paint",StringComparison.Ordinal)?.3f:0,
                EmissiveColor=pair.Key is "lamp" or "beacon-red" or "beacon-blue"?pair.Value:ColorRgba.Black,
                EmissiveIntensity=pair.Key=="lamp"?.5f:pair.Key.StartsWith("beacon",StringComparison.Ordinal)?2.2f:0});
        }
    }
    private static string LocateContent(string? root)
    {
        if(root is not null)
        {
            foreach(string path in new[]{root,Path.Combine(root,"Models"),Path.Combine(root,"Assets","StormChase","Models")})
                if(File.Exists(Path.Combine(path,"storm-chase-models.json")))return Path.GetFullPath(path);
            throw new DirectoryNotFoundException("Blender chase model package absent under "+root);
        }
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            string path=Path.Combine(directory.FullName,"Assets","StormChase","Models");
            if(File.Exists(Path.Combine(path,"storm-chase-models.json")))return path;
        }
        throw new DirectoryNotFoundException("The Blender chase model package was not copied to Assets/StormChase/Models.");
    }
    private static Vector3f Vector(JsonElement array)=>new(array[0].GetSingle(),array[1].GetSingle(),array[2].GetSingle());
    private static bool Finite(Vector3f v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    private static Vector3f EulerDegrees(Matrix4x4 m)
    {
        float sinY=Math.Clamp(-m.M13,-1,1),y=MathF.Asin(sinY),x,z;
        if(MathF.Abs(MathF.Cos(y))>.00001f){x=MathF.Atan2(m.M23,m.M33);z=MathF.Atan2(m.M12,m.M11);}
        else{x=MathF.Atan2(MathF.Sign(sinY)*m.M21,m.M22);z=0;}
        return new(x*180/MathF.PI,y*180/MathF.PI,z*180/MathF.PI);
    }
}
