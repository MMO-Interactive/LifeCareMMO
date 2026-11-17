using Engine.Paths;
using Engine.Paths.Terrain;
using Engine.Rendering.Assets;
using Engine.Rendering.Scene;
using Engine.Rendering.Terrain;
using Engine.Splines;
using Engine.Terrain;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>A seeded prairie built through native terrain pages, splat arrays and projected spline roads.</summary>
internal sealed partial class StormChaseWorld
{
    public const float LakeCenterX = 1100, LakeCenterZ = 400, LakeRadiusX = 600, LakeRadiusZ = 340, LakeWaterLevel = 3;
    private const int Cells = 64;
    private readonly MutableRenderAssetCatalog catalog;
    private readonly List<Page> pages = [];
    private readonly List<Placed> scenery = [];
    private readonly List<RenderItem> destructible = [];
    private readonly List<RenderItem> staticCollision = [];
    private readonly List<RenderItem> roads = [];
    // Vegetation is presentation-only: the client attaches it after construction
    // (StormChaseWorldVegetation.cs); the authoritative server never builds it.
#pragma warning disable CS0649 // Assigned only by the client-side vegetation partial.
    private int vegetationTreeCount, vegetationPatchCount;
#pragma warning restore CS0649
    private readonly List<TerrainPathPage> projectionPages = [];
    private int objectNumber;

    public IReadOnlyList<RenderItem> DestructibleItems => destructible;
    public IReadOnlyList<RenderItem> StaticCollisionItems => staticCollision;
    public int TerrainPageCount => pages.Count;
    public int TreeCount => vegetationTreeCount;
    public int GrassClusterCount => vegetationPatchCount;
    public int RoadCount => roads.Count;

    public StormChaseWorld(MutableRenderAssetCatalog catalog)
    {
        this.catalog = catalog;
        RegisterAssets();
        Models = new(catalog);
        BuildTerrain();
        BuildRoads();
        BuildFarmsAndTown();
        BuildSettlements();
        BuildPowerLines();
        BuildStreetFurniture();   // client presentation only (StormChaseWorldStreets.cs); not part of the fingerprint
        BuildRenderSpatialIndex();
        ContentFingerprint=ComputeContentFingerprint();
    }

    /// <summary>Continuous world-space ground elevation used by the chase camera and storm anchoring.</summary>
    public float HeightAt(float x, float z)
    {
        float h = 17 + 11 * MathF.Sin(x * .0018f + z * .0011f)
            + 8 * MathF.Sin(z * .0033f - x * .0013f)
            + 3 * MathF.Sin(x * .008f) * MathF.Sin(z * .005f);
        float distance = MathF.Sqrt(x*x + (z-1000)*(z-1000));
        h = Lerp(9 + 2 * MathF.Sin(z * .003f) + 1.2f*MathF.Sin(x*.004f), h, Smooth(700, 1800, distance));
        // Larger hills frame the prairie without hiding the tornado's ground contact.
        h += 95 * MathF.Exp(-Square((x+2600)/1000)) * MathF.Exp(-Square((z-2100)/1600));
        h += 70 * MathF.Exp(-Square((x-3300)/1200)) * MathF.Exp(-Square((z-3100)/1200));
        float r = LakeRadius(x,z);
        if (r < 1.25f)
        {
            float basin = -9 + 14 * r*r*r;
            h = Lerp(basin, h, Smooth(.96f,1.25f,r));
        }
        // Dry creek incision leads toward the lake, giving the banks visible relief.
        float creekZ = 460 + 65 * MathF.Sin((x-1650)*.003f);
        if (x > 1660 && x < 3500)
            h -= 9 * MathF.Exp(-Square((z-creekZ)/34)) * Smooth(1660,1800,x) * (1-Smooth(3200,3500,x));
        if(x>1650&&x<6400)
        {
            float bank=MathF.Abs(z-RiverZ(x));
            h=Lerp(-6+9*Square(bank/50),h,Smooth(50,105,bank));
        }
        // Gulf coast across the south-east corner: coastal plain, dunes, beach and shelving sea floor.
        h=StormChaseCoast.ShapeHeight(x,z,h);
        foreach(var pad in SettlementPads)
            h=Lerp(pad.Height,h,Smooth(pad.Radius*.75f,pad.Radius*1.25f,Distance(x,z,pad.X,pad.Z)));
        return h;
    }

