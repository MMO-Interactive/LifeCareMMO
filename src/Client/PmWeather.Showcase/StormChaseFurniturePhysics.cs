using System.Numerics;
using Engine.Physics;
using Engine.Physics.Collision;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

/// <summary>Bounded furnishing actors in the existing server PhysX scene. Never instantiated by an online replica.</summary>
internal sealed class StormChaseFurniturePhysics:IDisposable
{
    private const int MaximumResident=160,MaximumDynamic=48;
    private readonly StormChaseWorld world;
    private readonly StormChaseDestruction destruction;
    private readonly Dictionary<int,FurnitureState> states=[];
    private readonly Dictionary<int,Actor> actors=[];
    private float refresh;
    public IReadOnlyCollection<FurnitureState> States=>states.Values;
    public int ResidentCount=>actors.Count;
    public int DynamicCount=>actors.Values.Count(a=>a.Dynamic);
    public IReadOnlyList<Vector3f> PhysicsAnchors=>actors.Where(a=>a.Value.Dynamic).Select(a=>Render(states[a.Key].Position)).ToArray();
    public StormChaseFurniturePhysics(StormChaseWorld world,StormChaseDestruction destruction,IEnumerable<FurnitureState> initial)
    {this.world=world;this.destruction=destruction;foreach(var s in initial)states[s.FurnitureId]=s with{CollisionEnabled=false};}
    public void Put(FurnitureState s)
    {if(actors.Remove(s.FurnitureId,out var a))destruction.Physics.RemoveBody(a.Body);states[s.FurnitureId]=s with{CollisionEnabled=false};refresh=0;}
    public FurnitureState? Get(int id)=>states.GetValueOrDefault(id);
    public void AddUnknown(IEnumerable<FurnitureState> additional)
    {foreach(var s in additional)if(!states.ContainsKey(s.FurnitureId))states[s.FurnitureId]=s with{CollisionEnabled=false};}
    public void Update(float dt,RenderWeatherState? weather,IReadOnlyList<Vector3f> anchors,IReadOnlySet<string> activeOwners)
    {
        refresh-=dt;
        if(refresh<=0)
        {
            refresh=.25f;
            float Near(FurnitureState s)=>anchors.Count==0?float.MaxValue:anchors.Min(p=>Distance(s.Position,p));
            var wanted=states.Values.Where(s=>s.Placed&&StormChaseHousing.Furniture(s.Sku)!=null&&Near(s)<190)
                .OrderBy(Near).ThenBy(s=>s.FurnitureId).Take(MaximumResident).Select(s=>s.FurnitureId).ToHashSet();
            foreach(var id in actors.Keys.Where(id=>!wanted.Contains(id)&&!actors[id].Dynamic).ToArray())
            {destruction.Physics.RemoveBody(actors[id].Body);actors.Remove(id);states[id]=states[id] with{CollisionEnabled=false};}
            foreach(var id in wanted)
            {
                var state=states[id];float mph=StormChaseDestruction.SampleWindSpeedMph(Render(state.Position),weather);
                bool released=activeOwners.Contains(state.OwnerCharacterId)&&(mph>85||!state.Archived&&!state.Sleeping);
                if(actors.TryGetValue(id,out var existing))
                {
                    if(!existing.Dynamic&&released&&DynamicCount<MaximumDynamic)
                    {destruction.Physics.RemoveBody(existing.Body);actors[id]=Create(state,true);}
                }
                else if(actors.Count<MaximumResident)actors[id]=Create(state,released&&DynamicCount<MaximumDynamic);
            }
        }
        foreach(var (id,a) in actors.ToArray())
        {
            var old=states[id];
            if(!destruction.Physics.TryGetBodyPose(a.Body,out var pose))continue;
            Matrix4x4.Decompose(pose,out _,out var q,out var centre);
            var feet=centre-Vector3.Transform(a.LocalCentre,q);
            destruction.Physics.TryGetBodyDynamics(a.Body,out var linear,out var angular,out bool sleeping);
            states[id]=old with{Position=Net(feet),Rotation=new(q.X,q.Y,q.Z,q.W),LinearVelocity=Net(linear),AngularVelocity=Net(angular),Sleeping=!a.Dynamic||sleeping,CollisionEnabled=true,Archived=!a.Dynamic};
            if(!a.Dynamic)continue;
            var wind=StormChaseDestruction.SampleWindVelocity(Render(Net(centre)),weather);float mph=StormChaseDestruction.SampleWindSpeedMph(Render(Net(centre)),weather);
            var model=StormChaseHousing.Furniture(old.Sku)!;
            if(mph>45&&activeOwners.Contains(old.OwnerCharacterId))
            {
                var relative=wind-linear;float force=Math.Min(35000,.5f*1.225f*model.Size.X*model.Size.Y*relative.LengthSquared()*.65f);
                if(relative.LengthSquared()>1)a.Body.ApplyImpulse(Vector3.Normalize(relative)*force*dt);
                destruction.Physics.ApplyAngularImpulse(a.Body,Vector3.Cross(Vector3.UnitY,relative)*model.Mass*.024f*dt);
                a.Body.ApplyImpulse(Vector3.UnitY*model.Mass*Math.Clamp((mph-90)/80,0,2)*9.81f*dt);
                a.Rest=0;
            }
            else a.Rest=linear.LengthSquared()<.08f&&angular.LengthSquared()<.08f?a.Rest+dt:0;
            if(sleeping||a.Rest>3)
            {
                var final=states[id] with{LinearVelocity=default,AngularVelocity=default,Sleeping=true,Archived=true};
                destruction.Physics.RemoveBody(a.Body);actors[id]=Create(final,false);states[id]=final;
            }
        }
    }
    private Actor Create(FurnitureState s,bool dynamic)
    {
        var model=StormChaseHousing.Furniture(s.Sku)!;var local=new Vector3(0,model.Size.Y*.5f,0);var q=new Quaternion(s.Rotation.X,s.Rotation.Y,s.Rotation.Z,s.Rotation.W);
        var centre=new Vector3(s.Position.X,s.Position.Y,s.Position.Z)+Vector3.Transform(local,q);
        destruction.EnsurePhysicsAround(new(centre.X,centre.Y,centre.Z));
        var body=destruction.Physics.CreateBody(new("housing/furniture/"+s.FurnitureId,centre,dynamic?Native(s.LinearVelocity):Vector3.Zero,model.Mass,
            new BoxColliderShape(new(model.Size.X*.5f,model.Size.Y*.5f,model.Size.Z*.5f)),dynamic?PhysicsBodyType.Dynamic:PhysicsBodyType.Static));
        destruction.Physics.SetBodyPose(body,Matrix4x4.CreateFromQuaternion(q)*Matrix4x4.CreateTranslation(centre));
        if(dynamic){destruction.Physics.SetAngularVelocity(body,Native(s.AngularVelocity));destruction.Physics.SetBodyDamping(body,.1f,.18f);destruction.Physics.SetSpeculativeCollisionDetection(body,true);}
        return new(body,local,dynamic);
    }
    public void Dispose(){foreach(var a in actors.Values)destruction.Physics.RemoveBody(a.Body);actors.Clear();}
    private static float Distance(NetVector3 a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
    private static NetVector3 Net(Vector3 a)=>new(a.X,a.Y,a.Z);
    private static Vector3 Native(NetVector3 a)=>new(a.X,a.Y,a.Z);
    private static Vector3f Render(NetVector3 a)=>new(a.X,a.Y,a.Z);
    private sealed class Actor(PhysicsBody body,Vector3 localCentre,bool dynamic)
    {public PhysicsBody Body=body;public Vector3 LocalCentre=localCentre;public bool Dynamic=dynamic;public float Rest;}
}
