using System.Numerics;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal sealed record StormChaseDebrisState(string PartId,Vector3f Position,Quaternion Rotation,
    Vector3f LinearVelocity,Vector3f AngularVelocity,bool Sleeping,bool Archived,bool CollisionEnabled,bool Collected=false);

internal sealed partial class StormChaseDestruction
{
    private readonly Dictionary<string,Part> partsById=new(StringComparer.Ordinal);
    public bool IsReplicaMode {get;private set;}
    internal bool IsPartCollisionResident(string id)=>partsById.TryGetValue(id,out var part)&&part.HasBody;

    public IReadOnlyList<StormChaseDebrisState> GetReleasedPartStates()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var states=new List<StormChaseDebrisState>(released.Count);
        foreach(var entry in released.OrderBy(pair=>pair.Key,StringComparer.Ordinal))
        {
            var part=entry.Value;
            if(IsReplicaMode&&part.ReplicaState is { } replica){states.Add(replica);continue;}
            Matrix4x4 pose;Vector3 linear=Vector3.Zero,angular=Vector3.Zero;bool sleeping=true;
            if(part.IsDynamic)
            {
                if(!physics.TryGetBodyPose(part.Body,out pose)||!physics.TryGetBodyDynamics(part.Body,out linear,out angular,out sleeping))
                    throw new InvalidOperationException("Native debris snapshot lost body "+entry.Key);
            }
            else pose=part.FinalPose??ItemPose(part.FinalItem??part.Original);
            var rotation=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(pose));
            states.Add(new(entry.Key,ToRender(pose.Translation),rotation,ToRender(linear),ToRender(angular),sleeping,!part.IsDynamic,part.HasBody,part.IsCollected));
        }
        return states;
    }

    public void SetReplicaMode(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(enabled==IsReplicaMode)return;
        Reset();IsReplicaMode=enabled;
    }

    public bool TryCollectSettledPart(string id,Vector3f feet)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(IsReplicaMode||!IsFinite(ToNative(feet))||!released.TryGetValue(id,out var part)||part.IsDynamic||part.IsCollected||part.FinalPose is not { } pose)
            return false;
        if(HorizontalDistanceSquared(feet,ToRender(pose.Translation))>6*6||MathF.Abs(feet.Y-pose.Translation.Y)>4)return false;
        if(part.HasBody){physics.RemoveBody(part.Body);part.HasBody=false;staticParts.Remove(part);}
        part.IsCollected=true;streamCountdown=0;
        return true;
    }

    // Server poses become stationary native collision replicas. No client
    // tornado force, release decision, gravity integration or landing choice
    // can alter their road/river/wall destination.
    public void ApplyReplicaStates(IEnumerable<StormChaseDebrisState> states,bool completeBaseline=false)
    {
        ArgumentNullException.ThrowIfNull(states);SetReplicaMode(true);
        if(completeBaseline)Reset();
        foreach(var state in states)
        {
            if(!partsById.TryGetValue(state.PartId,out var part))throw new InvalidDataException("Unknown replicated world part: "+state.PartId);
            if(!IsFinite(ToNative(state.Position))||!float.IsFinite(state.Rotation.LengthSquared())||state.Rotation.LengthSquared()<.1f)
                throw new InvalidDataException("Invalid replicated rigid pose: "+state.PartId);
            bool first=!part.IsReleased;
            part.ReplicaState=state;
            var pose=Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(state.Rotation))*Matrix4x4.CreateTranslation(ToNative(state.Position));
            part.FinalPose=pose;
            part.FinalItem=part.Original with{Position=state.Position,RotationEulerDegrees=ToEulerDegrees(pose),RotationYawDegrees=0,
                WritesMotionVectors=!state.Archived,AllowStaticInstancing=state.Archived};
            part.IsReleased=true;part.IsDynamic=false;released[state.PartId]=part;
            part.IsCollected=state.Collected;
            if(part.HasBody)
            {
                if(state.CollisionEnabled&&!state.Collected)physics.SetBodyPose(part.Body,pose);
                else{physics.RemoveBody(part.Body);part.HasBody=false;staticParts.Remove(part);}
            }
            if(first&&part.Original.MaterialId=="chase/wood"&&part.Size.Y>=9&&part.Original.Mesh.Id.EndsWith("cylinder",StringComparison.Ordinal))
                failedUtilityAnchors.Add(part.Original.Position);
        }
        streamCountdown=0;
    }
}