    /// <param name="releasedPartIds">Destructible parts currently released (local physics or server debris);
    /// their buildings render part-by-part instead of as a merged distant mesh.</param>
    public RenderScene CreateScene(CameraState camera, float seconds, RenderWeatherState? weather, IEnumerable<string>? releasedPartIds = null)
    {
        var items = new List<RenderItem>(1800);
        float x = camera.Position.X, z = camera.Position.Z;
        foreach (var page in pages)
        {
            float distance = Distance(x,z,page.X,page.Z);
            if (distance > 10000 + page.Size) continue;
            int lod = distance < 1250 ? 0 : distance < 2700 ? 1 : 2;
            items.Add(new("chase/terrain",page.Meshes[lod],null,new(page.X,0,page.Z),new(1,1,1),0,false));
        }
        foreach(var module in visibleModules.Near(x,z,6000))
            if(DistanceSquared(x,z,module.X,module.Z)<module.Range*module.Range)items.Add(module.Item);
        HashSet<string>? damaged = null;
        if (releasedPartIds is not null)
            foreach (string part in releasedPartIds)
                if (partDamage.TryGetValue(part, out var damage)) (damaged ??= new(StringComparer.Ordinal)).Add(damage.BuildingId);
        AddBuildings(items, x, z, damaged);
        AddVegetation(items, camera);
        return new(){Camera=camera,Items=items,AnimationTimeSeconds=seconds};
    }

    private void RegisterAssets()
    {
        foreach(string name in new[]{"prairie-grass","roadside-gravel","weathered-rock"})
        {
            string path = FindTexture(name+".png");
            using var image = Image.Load<Rgba32>(path);
            byte[] bytes = new byte[image.Width*image.Height*4];
            image.CopyPixelDataTo(bytes);
            var mip = TextureMipChainGenerator.Generate((uint)image.Width,(uint)image.Height,1,bytes,TextureMipSemantic.Color,true);
            catalog.UpsertTexture(new(){Handle=Texture(name),Width=(uint)image.Width,Height=(uint)image.Height,MipLevels=mip.MipLevels,ColorSpace="srgb",PixelDataRgba8=mip.PixelDataRgba8});
        }
        catalog.UpsertMesh(StormChaseTerrainGeometry.Box(Mesh("box")));
        catalog.UpsertMesh(StormChaseTerrainGeometry.Gable(Mesh("gable")));
        catalog.UpsertMesh(StormChaseTerrainGeometry.Cylinder(Mesh("cylinder")));
        catalog.UpsertMesh(StormChaseTerrainGeometry.TreeCrown(Mesh("tree-crown"),false));
        catalog.UpsertMesh(StormChaseTerrainGeometry.TreeCrown(Mesh("tree-crown-far"),true));
        catalog.UpsertMesh(StormChaseTerrainGeometry.Grass(Mesh("grass")));
        Material("barn",new(.43f,.15f,.105f,1));
        Material("house",new(.72f,.69f,.57f,1));
        Material("trim",new(.84f,.82f,.73f,1));
        Material("wood",new(.35f,.27f,.17f,1));
        Material("roof",new(.27f,.30f,.31f,1),roughness:.55f,metalness:.45f);
        Material("metal",new(.53f,.56f,.56f,1),roughness:.47f,metalness:.7f);
        Material("glass",new(.075f,.12f,.14f,1),roughness:.15f);
        Material("wire",new(.065f,.061f,.055f,1));
        Material("asphalt",new(.38f,.39f,.39f,1),"roadside-gravel");
        Material("gravel",new(.88f,.85f,.75f,1),"roadside-gravel");
        Material("line",new(.86f,.69f,.21f,1));
        Material("edge-line",new(.88f,.88f,.84f,1));
        Material("rock",new(.78f,.76f,.7f,1),"weathered-rock");
        Material("foliage",new(.46f,.57f,.27f,1),foliage:true);
        Material("grass",new(.68f,.72f,.42f,1),foliage:true);
    }

