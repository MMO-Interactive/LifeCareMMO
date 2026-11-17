using System.Numerics;
using Engine.Physics;
using Engine.Physics.Collision;
using Engine.Physics.PhysX;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal readonly record struct StormChaseBreakEvent(string Id, Vector3f Position, bool IsUtilityPole);

/// <summary>
/// Persistent, native PhysX breakage for the chase world's individually authored
/// parts. Structural thresholds are gameplay tuning; all released motion and
/// terrain/part collision response are simulated by the actual PhysX provider.
/// </summary>
internal sealed partial class StormChaseDestruction : IDisposable
{
    private const float FixedDelta = 1f / 60f;
    private const int MaximumDebris = 256;
    private const int MaximumStaticParts = 2048;
    private const float CollisionActivationMeters = 550;
    private const float GridMeters = 256;
    private readonly StormChaseWorld world;
    private readonly PhysXPhysicsWorld physics;
    private readonly List<Part> parts = [];
    private readonly Dictionary<string, Part> released = new(StringComparer.Ordinal);
    private IReadOnlySet<string> destroyedBuildings = new HashSet<string>(StringComparer.Ordinal);
    private int destroyedBuildingsVersion = -1;
    private readonly Dictionary<string, Part> dynamicParts = new(StringComparer.Ordinal);
    private readonly HashSet<Part> staticParts = [];
    private readonly Dictionary<(int X,int Z),List<Part>> partGrid = [];
    private readonly Dictionary<(int X,int Z),PhysicsBody> terrainTiles = [];
    private readonly Dictionary<RenderItem,PhysicsBody> landmarkBodies = [];
    private readonly Queue<StormChaseBreakEvent> breakEvents = new();
    private readonly List<Vector3f> failedUtilityAnchors = [];
    private readonly Dictionary<string, Vector3> previousAxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3> axisVelocities = new(StringComparer.Ordinal);
    private PhysXCharacterController? avatarController;
    private double pendingSeconds;
    private bool disposed;
    private Vector3f observer = new(-300,12,20);
    private float streamCountdown;

    public int DestroyedPartCount => released.Count;
    public int ActiveDebrisCount => dynamicParts.Values.Count(part => part.Body.Velocity.LengthSquared() > .25f);
    public int DynamicBodyCount => dynamicParts.Count;
    public int StaticPartBodyCount => staticParts.Count;
    public int TerrainTileCount => terrainTiles.Count;
    public const int MaximumDynamicBodyCount = MaximumDebris;
    internal PhysXPhysicsWorld Physics => physics;
    public Func<Vector3f>? PersistentPhysicsAnchor {get;set;}
    public Func<IReadOnlyList<Vector3f>>? AdditionalPhysicsAnchors {get;set;}
    public long PhysicsStepCount { get; private set; }
    public PhysicsProviderInfo NativeProvider => physics.ProviderInfo;
    public float MaximumDebrisSpeed => dynamicParts.Count == 0 ? 0 : dynamicParts.Values.Max(part => part.Body.Velocity.Length());

    public static float SampleWindSpeedMph(Vector3f position, RenderWeatherState? weather) =>
        SampleTornadoWind(weather, ToNative(position)).SpeedMph;
    public static Vector3 SampleWindVelocity(Vector3f position, RenderWeatherState? weather) =>
        SampleTornadoWind(weather,ToNative(position)).Velocity;

    public bool IsPartDestroyed(string id) => released.ContainsKey(id);
    public IEnumerable<string> ReleasedPartIds => released.Keys;
    public bool IsBuildingDestroyed(string id) => world.Buildings.FirstOrDefault(building=>building.Id==id) is { } building
        && building.DestructiblePartIds.Count(part=>released.ContainsKey(part)) >= Math.Max(1,building.DestructiblePartIds.Count/2);
    public IReadOnlySet<string> DestroyedBuildingIds
    {
        get
        {
            if (destroyedBuildingsVersion != released.Count)
            {
                destroyedBuildings = world.Buildings.Where(building => building.DestructiblePartIds.Count(released.ContainsKey)
                    >= Math.Max(1, building.DestructiblePartIds.Count / 2)).Select(building => building.Id).ToHashSet(StringComparer.Ordinal);
                destroyedBuildingsVersion = released.Count;
            }
            return destroyedBuildings;
        }
    }

