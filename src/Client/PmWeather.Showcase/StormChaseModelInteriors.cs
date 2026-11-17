using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.World;

// Reassemble the existing authored modules into real openings. Each cut module
// has a matching box mesh and native collider; the doors remain physical leaves.
internal sealed partial class StormChaseModelLibrary
{
    internal readonly record struct InteriorLayout(float FrontZ,float Width,float Height,float FloorY,float CenterZ);
    internal static InteriorLayout Layout(string id)=>id switch
    {
        "house"=>new(5.5f,1.8f,3.2f,.32f,0),
        "store"=>new(7,2.3f,3.5f,.32f,0),
        "gas-station"=>new(-5,2.3f,3.3f,.32f,-10),
        "barn"=>new(15,8,6.2f,.32f,0),
        "warehouse"=>new(12,7.6f,6.7f,.32f,0),
        "cityhall"=>new(10,3.6f,4.3f,.32f,0),
        _=>default
    };

    private void RegisterAccessibleModules(MutableRenderAssetCatalog catalog)
    {
        foreach(string id in new[]{"house","store","gas-station","barn","warehouse","cityhall"})
        {
            var model=models[id];var layout=Layout(id);var parts=new List<StormChaseModelPart>();
            float aperture=layout.Width*.5f;
            void Box(string name,string material,Vector3f center,Vector3f size,string category="interior",float release=85)
            {
                var handle=new AssetHandle("mesh","chase/model/"+id+"/"+name);
                catalog.UpsertMesh(StormChaseTerrainGeometry.Box(handle));
                var part=new StormChaseModelPart(name,handle,new("material","chase/model/"+material),center,default,size,true,category,release);
                parts.Add(part);meshParts.Add(handle,part);
            }
            foreach(var part in model.Parts)
            {
                var p=part.LocalPositionMeters;var size=part.SizeMeters;
                if(part.Name.StartsWith("end1-panel",StringComparison.Ordinal)&&p.X-size.X*.5f<aperture&&p.X+size.X*.5f>-aperture)
                {
                    float left=p.X-size.X*.5f,right=p.X+size.X*.5f,top=p.Y+size.Y*.5f;
                    string material=part.Material.Id["chase/model/".Length..];
                    if(left<-aperture)Box(part.Name+"-jamb-left",material,new((left-aperture)*.5f,p.Y,p.Z),new(-aperture-left,size.Y,size.Z),"wall",part.ReleaseWindMph);
                    if(right>aperture)Box(part.Name+"-jamb-right",material,new((right+aperture)*.5f,p.Y,p.Z),new(right-aperture,size.Y,size.Z),"wall",part.ReleaseWindMph);
                    float low=Math.Max(left,-aperture),high=Math.Min(right,aperture);
                    if(top>layout.Height&&high>low)Box(part.Name+"-lintel",material,new((low+high)*.5f,(top+layout.Height)*.5f,p.Z),new(high-low,top-layout.Height,size.Z),"wall",part.ReleaseWindMph);
                    continue;
                }
                // Decorative glazing across a new entrance must not leave an
                // invisible physical or visible glass barrier in the doorway.
                if(part.Category=="window"&&Math.Abs(p.Z-layout.FrontZ)<.4f&&p.Y-size.Y*.5f<layout.Height
                    &&p.X-size.X*.5f<aperture&&p.X+size.X*.5f>-aperture)continue;
                if(part.Category=="door"&&p.Z>layout.FrontZ-.4f)
                {
                    if(id=="warehouse"&&Math.Abs(p.X)>.1f){parts.Add(part);continue;}
                    if(id=="barn")
                    {
                        // Barn leaves slide against the exterior beside the opening.
                        parts.Add(part with{LocalPositionMeters=new(MathF.CopySign(aperture+size.X*.5f+.15f,p.X),p.Y,p.Z+.22f)});
                    }
                    else parts.Add(part with{LocalPositionMeters=new(-aperture+.12f,p.Y,p.Z+size.X*.5f),
                        LocalRotationRadians=part.LocalRotationRadians+new Vector3f(0,MathF.PI*.5f,0)});
                    continue;
                }
                if(id=="cityhall"&&part.Name=="steps")continue;
                parts.Add(part);
            }
            Box("entry-frame-left","trim",new(-aperture-.08f,layout.Height*.5f,layout.FrontZ+.17f),new(.16f,layout.Height,.22f),"trim",90);
            Box("entry-frame-right","trim",new(aperture+.08f,layout.Height*.5f,layout.FrontZ+.17f),new(.16f,layout.Height,.22f),"trim",90);
            Box("entry-frame-header","trim",new(0,layout.Height+.08f,layout.FrontZ+.17f),new(layout.Width+.32f,.16f,.22f),"trim",90);
            if(id=="house")
                for(int step=0;step<3;step++)Box("porch-step-"+step,"wood",new(0,(step+1)*.1f,9.7f-step*.55f),new(2.6f,(step+1)*.2f,.7f),"trim",80);
            else if(id=="cityhall")
                for(int step=0;step<2;step++)Box("entry-step-"+step,"concrete",new(0,(step+1)*.08f,12.9f-step*.7f),new(4,(step+1)*.16f,.9f),"trim",140);
            else Box("entry-step","concrete",new(0,.08f,layout.FrontZ+.75f),new(Math.Min(layout.Width+1,4),.16f,1),"trim",130);

            // Furniture is placed beside the central entrance corridor. These
            // remain separate destructible native rigid modules, never props
            // drawn over a closed collision volume.
            float side=id=="house"?4.3f:id=="gas-station"?5.8f:id=="store"?7:id=="barn"?7.5f:id=="warehouse"?12:8.5f;
            float center=layout.CenterZ;
            if(id=="house")
            {
                Box("interior-sofa","siding",new(-side,.85f,0),new(1.4f,1.05f,3));
                Box("interior-dining-table","wood",new(side,1.08f,-1.7f),new(2.8f,.16f,1.8f));
                int leg=0;
                foreach(float x in new[]{side-.9f,side+.9f})foreach(float z in new[]{-2.2f,-1.2f})Box("table-leg-"+leg++,"wood",new(x,.66f,z),new(.13f,.65f,.13f));
                Box("interior-kitchen-counter","trim",new(0,.95f,-4.25f),new(6,1.25f,.9f));
                Box("interior-bedroom-divider","siding",new(-1.8f,1.85f,-2.8f),new(.18f,3.05f,3));
            }
            else if(id is "store" or "gas-station")
            {
                foreach(int direction in new[]{-1,1})
                    for(int shelf=0;shelf<3;shelf++)Box("interior-shelf-"+(direction<0?"left":"right")+"-"+shelf,"wood",
                        new(side*direction,.65f+shelf*.7f,center),new(1.1f,.18f,4));
                Box("interior-service-counter","wood",new(-side*.45f,1.05f,center-2.8f),new(4.2f,1.4f,1));
                Box("interior-weather-display","lamp",new(-side*.45f,1.95f,center-2.8f),new(1.1f,.65f,.1f),"interior",50);
            }
            else if(id=="cityhall")
            {
                foreach(int direction in new[]{-1,1})for(int bench=0;bench<3;bench++)Box("interior-bench-"+(direction<0?"left":"right")+"-"+bench,"wood",new(side*direction,.7f,1-bench*2.4f),new(3.6f,.75f,.7f));
                Box("interior-warning-desk","wood",new(-5,1.05f,-6.5f),new(4,1.4f,1.2f));
                Box("interior-shelter-rear-wall","concrete",new(5,2,-7.5f),new(8,3.4f,.3f),"wall",230);
                Box("interior-shelter-side-wall","concrete",new(8.7f,2,-4.7f),new(.3f,3.4f,5.5f),"wall",230);
                Box("interior-shelter-left-wall","concrete",new(1.3f,2,-4.7f),new(.3f,3.4f,5.5f),"wall",230);
                Box("interior-shelter-front-left","concrete",new(2.7f,2,-1.9f),new(2.8f,3.4f,.3f),"wall",230);
                Box("interior-shelter-front-right","concrete",new(7.3f,2,-1.9f),new(2.8f,3.4f,.3f),"wall",230);
                Box("interior-shelter-roof","concrete",new(5,3.8f,-4.7f),new(8,.35f,5.8f),"roof",230);
                Box("interior-shelter-bench","wood",new(5,.72f,-6.8f),new(5,.8f,.7f));
            }
            else
                foreach(int direction in new[]{-1,1})for(int stack=0;stack<3;stack++)Box("interior-stock-"+(direction<0?"left":"right")+"-"+stack,id=="barn"?"wood":"metal",
                    new(side*direction,1.25f,center-4+stack*3),new(2.5f,1.85f,2.3f),"interior",90);
            Box("interior-ceiling-lamp","lamp",new(0,layout.Height-.4f,layout.CenterZ),new(.8f,.08f,.8f),"interior",65);
            models[id]=model with{Parts=parts.ToArray()};
        }
    }
}