    private void Material(string name,ColorRgba tint,string? texture=null,bool foliage=false,float roughness=.88f,float metalness=0)
    {
        catalog.UpsertMaterial(new(){Handle=new("material","chase/"+name),ShaderProgramId=foliage?BuiltInShaderLibrary.FoliageLit.Id:BuiltInShaderLibrary.PbrLit.Id,
            LightingProfile=RenderMaterialLightingProfile.MetallicRoughness,Tint=tint,AlbedoTexture=texture is null?null:Texture(texture),
            RoughnessStrength=roughness,MetalnessStrength=metalness,CullMode=foliage?RenderCullMode.None:RenderCullMode.Back,
            FeatureFlags=RenderMaterialFeatureFlags.WetnessResponse|(foliage?RenderMaterialFeatureFlags.FoliageWind:0)});
    }

    private TerrainPageLayerMaps Layers()=>new(
    [
        new("terrain/grass","terrain/grass","chase/prairie-grass",null,null,1,1,new(1,1,1,1)),
        new("terrain/dirt","terrain/dirt","chase/roadside-gravel",null,null,1,1,new(.87f,.75f,.58f,1)),
        new("terrain/rock","terrain/rock","chase/weathered-rock",null,null,1,1,new(1,1,1,1)),
        new("terrain/farmland/tilled","terrain/farmland/tilled","chase/roadside-gravel",null,null,1,1,new(.52f,.40f,.27f,1)),
        new("terrain/sand","terrain/sand","chase/roadside-gravel",null,null,1,1,new(1,.98f,.86f,1))
    ],null,[],[],[]);

    private void BuildTerrain()
    {
        TerrainPageTextureArray? textures = null;
        // Preserve the detailed original chase region, then extend the native
        // map to 49.152 km. Coarse outer prairie pages stay inexpensive; pages
        // near the additional settlements subdivide into detailed 1 km tiles.
        for(int z=0;z<16;z++)for(int x=0;x<16;x++)
        {
            float cx=x*1024-7680,cz=z*1024-6680;
            bool detailed = (MathF.Abs(cx)<4096 && MathF.Abs(cz-1000)<4096)
                || Distance(cx,cz,6000,7000)<2400 || Distance(cx,cz,-6500,2500)<1500
                || Distance(cx,cz,3500,-5500)<1500;
            AddPage(cx,cz,1024,detailed?Cells:32,x,z,ref textures);
        }
        for(int z=0;z<6;z++)for(int x=0;x<6;x++)
        {
            if(x>0&&x<5&&z>0&&z<5)continue;
            AddPage(x*4096-10240,z*4096-9240,4096,32,x+100,z+100,ref textures);
        }
        for(int z=0;z<12;z++)for(int x=0;x<12;x++)
        {
            if(x>=3&&x<=8&&z>=3&&z<=8)continue;
            float cx=x*4096-22528,cz=z*4096-21528;
            bool settlement=ExtendedTownDefinitions.Any(t=>MathF.Abs(t.X-cx)<3400&&MathF.Abs(t.Z-cz)<3400);
            // Pages crossing the coastal plain, beach and near-shore shelf use 2 km pages at 32 m
            // cells so the dunes, beach and water line read; deep sea and inland prairie stay coarse.
            bool coast=!settlement&&MathF.Abs(StormChaseCoast.SeawardDistance(cx,cz)+600)<2896+2600;
            if(coast)for(int tz=0;tz<2;tz++)for(int tx=0;tx<2;tx++)
                AddPage(cx-1024+tx*2048,cz-1024+tz*2048,2048,Cells,600+x*2+tx,600+z*2+tz,ref textures);
            else if(!settlement)AddPage(cx,cz,4096,32,x+200,z+200,ref textures);
            else for(int tz=0;tz<4;tz++)for(int tx=0;tx<4;tx++)
            {
                float px=cx-1536+tx*1024,pz=cz-1536+tz*1024;
                bool detailed=ExtendedTownDefinitions.Any(t=>Distance(px,pz,t.X,t.Z)<2200);
                AddPage(px,pz,1024,detailed?Cells:32,300+x*4+tx,300+z*4+tz,ref textures);
            }
        }
        catalog.UpsertMaterial(new(){Handle=new("material","chase/terrain"),ShaderProgramId=BuiltInShaderLibrary.TerrainTextureArray.Id,
            LightingProfile=RenderMaterialLightingProfile.MetallicRoughness,AlbedoTexture=textures!.AlbedoTexture.Handle,
            NormalTexture=textures.NormalTexture.Handle,SurfaceTexture=textures.SurfaceTexture.Handle,Tint=new(1,1,1,1),
            RoughnessStrength=.94f,CullMode=RenderCullMode.Back,FeatureFlags=RenderMaterialFeatureFlags.WetnessResponse});
    }