    public void EnsurePhysicsAround(Vector3f position)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        observer=position;
        RefreshCollisionStreaming(null);
    }

    public IReadOnlyList<StormChaseBreakEvent> DrainBreakEvents()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (breakEvents.Count == 0) return Array.Empty<StormChaseBreakEvent>();
        var events = breakEvents.ToArray();
        breakEvents.Clear();
        return events;
    }

    /// <summary>Resolves on-foot movement in the same native terrain/debris scene; fly cameras bypass this.</summary>
    public Vector3f ResolveAvatarMovement(Vector3f previousFeet, Vector3f nextFeet, float wallDelta,
        RenderWeatherState? weather = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var previous = ToNative(previousFeet);
        var requested = ToNative(nextFeet);
        if (!IsFinite(previous) || !IsFinite(requested)) throw new ArgumentOutOfRangeException(nameof(nextFeet));
        if(HorizontalDistanceSquared(observer,previousFeet)>180*180)EnsurePhysicsAround(previousFeet);
        avatarController ??= physics.CreateCapsuleCharacter("chase/avatar", previous);
        physics.TryGetCharacterFootPosition(avatarController, out var actual);
        // Tab/camera-mode jumps are explicit positioning, rather than a sweep
        // through kilometers of terrain. Native movement remains continuous.
        if (Vector3.DistanceSquared(actual, previous) > 4)
            physics.SetCharacterFootPosition(avatarController, previous);
        if (Vector3.DistanceSquared(requested, previous) > 32 * 32)
        {
            physics.SetCharacterFootPosition(avatarController, requested);
            return ToRender(avatarController.FootPosition);
        }
        if (!float.IsFinite(wallDelta) || wallDelta <= 0) return ToRender(avatarController.FootPosition);
        float delta = MathF.Min(wallDelta, .25f);
        var displacement = requested - previous;
        var wind = SampleTornadoWind(weather, previous + Vector3.UnitY, axisVelocities);
        var horizontalWind = new Vector3(wind.Velocity.X, 0, wind.Velocity.Z);
        bool sheltered=world.FindShelter(previousFeet,IsPartDestroyed) is not null;
        if (wind.SpeedMph > 65 && horizontalWind.LengthSquared() > .01f)
        {
            // Modest controllable drift conveys vortex force without teleporting
            // the observer or turning the player into a free-flying rigid body.
            float driftSpeed = Math.Clamp((wind.SpeedMph - 65) / 40, 0, 2.8f);
            displacement += Vector3.Normalize(horizontalWind) * (driftSpeed * delta*(sheltered?.08f:1));
        }
        // A small downward sweep maintains native terrain/step support even
        // when the requested foot height uses the analytic world surface.
        displacement.Y -= 1.5f * delta;
        if (!physics.MoveCharacter(avatarController, displacement, delta, out actual))
            throw new InvalidOperationException("The chase avatar lost its native character controller.");
        return ToRender(actual);
    }

    public void ResetAvatarMovement()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (avatarController is null) return;
        physics.RemoveCharacter(avatarController);
        avatarController = null;
    }

    public StormChaseDestruction(StormChaseWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        this.world=world;
        physics = new PhysXPhysicsWorld(new Vector3(0, -9.81f, 0), new(FixedDelta, 1));
        try
        {
            foreach (var item in world.DestructibleItems)
            {
                if (string.IsNullOrEmpty(item.MotionVectorId))
                    throw new InvalidOperationException("Destructible world parts require a stable motion-vector ID.");
                var size = Vector3.Abs(ToNative(item.Scale));
                bool hasDamage=world.PartDamage.TryGetValue(item.MotionVectorId,out var damage);
                var part = new Part(item,size,hasDamage?damage.ReleaseWindMph:BreakThreshold(item),
                    hasDamage?damage.MassKilograms:PartMass(item,size));
                parts.Add(part);
                partsById.Add(item.MotionVectorId,part);
                var cell=Cell(item.Position);
                if(!partGrid.TryGetValue(cell,out var bucket))partGrid.Add(cell,bucket=[]);
                bucket.Add(part);
            }
            RefreshCollisionStreaming(null);
        }
        catch
        {
            physics.Dispose();
            throw;
        }
    }

    public void Update(float wallDelta, RenderWeatherState? weather, Vector3f? observerPosition=null,Action<float>? beforeStep=null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(wallDelta) || wallDelta <= 0) return;
        if(observerPosition is { } position)observer=position;
        streamCountdown-=MathF.Min(wallDelta,.25f);
        if(streamCountdown<=0){RefreshCollisionStreaming(weather);streamCountdown=.2f;}
        UpdateAxisMotion(weather, MathF.Min(wallDelta, .25f));
        // Input stalls do not produce an unbounded simulation burst. Ordinary
        // display/UI deltas run the identical 60 Hz force/contact simulation.
        pendingSeconds += MathF.Min(wallDelta, .25f);
        var windCandidates=new HashSet<Part>();
        foreach(var storm in IsReplicaMode?Array.Empty<RenderStormCellState>():weather?.ActiveStorms??Array.Empty<RenderStormCellState>())foreach(var tornado in storm.Tornadoes)
        {
            float radius=MathF.Max(tornado.WindfieldRadiusMeters,MathF.Max(tornado.TopRadiusMeters,tornado.BaseRadiusMeters)*12)+40;
            foreach(var part in QueryParts(tornado.Position,radius))if(!part.IsReleased)windCandidates.Add(part);
        }
        int steps = 0;
        while (pendingSeconds >= FixedDelta && steps++ < 15)
        {
            foreach (var part in windCandidates)
            {
                if(part.IsReleased||dynamicParts.Count>=MaximumDebris)continue;
                var wind=SampleTornadoWind(weather,ToNative(part.Original.Position),axisVelocities);
                if(wind.SpeedMph>=part.BreakWindMph)Release(part,wind);
            }
            foreach(var part in dynamicParts.Values)
                ApplyWind(part,SampleTornadoWind(weather,part.Body.Position,axisVelocities),FixedDelta);
            beforeStep?.Invoke(FixedDelta);
            physics.Step(FixedDelta);
            ArchiveSettledParts(FixedDelta);
            PhysicsStepCount++;
            pendingSeconds -= FixedDelta;
        }
    }

    public IReadOnlyList<RenderItem> Apply(IReadOnlyList<RenderItem> staticItems)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(staticItems);
        if (released.Count == 0) return staticItems;
        var items = new List<RenderItem>(staticItems.Count + released.Count);
        foreach (var item in staticItems)
        {
            if (item.MotionVectorId is { } id && released.ContainsKey(id)) continue;
            bool brokenConnection = item.MaterialId == "chase/wire" && failedUtilityAnchors.Any(anchor =>
                HorizontalDistanceSquared(anchor, item.Position) < 95 * 95);
            bool unsupportedInsulator = item.MaterialId == "chase/metal" && item.Scale.Y < 1
                && failedUtilityAnchors.Any(anchor => HorizontalDistanceSquared(anchor, item.Position) < 4 * 4
                    && item.Position.Y > anchor.Y + 4);
            if (!brokenConnection && !unsupportedInsulator) items.Add(item);
        }
        foreach (var part in released.Values)
        {
            if(part.IsCollected)continue;
            if(!part.IsDynamic)
            {
                var archived=part.FinalItem!;
                if(HorizontalDistanceSquared(observer,archived.Position)<4200*4200)items.Add(archived);
                continue;
            }
            if (!physics.TryGetBodyPose(part.Body, out var pose))
                throw new InvalidOperationException("A released debris actor lost its native rigid pose.");
            if(Vector3.DistanceSquared(ToNative(observer),pose.Translation)>5000*5000)continue;
            items.Add(part.Original with
            {
                Position = ToRender(pose.Translation),
                RotationEulerDegrees = ToEulerDegrees(pose),
                RotationYawDegrees = 0,
                AllowStaticInstancing = false,
                WritesMotionVectors = true,
            });
        }
        return items;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ResetAvatarMovement();
        foreach (var part in released.Values)
        {
            if(part.HasBody)physics.RemoveBody(part.Body);
            staticParts.Remove(part);
            part.HasBody=false;
            part.IsDynamic=false;
            part.FinalItem=null;
            part.FinalPose=null;
            part.StillSeconds=0;
            part.IsReleased = false;
            part.IsCollected = false;
            part.ReplicaState=null;
        }
        released.Clear();
        dynamicParts.Clear();
        breakEvents.Clear();
        failedUtilityAnchors.Clear();
        previousAxes.Clear();
        axisVelocities.Clear();
        pendingSeconds = 0;
        PhysicsStepCount = 0;
        RefreshCollisionStreaming(null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        physics.Dispose();
        released.Clear();
        dynamicParts.Clear();staticParts.Clear();terrainTiles.Clear();landmarkBodies.Clear();partGrid.Clear();partsById.Clear();
        breakEvents.Clear();
        previousAxes.Clear();
        axisVelocities.Clear();
        parts.Clear();
        failedUtilityAnchors.Clear();
    }

    private PhysicsBody CreateBody(Part part, PhysicsBodyType type)
    {
        var body = physics.CreateBody(new("chase/" + part.Original.MotionVectorId,
            ToNative(part.Original.Position), Vector3.Zero,
            type == PhysicsBodyType.Dynamic ? part.Mass : 0,
            new BoxColliderShape(Vector3.Max(part.Size * .5f, new Vector3(.025f))), type));
        physics.SetBodyPose(body, ItemPose(part.Original));
        if (type == PhysicsBodyType.Dynamic)
        {
            physics.SetBodyDamping(body, .025f, .16f);
            physics.SetSpeculativeCollisionDetection(body);
        }
        return body;
    }

    private static Matrix4x4 ItemPose(RenderItem item)
    {
        var euler = item.RotationEulerDegrees ?? new Vector3f(0, item.RotationYawDegrees, 0);
        return Matrix4x4.CreateRotationX(euler.X * MathF.PI / 180)
            * Matrix4x4.CreateRotationY(euler.Y * MathF.PI / 180)
            * Matrix4x4.CreateRotationZ(euler.Z * MathF.PI / 180)
            * Matrix4x4.CreateTranslation(ToNative(item.Position));
    }

    private void Release(Part part, WindSample wind)
    {
        if(part.HasBody)physics.RemoveBody(part.Body);
        staticParts.Remove(part);
        part.Body = CreateBody(part, PhysicsBodyType.Dynamic);
        part.HasBody=true;part.IsDynamic=true;
        part.IsReleased = true;
        released.Add(part.Original.MotionVectorId!, part);
        dynamicParts.Add(part.Original.MotionVectorId!,part);
        bool utilityPole = part.Original.MaterialId == "chase/wood" && part.Size.Y >= 9
            && part.Original.Mesh.Id.EndsWith("cylinder", StringComparison.Ordinal);
        if (utilityPole) failedUtilityAnchors.Add(part.Original.Position);
        var eventPosition = part.Body.Position + Vector3.UnitY * (utilityPole ? part.Size.Y * .45f : 0);
        breakEvents.Enqueue(new(part.Original.MotionVectorId!, ToRender(eventPosition), utilityPole));
        // A small initial impulse represents failed fasteners. Continued lift,
        // translation and spin come from aerodynamic forces and native contacts.
        part.Body.ApplyImpulse((wind.Velocity * .06f + Vector3.UnitY * .7f) * part.Mass);
        physics.SetAngularVelocity(part.Body, Vector3.Normalize(new Vector3(.45f,
            part.Original.Position.X % 2 < 1 ? .7f : -.7f, .3f)) * .35f);
    }

    private void ApplyWind(Part part, WindSample wind, float delta)
    {
        if (wind.PeakMetersPerSecond <= 0) return;
        var relative = wind.Velocity - part.Body.Velocity;
        float speed = relative.Length();
        var direction = speed > .01f ? relative / speed : Vector3.Zero;
        physics.TryGetBodyPose(part.Body, out var pose);
        // Projected cross-section of the native oriented box. The mass and
        // inertia remain physical rather than assigning an animated velocity.
        var localDirection = Vector3.TransformNormal(direction, Matrix4x4.Transpose(pose));
        var size = part.Size;
        float area = MathF.Abs(localDirection.X) * size.Y * size.Z
            + MathF.Abs(localDirection.Y) * size.X * size.Z
            + MathF.Abs(localDirection.Z) * size.X * size.Y;
        var impulse = direction * (.5f * 1.225f * 1.05f * area * speed * speed * delta);
        // Aerodynamic impulses are bounded per fixed tick to keep newly
        // detached, very thin trim stable without changing its collision mass.
        float cap = part.Mass * 75 * delta;
        if (impulse.LengthSquared() > cap * cap) impulse = Vector3.Normalize(impulse) * cap;
        // Drag alone throws debris across the vortex: its inertia needs the
        // pressure gradient that holds circulating air in an orbit. Apply
        // continuous native impulses for confinement, inward capture and lift.
        float tangentialSpeed = Vector3.Dot(part.Body.Velocity - wind.AxisVelocity, wind.Tangent);
        float confinement = tangentialSpeed * tangentialSpeed / MathF.Max(wind.Distance, wind.Radius * .22f)
            * wind.CaptureEnvelope;
        float pressure = .11f * wind.PeakMetersPerSecond * wind.PeakMetersPerSecond / MathF.Max(wind.Radius, 12)
            * MathF.Min(wind.Distance / wind.Radius, 1) * wind.CoreEnvelope;
        float lift = MathF.Min(28, .18f * wind.PeakMetersPerSecond * wind.PeakMetersPerSecond
            / MathF.Max(wind.Radius, 12)) * wind.CoreEnvelope;
        var vortexImpulse = (-wind.Radial * MathF.Min(65, confinement + pressure) + Vector3.UnitY * lift)
            * (part.Mass * delta);
        var combinedImpulse = impulse + vortexImpulse;
        float totalCap = part.Mass * 110 * delta;
        if (combinedImpulse.LengthSquared() > totalCap * totalCap)
            combinedImpulse = Vector3.Normalize(combinedImpulse) * totalCap;
        part.Body.ApplyImpulse(combinedImpulse);
        // Pressure is offset from the center on a irregular broken piece;
        // native torque uses the actual mass/inertia tensor for angular motion.
        var lever = Vector3.TransformNormal(new Vector3(size.X * .11f, size.Y * .07f, size.Z * -.09f), pose);
        physics.ApplyAngularImpulse(part.Body, Vector3.Cross(lever, impulse) * .2f);
    }

    private void UpdateAxisMotion(RenderWeatherState? weather, float delta)
    {
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var storm in weather?.ActiveStorms ?? Array.Empty<RenderStormCellState>())
            foreach (var tornado in storm.Tornadoes)
            {
                activeIds.Add(tornado.Id);
                var position = ToNative(tornado.Position);
                var simulationVelocity = ToNative(tornado.Velocity);
                var velocity = simulationVelocity;
                if (previousAxes.TryGetValue(tornado.Id, out var previous))
                {
                    var movement = position - previous;
                    // Large lifecycle skips/presets move the source, but do not
                    // physically strike debris with a teleport's velocity.
                    if (movement.LengthSquared() < 40 * 40)
                    {
                        var measured = movement / MathF.Max(delta, .001f);
                        float speed = measured.Length();
                        if (speed > 120) measured *= 120 / speed;
                        var prior = axisVelocities.GetValueOrDefault(tornado.Id, simulationVelocity);
                        velocity = Vector3.Lerp(prior, measured, 1 - MathF.Exp(-delta * 8));
                    }
                }
                previousAxes[tornado.Id] = position;
                axisVelocities[tornado.Id] = velocity;
            }
        foreach (var id in previousAxes.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            previousAxes.Remove(id);
            axisVelocities.Remove(id);
        }
    }

    private static WindSample SampleTornadoWind(RenderWeatherState? weather, Vector3 position,
        IReadOnlyDictionary<string, Vector3>? actualAxisVelocities = null)
    {
        var strongest = default(WindSample);
        float strongestInfluence = 0;
        if (weather is null) return strongest;
        foreach (var storm in weather.ActiveStorms)
        {
            if (storm.VisualOnly || (storm.SmoothedStage ?? storm.Stage + storm.Energy / 100f) < 3) continue;
            foreach (var tornado in storm.Tornadoes)
            {
                if (tornado.VisualOnly) continue;
                float peakMph = MathF.Max(0, tornado.SmoothedWindSpeed ?? tornado.WindSpeed);
                // A weakening tornado can remain on the ground below its
                // initial touchdown threshold. Preserve its continuous force.
                if (peakMph < 1 || tornado.TouchdownAmount <= 0 && peakMph < Math.Max(1, tornado.TouchdownSpeed)) continue;
                float width = MathF.Max(5, tornado.SmoothedWidthMeters ?? tornado.WidthMeters);
                float baseRadius = MathF.Max(tornado.BaseRadiusMeters, width / 2.5f);
                var offset = position - ToNative(tornado.Position);
                float height = MathF.Max(40, tornado.HeightMeters);
                float heightFraction = Math.Clamp(offset.Y / height, 0, 1);
                float heightTaper = 1 - Smooth(.85f, 1.2f, offset.Y / height);
                if (heightTaper <= 0) continue;
                float topRadius = MathF.Max(tornado.TopRadiusMeters, baseRadius * 1.6f);
                float radius = baseRadius + (topRadius - baseRadius) * MathF.Pow(heightFraction, 1.3f);
                float r = MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
                if (r > MathF.Max(tornado.WindfieldRadiusMeters, radius * 12)) continue;
                float distanceRatio = r / radius;
                float radialProfile = distanceRatio <= 1 ? distanceRatio : MathF.Pow(distanceRatio, -.8f);
                float heightProfile = (1 - .35f * heightFraction) * heightTaper;
                float speedMph = peakMph * radialProfile * heightProfile;
                var radial = r > .1f ? new Vector3(offset.X / r, 0, offset.Z / r) : Vector3.Zero;
                var tangent = new Vector3(-radial.Z, 0, radial.X);
                float core = MathF.Exp(-distanceRatio * distanceRatio / (1.85f * 1.85f)) * heightTaper;
                float capture = MathF.Exp(-distanceRatio * distanceRatio / 9) * heightTaper;
                float peakMeters = MathF.Min(170, peakMph * .44704f);
                float localMeters = speedMph * .44704f;
                float updraft = peakMeters * (.68f * core + .10f * radialProfile * capture);
                var axisVelocity = actualAxisVelocities?.GetValueOrDefault(tornado.Id) ?? ToNative(tornado.Velocity);
                var velocity = tangent * (.94f * localMeters) - radial * (.48f * localMeters)
                    + Vector3.UnitY * updraft + axisVelocity;
                var sample = new WindSample(speedMph, velocity, radial, tangent, axisVelocity, radius, r, peakMeters, core, capture);
                var combined = AddSubVortexWind(storm, tornado, position, peakMph, sample);
                float influence = combined == sample ? localMeters + updraft
                    : combined.SpeedMph * .44704f + MathF.Max(0, combined.Velocity.Y - axisVelocity.Y);
                if (influence <= strongestInfluence) continue;
                strongestInfluence = influence;
                strongest = combined;
            }
        }
        return strongest;
    }

    private static WindSample AddSubVortexWind(RenderStormCellState storm, RenderTornadoState tornado,
        Vector3 position, float parentPeakMph, WindSample parent)
    {
        // Failed funnels have no ground circulation. Condensation/lowering are
        // deliberately independent: invisible circulating air still has force.
        if (tornado.TouchdownAmount <= 0 || storm.ActiveVorticies.Count == 0) return parent;
        Vector3 extra = Vector3.Zero;
        foreach (var field in storm.ActiveVorticies)
        {
            if (field.IsDead || field.AgeTicks < 0 || field.LifetimeTicks <= field.AgeTicks ||
                !StormChaseVortexFields.BelongsTo(field, tornado) || !float.IsFinite(field.WidthMeters) ||
                !float.IsFinite(field.WindSpeedMultiplier) || field.WindSpeedMultiplier <= 0) continue;
            var offset = position - ToNative(field.Position);
            if (!IsFinite(offset)) continue;
            float radius = MathF.Max(2, field.WidthMeters / 2.5f);
            float distance = MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
            float ratio = distance / radius;
            if (ratio >= 3.5f) continue;
            float vertical = 1 - Smooth(.25f, .7f, offset.Y / MathF.Max(40, tornado.HeightMeters));
            if (vertical <= 0) continue;
            float envelope = (1 - Smooth(1.7f, 3.5f, ratio)) * vertical;
            float rankine = ratio <= 1 ? ratio : 1 / ratio;
            float peak = MathF.Min(parentPeakMph, StormChaseVortexFields.MaximumCombinedWindMph) * .44704f
                * Math.Clamp(field.WindSpeedMultiplier, 0, .4f);
            var radial = distance > .01f ? new Vector3(offset.X / distance, 0, offset.Z / distance) : Vector3.Zero;
            var tangent = new Vector3(-radial.Z, 0, radial.X);
            extra += (tangent - radial * .12f) * (peak * rankine * envelope)
                + Vector3.UnitY * (peak * .15f * MathF.Exp(-ratio * ratio) * envelope);
        }
        // Preserve the legacy main-vortex response exactly outside local fields.
        if (extra == Vector3.Zero) return parent;
        var circulation = parent.Velocity - parent.AxisVelocity + extra;
        float peakMph = MathF.Min(StormChaseVortexFields.MaximumCombinedWindMph,
            parentPeakMph * StormChaseVortexFields.MaximumPeakEnhancement);
        float cap = peakMph * .44704f;
        if (circulation.LengthSquared() > cap * cap) circulation *= cap / circulation.Length();
        float horizontal = MathF.Sqrt(circulation.X * circulation.X + circulation.Z * circulation.Z);
        // Main-field MPH is its tangential/inflow amplitude. Preserve that same
        // convention when the localized velocity modifies a probe/radar reading.
        float measuredMph = MathF.Min(peakMph, horizontal / (MathF.Sqrt(.94f * .94f + .48f * .48f) * .44704f));
        return parent with { SpeedMph = measuredMph, Velocity = circulation + parent.AxisVelocity };
    }

    private IEnumerable<Part> QueryParts(Vector3f center,float radius)
    {
        int minX=(int)MathF.Floor((center.X-radius)/GridMeters),maxX=(int)MathF.Floor((center.X+radius)/GridMeters);
        int minZ=(int)MathF.Floor((center.Z-radius)/GridMeters),maxZ=(int)MathF.Floor((center.Z+radius)/GridMeters);
        float squared=radius*radius;
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
            if(partGrid.TryGetValue((x,z),out var bucket))foreach(var part in bucket)
                if(HorizontalDistanceSquared(center,part.Original.Position)<=squared)yield return part;
    }

    private static (int X,int Z) Cell(Vector3f position)=>((int)MathF.Floor(position.X/GridMeters),(int)MathF.Floor(position.Z/GridMeters));

    private void RefreshCollisionStreaming(RenderWeatherState? weather)
    {
        var wantedTerrain=new HashSet<(int X,int Z)>();
        void TerrainAround(Vector3f point,int reach)
        {
            int tx=(int)MathF.Floor(point.X/1024),tz=(int)MathF.Floor((point.Z-1000)/1024);
            for(int z=tz-reach;z<=tz+reach;z++)for(int x=tx-reach;x<=tx+reach;x++)
                if(world.ContainsPlayablePosition(x*1024+512,z*1024+1512))wantedTerrain.Add((x,z));
        }
        TerrainAround(observer,1);
        Vector3f? vehicleAnchor=PersistentPhysicsAnchor?.Invoke();
        if(vehicleAnchor is { } parked)TerrainAround(parked,1);
        var interests=new List<Vector3f>{observer};
        if(vehicleAnchor is { } persistent)interests.Add(persistent);
        if(AdditionalPhysicsAnchors?.Invoke() is { } additional)interests.AddRange(additional);
        interests=interests.Distinct().ToList();
        foreach(var interest in interests)TerrainAround(interest,1);
        var axes=(weather?.ActiveStorms??Array.Empty<RenderStormCellState>()).SelectMany(storm=>storm.Tornadoes).Select(tornado=>tornado.Position).ToArray();
        foreach(var axis in axes)TerrainAround(axis,1);
        foreach(var part in dynamicParts.Values)
        {
            var p=part.Body.Position;
            // Only descending/near-ground debris requires broad terrain tiles.
            // Streaming its projected next contact also protects fast fragments.
            if(p.Y-world.HeightAt(p.X,p.Z)<120||part.Body.Velocity.Y<0)
                TerrainAround(ToRender(p+part.Body.Velocity*.35f),1);
        }
        foreach(var key in terrainTiles.Keys.Where(key=>!wantedTerrain.Contains(key)).ToArray())
        {physics.RemoveBody(terrainTiles[key]);terrainTiles.Remove(key);}
        foreach(var key in wantedTerrain)
            if(!terrainTiles.ContainsKey(key))terrainTiles.Add(key,CookTerrainTile(key.X,key.Z));

        // Budget each independent player/car interest rather than letting the
        // first observer consume the entire shared server collision budget.
        var wantedParts=new HashSet<Part>();
        foreach(var interest in interests)
            foreach(var part in QueryParts(interest,CollisionActivationMeters)
                .Where(part=>!part.IsDynamic&&!part.IsReleased&&part.Size.X*part.Size.Y*part.Size.Z>.06f)
                .OrderBy(part=>HorizontalDistanceSquared(interest,part.Original.Position)).Take(MaximumStaticParts))wantedParts.Add(part);
        // Archived fragments retain their actual settled native pose. Their
        // original grid location can be far from the final wreckage position.
        foreach(var part in released.Values)
            if(!part.IsDynamic&&!part.IsCollected&&part.FinalItem is { } final&&(!IsReplicaMode||part.ReplicaState?.CollisionEnabled!=false)
                &&interests.Any(interest=>HorizontalDistanceSquared(interest,final.Position)<CollisionActivationMeters*CollisionActivationMeters)
                &&wantedParts.Count<MaximumStaticParts*interests.Count)wantedParts.Add(part);
        foreach(var part in staticParts.Where(part=>!wantedParts.Contains(part)).ToArray())
        {physics.RemoveBody(part.Body);part.HasBody=false;staticParts.Remove(part);}
        foreach(var part in wantedParts)
        {
            if(part.HasBody)continue;
            part.Body=CreateBody(part,PhysicsBodyType.Static);part.HasBody=true;staticParts.Add(part);
            if(part.FinalItem is { } final)physics.SetBodyPose(part.Body,part.FinalPose??ItemPose(final));
        }
        var wantedLandmarks=world.StaticCollisionItems.Where(item=>interests.Any(interest=>HorizontalDistanceSquared(interest,item.Position)<650*650)
            ||axes.Any(axis=>HorizontalDistanceSquared(axis,item.Position)<1100*1100)).ToHashSet();
        foreach(var item in landmarkBodies.Keys.Where(item=>!wantedLandmarks.Contains(item)).ToArray())
        {physics.RemoveBody(landmarkBodies[item]);landmarkBodies.Remove(item);}
        int number=0;
        foreach(var item in world.StaticCollisionItems)
        {
            number++;
            if(!wantedLandmarks.Contains(item)||landmarkBodies.ContainsKey(item))continue;
            var body=physics.CreateBody(new("chase/solid/"+number,ToNative(item.Position),Vector3.Zero,0,
                new BoxColliderShape(Vector3.Max(Vector3.Abs(ToNative(item.Scale))*.5f,new(.025f))),PhysicsBodyType.Static));
            physics.SetBodyPose(body,ItemPose(item));landmarkBodies.Add(item,body);
        }
    }

    private void ArchiveSettledParts(float delta)
    {
        foreach(var part in dynamicParts.Values.ToArray())
        {
            part.StillSeconds=part.Body.Velocity.LengthSquared()<.25f?part.StillSeconds+delta:0;
            if(part.StillSeconds<3||!physics.TryGetBodyPose(part.Body,out var pose))continue;
            part.FinalItem=part.Original with {Position=ToRender(pose.Translation),RotationEulerDegrees=ToEulerDegrees(pose),RotationYawDegrees=0,
                AllowStaticInstancing=true,WritesMotionVectors=false};
            part.FinalPose=pose;
            physics.RemoveBody(part.Body);part.HasBody=false;part.IsDynamic=false;
            dynamicParts.Remove(part.Original.MotionVectorId!);
        }
    }

    private PhysicsBody CookTerrainTile(int tileX,int tileZ)
    {
        const int cells=64;
        const float spacing=16;
        float minX=tileX*1024,minZ=tileZ*1024+1000;
        var vertices = new Vector3[(cells + 1) * (cells + 1)];
        var indices = new int[cells * cells * 6];
        for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
            {
                float px = minX + x * spacing, pz = minZ + z * spacing;
                vertices[z * (cells + 1) + x] = new(px, world.HeightAt(px, pz), pz);
            }
        int index = 0;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int a = z * (cells + 1) + x, b = a + 1, c = a + cells + 1, d = c + 1;
                indices[index++] = a; indices[index++] = c; indices[index++] = b;
                indices[index++] = b; indices[index++] = c; indices[index++] = d;
            }
        return physics.CreateStaticTriangleMesh($"chase/terrain/{tileX}/{tileZ}",vertices,indices);
    }

    private static float BreakThreshold(RenderItem item)
    {
        if (item.MaterialId == "chase/roof") return 82;
        if (item.MaterialId == "chase/barn") return 108;
        if (item.MaterialId == "chase/trim") return 48;
        if (item.Mesh.Id.EndsWith("cylinder", StringComparison.Ordinal)) return 102;
        if (item.Scale.Y > 4) return 76;
        return item.Scale.Y > 1 ? 64 : 48;
    }

    private static float PartMass(RenderItem item, Vector3 size)
    {
        // The gable is a hollow roof shell, not a solid box of wood.
        if (item.MaterialId == "chase/roof") return Math.Clamp(size.X * size.Z * 4.5f, 30, 950);
        float density = item.MaterialId == "chase/barn" ? 110 : item.MaterialId == "chase/trim" ? 220 : 350;
        return Math.Clamp(size.X * size.Y * size.Z * density, 2, 700);
    }

    private static Vector3f ToEulerDegrees(Matrix4x4 rotation)
    {
        // Renderer order is Rx * Ry * Rz. This is its row-vector inverse.
        float sinY = Math.Clamp(-rotation.M13, -1, 1);
        float y = MathF.Asin(sinY), x, z;
        if (MathF.Abs(MathF.Cos(y)) > .00001f)
        {
            x = MathF.Atan2(rotation.M23, rotation.M33);
            z = MathF.Atan2(rotation.M12, rotation.M11);
        }
        else
        {
            x = MathF.Atan2(MathF.Sign(sinY) * rotation.M21, rotation.M22);
            z = 0;
        }
        return new(x * 180 / MathF.PI, y * 180 / MathF.PI, z * 180 / MathF.PI);
    }

    private static float HorizontalDistanceSquared(Vector3f a, Vector3f b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
    private static Vector3 ToNative(Vector3f value) => new(value.X, value.Y, value.Z);
    private static Vector3f ToRender(Vector3 value) => new(value.X, value.Y, value.Z);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static float Smooth(float low, float high, float value)
    {
        float amount = Math.Clamp((value - low) / (high - low), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }
    private readonly record struct WindSample(float SpeedMph, Vector3 Velocity, Vector3 Radial, Vector3 Tangent,
        Vector3 AxisVelocity, float Radius, float Distance, float PeakMetersPerSecond, float CoreEnvelope, float CaptureEnvelope);
    private sealed class Part(RenderItem original, Vector3 size, float breakWindMph, float mass)
    {
        public RenderItem Original { get; } = original;
        public Vector3 Size { get; } = size;
        public float BreakWindMph { get; } = breakWindMph;
        public float Mass { get; } = mass;
        public PhysicsBody Body { get; set; } = null!;
        public bool HasBody {get;set;}
        public bool IsDynamic {get;set;}
        public RenderItem? FinalItem {get;set;}
        public float StillSeconds {get;set;}
        public bool IsReleased { get; set; }
        public bool IsCollected {get;set;}
        public StormChaseDebrisState? ReplicaState {get;set;}
        public Matrix4x4? FinalPose {get;set;}
    }
}
