using System.Numerics;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

// Small reusable meshes keep scenery in the renderer's native static-instance path.
internal static class StormChaseTerrainGeometry
{
    public static RenderMeshData Box(AssetHandle handle)
    {
        var mesh = new Builder(handle);
        mesh.Quad(new(-.5f,-.5f,.5f), new(.5f,-.5f,.5f), new(.5f,.5f,.5f), new(-.5f,.5f,.5f), Vector3.UnitZ);
        mesh.Quad(new(.5f,-.5f,-.5f), new(-.5f,-.5f,-.5f), new(-.5f,.5f,-.5f), new(.5f,.5f,-.5f), -Vector3.UnitZ);
        mesh.Quad(new(.5f,-.5f,.5f), new(.5f,-.5f,-.5f), new(.5f,.5f,-.5f), new(.5f,.5f,.5f), Vector3.UnitX);
        mesh.Quad(new(-.5f,-.5f,-.5f), new(-.5f,-.5f,.5f), new(-.5f,.5f,.5f), new(-.5f,.5f,-.5f), -Vector3.UnitX);
        mesh.Quad(new(-.5f,.5f,.5f), new(.5f,.5f,.5f), new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f), Vector3.UnitY);
        mesh.Quad(new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f), new(.5f,-.5f,.5f), new(-.5f,-.5f,.5f), -Vector3.UnitY);
        return mesh.Build();
    }

    public static RenderMeshData Gable(AssetHandle handle)
    {
        var mesh = new Builder(handle);
        mesh.Quad(new(-.5f,-.5f,-.5f), new(-.5f,-.5f,.5f), new(0,.5f,.5f), new(0,.5f,-.5f), Vector3.Normalize(new(-2,1,0)));
        mesh.Quad(new(0,.5f,-.5f), new(0,.5f,.5f), new(.5f,-.5f,.5f), new(.5f,-.5f,-.5f), Vector3.Normalize(new(2,1,0)));
        mesh.Triangle(new(-.5f,-.5f,.5f), new(.5f,-.5f,.5f), new(0,.5f,.5f), Vector3.UnitZ);
        mesh.Triangle(new(.5f,-.5f,-.5f), new(-.5f,-.5f,-.5f), new(0,.5f,-.5f), -Vector3.UnitZ);
        return mesh.Build();
    }

    public static RenderMeshData Cylinder(AssetHandle handle, int sides = 12)
    {
        var mesh = new Builder(handle);
        for (int i=0;i<sides;i++)
        {
            float a=i*MathF.Tau/sides, b=(i+1)*MathF.Tau/sides;
            Vector3 p=new(MathF.Cos(a)*.5f,-.5f,MathF.Sin(a)*.5f), q=new(MathF.Cos(b)*.5f,-.5f,MathF.Sin(b)*.5f);
            mesh.Quad(p,q,q+Vector3.UnitY,p+Vector3.UnitY,Vector3.Normalize(new(MathF.Cos((a+b)/2),0,MathF.Sin((a+b)/2))));
            mesh.Triangle(new(0,.5f,0),p+Vector3.UnitY,q+Vector3.UnitY,Vector3.UnitY);
            mesh.Triangle(new(0,-.5f,0),q,p,-Vector3.UnitY);
        }
        return mesh.Build();
    }

    public static RenderMeshData TreeCrown(AssetHandle handle, bool simplified)
    {
        var mesh=new Builder(handle);
        mesh.Sphere(Vector3.Zero,new(.48f,.53f,.48f),simplified?7:11,simplified?4:7,0);
        if (!simplified)
        {
            mesh.Sphere(new(-.29f,-.09f,.02f),new(.32f,.33f,.31f),9,5,1);
            mesh.Sphere(new(.28f,-.06f,.08f),new(.34f,.35f,.32f),9,5,2);
            mesh.Sphere(new(.05f,-.1f,-.28f),new(.35f,.32f,.33f),9,5,3);
            mesh.Sphere(new(-.08f,.25f,.04f),new(.31f,.34f,.29f),9,5,4);
        }
        return mesh.Build();
    }

    public static RenderMeshData Grass(AssetHandle handle)
    {
        var mesh=new Builder(handle);
        for(int blade=0;blade<7;blade++)
        {
            float angle=blade*2.399963f;
            Vector3 basePoint=new(MathF.Cos(angle)*.22f,0,MathF.Sin(angle)*.22f);
            Vector3 across=new(MathF.Cos(angle),0,MathF.Sin(angle));
            Vector3 bend=new(-MathF.Sin(angle)*.2f,0,MathF.Cos(angle)*.2f);
            float height=.6f+(blade%3)*.18f;
            Vector3 middle=basePoint+Vector3.UnitY*height*.58f+bend*.3f;
            Vector3 top=basePoint+Vector3.UnitY*height+bend;
            Vector3 normal=Vector3.Normalize(Vector3.Cross(across,Vector3.UnitY));
            mesh.Quad(basePoint-across*.055f,basePoint+across*.055f,middle+across*.028f,middle-across*.028f,normal);
            mesh.Triangle(middle-across*.028f,middle+across*.028f,top,normal);
        }
        return mesh.Build();
    }

    private sealed class Builder(AssetHandle handle)
    {
        private readonly List<RenderMeshVertex> vertices=[];
        private readonly List<ushort> indices=[];
        public void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 n)
        {
            int start=vertices.Count;
            Vertex(a,n,0,1); Vertex(b,n,1,1); Vertex(c,n,1,0); Vertex(d,n,0,0);
            Face(start,start+1,start+2,n); Face(start,start+2,start+3,n);
        }
        public void Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 n)
        {
            int start=vertices.Count;
            Vertex(a,n,0,1); Vertex(b,n,1,1); Vertex(c,n,.5f,0); Face(start,start+1,start+2,n);
        }
        public void Sphere(Vector3 center,Vector3 radii,int sides,int rings,int phase)
        {
            int start=vertices.Count;
            for(int y=0;y<=rings;y++) for(int x=0;x<=sides;x++)
            {
                float latitude=y*MathF.PI/rings, angle=x*MathF.Tau/sides;
                Vector3 n=new(MathF.Sin(latitude)*MathF.Cos(angle),MathF.Cos(latitude),MathF.Sin(latitude)*MathF.Sin(angle));
                float crinkle=1+.07f*MathF.Sin(angle*3+phase)*MathF.Sin(latitude*4+phase);
                Vector3 p=center+n*radii*crinkle;
                float shade=.86f+.14f*((x*7+y*3+phase)%9)/8f;
                vertices.Add(new(new(p.X,p.Y,p.Z),new(shade,shade,shade,1),x/(float)sides,y/(float)rings,new(n.X,n.Y,n.Z),new(-MathF.Sin(angle),0,MathF.Cos(angle))));
            }
            for(int y=0;y<rings;y++)for(int x=0;x<sides;x++)
            {
                int a=start+y*(sides+1)+x,b=a+1,c=a+sides+1,d=c+1;
                Vector3 n=ToNumerics(vertices[a].Position)-center;
                Face(a,c,b,n);Face(b,c,d,n);
            }
        }
        private void Vertex(Vector3 p,Vector3 n,float u,float v)
        {
            Vector3 t=Vector3.Normalize(MathF.Abs(n.Y)>.9f?Vector3.UnitX:Vector3.Cross(Vector3.UnitY,n));
            vertices.Add(new(new(p.X,p.Y,p.Z),new(1,1,1,1),u,v,new(n.X,n.Y,n.Z),new(t.X,t.Y,t.Z)));
        }
        private void Face(int a,int b,int c,Vector3 normal)
        {
            if(Vector3.Dot(Vector3.Cross(ToNumerics(vertices[b].Position)-ToNumerics(vertices[a].Position),ToNumerics(vertices[c].Position)-ToNumerics(vertices[a].Position)),normal)<0) (b,c)=(c,b);
            indices.Add(checked((ushort)a));indices.Add(checked((ushort)b));indices.Add(checked((ushort)c));
        }
        public RenderMeshData Build()=>new(){Handle=handle,Vertices=vertices.ToArray(),Indices=indices.ToArray()};
        private static Vector3 ToNumerics(Vector3f p)=>new(p.X,p.Y,p.Z);
    }
}