    private void AddPage(float centerX,float centerZ,float size,int cells,int idX,int idZ,ref TerrainPageTextureArray? textures)
    {
        float[] heights=new float[(cells+1)*(cells+1)];
        string[] surfaces=new string[cells*cells];
        for(int z=0;z<=cells;z++)for(int x=0;x<=cells;x++)
            heights[z*(cells+1)+x]=HeightAt(centerX-size/2+x*size/cells,centerZ-size/2+z*size/cells);
        for(int z=0;z<cells;z++)for(int x=0;x<cells;x++)
            surfaces[z*cells+x]=SurfaceAt(centerX-size/2+(x+.5f)*size/cells,centerZ-size/2+(z+.5f)*size/cells);
        var tile=new TerrainTilePage(){Id=new("StormChase","Prairie",idX,idZ),Size=size,TilesPerSide=cells,CornerHeights=heights,SurfaceIds=surfaces,LayerMaps=Layers()};
        var page=TerrainPageMeshBuilder.CreatePageDefinition(tile,textureRepeat:1);
        if(textures is null)
        {
            textures=TerrainPageTextureArrayFactory.Create(Texture("terrain-array"),Texture("terrain-normals"),Texture("terrain-orm"),page,catalog);
            catalog.UpsertTexture(textures.AlbedoTexture);catalog.UpsertTexture(textures.NormalTexture);catalog.UpsertTexture(textures.SurfaceTexture);
        }
        var meshes=new AssetHandle[3];
        for(int lod=0;lod<3;lod++)
        {
            meshes[lod]=Mesh($"terrain/{idX}/{idZ}/{lod}");
            var definition=TerrainPageLodGenerator.CreateLod(page,lod);
            catalog.UpsertMesh(TerrainRenderMeshFactory.CreateRenderMesh(meshes[lod],definition,TerrainRenderMeshFactory.VertexColorMode.LightingOnly,textures.SurfaceTextureIndices));
        }
        pages.Add(new(centerX,centerZ,size,meshes));
        projectionPages.Add(new(page,new(centerX,0,centerZ)));
    }

    private string SurfaceAt(float x,float z)
    {
        float seaward=StormChaseCoast.SeawardDistance(x,z);
        if(seaward>-StormChaseCoast.BeachInlandMeters)return "terrain/sand";
        float r=LakeRadius(x,z);
        if(r<1.08f)return r>.92f?"terrain/sand":"terrain/dirt";
        if(x>1650&&x<6400&&MathF.Abs(z-RiverZ(x))<95)return "terrain/sand";
        if(x>1680&&x<3400&&MathF.Abs(z-(460+65*MathF.Sin((x-1650)*.003f)))<48)return "terrain/rock";
        if(MathF.Abs(x-RoadX(z))<16||MathF.Abs(z-SpurZ(x))<10&&x>-500&&x<2200)return "terrain/dirt";
        if(x>-1200&&x<-440&&z>500&&z<2100)return "terrain/farmland/tilled";
        if(x>420&&x<920&&z>1200&&z<2100)return "terrain/farmland/tilled";
        if(MathF.Abs(x)>1500&&MathF.Abs(z)>1400&&((int)MathF.Floor(x/700)+(int)MathF.Floor(z/650))%5==0
            &&Distance(x,z,6000,7000)>2200&&Distance(x,z,-6500,2500)>1300&&Distance(x,z,3500,-5500)>1400)
            return "terrain/farmland/tilled";
        float slope=MathF.Abs(HeightAt(x+8,z)-HeightAt(x-8,z))+MathF.Abs(HeightAt(x,z+8)-HeightAt(x,z-8));
        if(slope>7)return "terrain/rock";
        return "terrain/grass";
    }

    private const float RoadShoreClearance=120;
    private static float RoadX(float z)=>-310+100*MathF.Sin((z+200)*.00085f);
    private static float SpurZ(float x)=>-130-65*MathF.Sin((x+300)*.0016f);

    private void BuildRoads()
    {
        var surface=new TerrainPageSplineSurface(projectionPages);
        var highway=new TerrainPath("Prairie highway",SplineCurveMode.CatmullRom,defaultWidth:10);
        for(int z=-2800;z<=4400;z+=600)highway.AddPoint(new(RoadX(z),0,z));
        var spur=new TerrainPath("Lake road",SplineCurveMode.CatmullRom,defaultWidth:7);
        for(int x=-310;x<=2400;x+=380)spur.AddPoint(new(x,0,SpurZ(x)));
        AddRoad(highway,surface,"highway",10);
        AddRoad(spur,surface,"lake-road",7);
        var farmLane=new TerrainPath("Farm lane",SplineCurveMode.CatmullRom,defaultWidth:5);
        farmLane.AddPoint(new(RoadX(1200),0,1200));farmLane.AddPoint(new(10,0,1210));farmLane.AddPoint(new(430,0,1180));
        AddRoad(farmLane,surface,"farm-lane",5,gravel:true);
        BuildRegionalRoads(surface);
    }

    private void AddRoad(TerrainPath path,ISplineSurface surface,string name,float width,bool gravel=false)
    {
        AddRibbon(path,surface,name+"-shoulder",width+4,.15f,"gravel");
        var samples=AddRibbon(path,surface,name,width,.26f,gravel?"gravel":"asphalt");
        roadCenterlines[name]=(samples,width);
        if(gravel)gravelRoads.Add(name);
        if(gravel)return;
        // Painted centre-line dashes follow the projected path frame. They are merged
        // into world-space meshes of 64 dashes: one render item per ~1 km instead of
        // one per 4 m dash, which dominated the renderer's per-item CPU cost.
        var vertices=new List<RenderMeshVertex>(256);var indices=new List<uint>(384);
        int group=0,dashes=0;float sumX=0,sumZ=0;
        void Flush()
        {
            if(dashes==0)return;
            var handle=Mesh($"road-paint/{name}/{group++}");
            catalog.UpsertMesh(new(){Handle=handle,Vertices=vertices.ToArray(),WideIndices=indices.ToArray()});
            scenery.Add(new(sumX/dashes,sumZ/dashes,4500,new RenderItem("chase/line",handle,null,default,new(1,1,1),0,false)));
            vertices.Clear();indices.Clear();dashes=0;sumX=sumZ=0;
        }
        for(int i=2;i<samples.Count-2;i+=2)
        {
            var s=samples[i];
            if(StormChaseCoast.SeawardDistance(s.Position.X,s.Position.Z)>-RoadShoreClearance-4)continue;
            float tl=MathF.Sqrt(s.Tangent.X*s.Tangent.X+s.Tangent.Z*s.Tangent.Z);
            float tx=s.Tangent.X/tl,tz=s.Tangent.Z/tl,bx=-tz,bz=tx;
            foreach(float offset in new[]{-.16f,.16f})
            {
                float cx=s.Position.X+s.Binormal.X*offset,cz=s.Position.Z+s.Binormal.Z*offset,y=s.Position.Y+s.Binormal.Y*offset+.06f;
                uint first=(uint)vertices.Count;
                foreach(var (along,across,u,v) in new[]{(-2f,-.06f,0f,0f),(-2f,.06f,1f,0f),(2f,.06f,1f,1f),(2f,-.06f,0f,1f)})
                    vertices.Add(new(new(cx+tx*along+bx*across,y,cz+tz*along+bz*across),new(1,1,1,1),u,v,new(0,1,0),new(tx,0,tz)));
                // With b=(-tz,0,tx), cross(v1-v0, v2-v0) = 0.48*(b x t) = +Y: front face points up.
                indices.AddRange([first,first+1,first+2,first,first+2,first+3]);
                sumX+=cx;sumZ+=cz;
                if(++dashes>=64)Flush();
            }
        }
        Flush();
    }

    private IReadOnlyList<TerrainPathSample> AddRibbon(TerrainPath path,ISplineSurface surface,string name,float width,float offset,string material)
    {
        var built=TerrainPathGenerator.Generate(path,surface,new(){SampleSpacing=8,FixedWidth=width,SurfaceOffset=offset,UvTileLength=8,RequireTerrainProjection=true});
        if(built.Issues.Any(issue=>issue.Code==TerrainPathGenerator.RequiredTerrainProjectionFailedCode))throw new InvalidOperationException("Chase road projection failed: "+name);
        // Bound each actual spline mesh section so distant roads do not force
        // entire regional ribbons into visibility/residency or a single draw.
        const int verticesPerSection=256;
        for(int first=0;first<built.Mesh.Vertices.Count-2;first+=verticesPerSection-2)
        {
            int end=Math.Min(built.Mesh.Vertices.Count,first+verticesPerSection);
            var vertices=built.Mesh.Vertices.Skip(first).Take(end-first).Select(v=>new RenderMeshVertex(v.Position,new(1,1,1,1),v.U,v.V,v.Normal,v.Tangent)).ToArray();
            // Routes authored past the shoreline stop at the top of the beach instead of running into the sea.
            var indices=built.Mesh.Indices.Chunk(3).Where(tri=>tri.All(i=>i>=first&&i<end
                &&StormChaseCoast.SeawardDistance(built.Mesh.Vertices[i].Position.X,built.Mesh.Vertices[i].Position.Z)<=-RoadShoreClearance))
                .SelectMany(tri=>tri.Select(i=>(uint)(i-first))).ToArray();
            if(indices.Length==0)continue;
            foreach(uint index in indices)if(vertices[index].Position.Y<StormChaseCoast.SeaLevel+.5f
                &&StormChaseCoast.SeawardDistance(vertices[index].Position.X,vertices[index].Position.Z)>-3400)
                throw new InvalidOperationException("Chase road dips to sea level: "+name);
            AssetHandle handle=Mesh($"road/{name}/{first}");
            catalog.UpsertMesh(new(){Handle=handle,Vertices=vertices,WideIndices=indices});
            var item=new RenderItem("chase/"+material,handle,null,default,new(1,1,1),0,false);
            roads.Add(item);
            roadSections.Add(new(vertices.Average(v=>v.Position.X),vertices.Average(v=>v.Position.Z),6000,item));
        }
        return built.Samples;
    }

    private void BuildFarmsAndTown()
    {
        Farm(80,1040,22,36,11,true);
        Farm(430,1120,18,28,-8,true);
        Farm(-780,1850,28,44,8,false);
        House(-460,920,13,18,12);
        House(-680,1780,14,20,3);
        // Roadside settlement south of the storm, with porches, windows and roofs.
        for(int i=0;i<9;i++)House(-430-(i%2)*90,-460+i*72,12+(i%3)*2,18,i%2==0?4:-6);
        for(int i=0;i<8;i++)House(-150,-440+i*76,14,20,180);
        // Farm grain silos: proper curved geometry and metal bands.
        foreach(var p in new[]{new Vector3f(140,0,1050),new Vector3f(460,0,1160),new Vector3f(-720,0,1900)})
        {
            float y=HeightAt(p.X,p.Z);
            PlaceBuilding("silo","rural",p.X,p.Z,0,1,"Grain silo");
        }
        Fence(-40,990,230,990,18,true);
        Fence(240,990,240,1180,18,true);
        Fence(330,1190,660,1190,22,true);
        Fence(-1150,600,-440,600,10,false);
        Fence(-1150,600,-1150,2080,10,false);
        Fence(-440,1400,-440,2100,10,false);
        // A lakeshore jetty and weathered rock clusters make the water edge readable.
        float dockY=4.7f;
        int pierStart=destructible.Count;
        for(int i=0;i<9;i++)Add("wood","box",new(1040,dockY,70+i*3.2f),new(6,.22f,3),0,2500,damage:true);
        for(int i=0;i<4;i++)foreach(float dx in new[]{-2.5f,2.5f})Add("wood","cylinder",new(1040+dx,1,70+i*8),new(.3f,8,.3f),0,2200,damage:true);
        RecordBuilding("rural/pier","rural","pier",new(1040,dockY,83),new(7,9,32),pierStart);
        var random=new Random(72013);
        for(int i=0;i<60;i++)
        {
            float a=i*MathF.Tau/60, r=1.07f+(float)random.NextDouble()*.09f;
            float x=LakeCenterX+MathF.Cos(a)*LakeRadiusX*r,z=LakeCenterZ+MathF.Sin(a)*LakeRadiusZ*r;
            float size=1.5f+(float)random.NextDouble()*3.5f;
            Add("rock","tree-crown-far",new(x,HeightAt(x,z)+size*.25f,z),new(size,size*.7f,size),a*180/MathF.PI,2500);
        }
    }

    private void Farm(float x,float z,float width,float length,float yaw,bool destructibleParts)
    {
        PlaceBuilding("barn","rural",x,z,yaw,width/20,"Farm barn");
    }
    private void House(float x,float z,float width,float length,float yaw)
    {
        PlaceBuilding("house","rural",x,z,yaw,width/14,"Prairie house");
    }
    private void Fence(float ax,float az,float bx,float bz,float spacing,bool damage)
    {
        float length=Distance(ax,az,bx,bz);int count=(int)MathF.Ceiling(length/spacing);
        float yaw=MathF.Atan2(bx-ax,bz-az)*180/MathF.PI;
        for(int i=0;i<=count;i++)
        {
            float t=i/(float)count,x=Lerp(ax,bx,t),z=Lerp(az,bz,t),y=HeightAt(x,z);
            Add("wood","box",new(x,y+1.15f,z),new(.18f,2.3f,.18f),yaw,1900,true,damage);
            if(i==count)continue;
            float nx=Lerp(ax,bx,(i+1f)/count),nz=Lerp(az,bz,(i+1f)/count),ny=HeightAt(nx,nz);
            foreach(float h in new[]{.8f,1.7f})Add("wood","box",new((x+nx)/2,(y+ny)/2+h,(z+nz)/2),new(.12f,.16f,length/count),yaw,1900,true,damage);
        }
    }

    private void BuildPowerLines()
    {
        for(int z=-2500;z<4200;z+=90)
        {
            float x=RoadX(z)-15,y=HeightAt(x,z);
            bool damage=true;
            Add("wood","cylinder",new(x,y+7,z),new(.4f,14,.4f),0,3300,true,damage);
            Add("wood","box",new(x,y+13,z),new(3.6f,.18f,.22f),0,3000,true,damage);
            foreach(float dx in new[]{-1.45f,0,1.45f})
            {
                Add("metal","cylinder",new(x+dx,y+13.4f,z),new(.18f,.55f,.18f),0,2000);
                if(z>=4110)continue;
                float nextX=RoadX(z+90)-15,nextY=HeightAt(nextX,z+90)+13.6f;
                // Four sagging spans per wire, efficiently grouped as instances.
                for(int segment=0;segment<4;segment++)
                {
                    float t0=segment/4f,t1=(segment+1)/4f;
                    Vector3f p0=new(Lerp(x,nextX,t0)+dx,Lerp(y+13.6f,nextY,t0)-MathF.Sin(t0*MathF.PI)*1.4f,z+t0*90);
                    Vector3f p1=new(Lerp(x,nextX,t1)+dx,Lerp(y+13.6f,nextY,t1)-MathF.Sin(t1*MathF.PI)*1.4f,z+t1*90);
                    Beam("wire",p0,p1,.032f,700);
                }
            }
        }
        // Explicit poles on the tornado's track let the native wind/destruction demo engage.
        for(int i=0;i<7;i++)
        {
            float x=35+i*95,z=1060+i*12,y=HeightAt(x,z);
            Add("wood","cylinder",new(x,y+6,z),new(.32f,12,.32f),0,3200,true,true);
            Add("wood","box",new(x,y+11,z),new(3.4f,.16f,.2f),0,3200,true,true);
        }
    }

    /// <summary>Client presentation hook; compiled away on the server.</summary>
    partial void BuildStreetFurniture();
    /// <summary>Client-only merged static props (street furniture, distribution lines), kept out of the fingerprint.</summary>
    private readonly List<Placed> streetProps = [];
    /// <summary>Client-only street-lamp light positions, bucketed by 256 m cell for night lighting.</summary>
    private readonly Dictionary<(int X,int Z),List<Vector3f>> streetLamps = [];
    /// <summary>Road centre-line samples per authored road name (client furniture and power lines follow the real ribbons).</summary>
    private readonly Dictionary<string,(IReadOnlyList<TerrainPathSample> Samples,float Width)> roadCenterlines = new(StringComparer.Ordinal);
    private readonly HashSet<string> gravelRoads = new(StringComparer.Ordinal);

    /// <summary>Client presentation hook; compiled away on the server.</summary>
    partial void AddVegetation(List<RenderItem> items, CameraState camera);

    private void Beam(string material,Vector3f p0,Vector3f p1,float thickness,float range)
    {
        float dx=p1.X-p0.X,dy=p1.Y-p0.Y,dz=p1.Z-p0.Z;
        float horizontal=MathF.Sqrt(dx*dx+dz*dz),length=MathF.Sqrt(horizontal*horizontal+dy*dy);
        var item=Item(material,"box",(p0+p1)*.5f,new(thickness,thickness,length),0,false) with
        {RotationEulerDegrees=new(-MathF.Atan2(dy,horizontal)*180/MathF.PI,MathF.Atan2(dx,dz)*180/MathF.PI,0)};
        scenery.Add(new(item.Position.X,item.Position.Z,range,item));
    }

    private void Add(string material,string mesh,Vector3f position,Vector3f scale,float yaw,float range=4000,bool shadow=true,bool damage=false,bool staticCollider=false)
    {
        var item=Item(material,mesh,position,scale,yaw,shadow);
        if(staticCollider) staticCollision.Add(item);
        if(damage)
        {
            item=item with{AllowStaticInstancing=true,WritesMotionVectors=false,MotionVectorId="destructible/"+(++objectNumber)};
            destructible.Add(item);
        }
        else scenery.Add(new(position.X,position.Z,range,item));
    }

    private static RenderItem Item(string material,string mesh,Vector3f position,Vector3f scale,float yaw,bool shadow)=>
        new("chase/"+material,Mesh(mesh),null,position,scale,yaw,shadow,AllowStaticInstancing:true);
    private static AssetHandle Mesh(string name)=>new("mesh","chase/"+name);
    private static AssetHandle Texture(string name)=>new("texture","chase/"+name);
    private static string FindTexture(string file)
    {
        string? directory=AppContext.BaseDirectory;
        while(directory is not null)
        {
            string candidate=Path.Combine(directory,"Assets","StormChase","Textures",file);
            if(File.Exists(candidate))return candidate;
            directory=Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("ImageGen chase texture is missing; copy Assets/StormChase/Textures with the showcase.",file);
    }
    private static float LakeRadius(float x,float z)=>MathF.Sqrt(Square((x-LakeCenterX)/LakeRadiusX)+Square((z-LakeCenterZ)/LakeRadiusZ));
    private static float Square(float x)=>x*x;
    private static float Smooth(float low,float high,float x){float t=Math.Clamp((x-low)/(high-low),0,1);return t*t*(3-2*t);}
    private static float Lerp(float a,float b,float t)=>a+(b-a)*t;
    private static float DistanceSquared(float x,float z,float px,float pz)=>Square(x-px)+Square(z-pz);
    private static float Distance(float x,float z,float px,float pz)=>MathF.Sqrt(DistanceSquared(x,z,px,pz));
    private sealed record Page(float X,float Z,float Size,AssetHandle[] Meshes);
    private sealed record Placed(float X,float Z,float Range,RenderItem Item);
}
