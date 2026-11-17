using System.Numerics;
using Engine.Physics;
using Engine.Physics.Collision;
using Engine.Physics.Queries;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Drivable native PhysX chassis in the same terrain/building/debris scene.</summary>
internal sealed class StormChaseVehicles : IDisposable
{
    private readonly StormChaseWorld world;
    private readonly StormChaseDestruction destruction;
    private readonly StormChaseCareer career;
    private readonly string nativeBodyId;
    private PhysicsBody body = null!;
    private string vehicleId = "";
    private float throttle, steering, lastYaw, measuredYawRate;
    private float appliedThrottle, appliedSteering, cameraOrbit, lastLookYaw;
    private Vector3 chassisCentre, chassisExtents;
    private Wheel[] wheels = [];
    private readonly Vector3[] contactPoints = new Vector3[4];
    private readonly float[] contactLoads = new float[4];
    private int groundedWheels;
    private float totalSupportForce;
    private bool brake;
    private RenderWeatherState? weather;
    private Vector3 previousPosition;
    private float pendingDelta;
    private bool lastGrounded;
    private float lastAcceleration;
    private Vector3 lastDriveImpulse;
    public object NativeDiagnostics
    {
        get
        {
            destruction.Physics.TryGetBodyPose(body, out var pose);
            var up = Vector3.TransformNormal(Vector3.UnitY, pose);
            return new
            {
                Position, Centre = new Vector3f(body.Position.X, body.Position.Y, body.Position.Z),
                Velocity = new Vector3f(body.Velocity.X, body.Velocity.Y, body.Velocity.Z),
                SpeedMps, YawRadians, Up = new Vector3f(up.X, up.Y, up.Z),
                AnalyticGroundHeight = world.HeightAt(body.Position.X, body.Position.Z),
                GroundClearance = body.Position.Y - world.HeightAt(body.Position.X, body.Position.Z),
                Grounded = lastGrounded, Acceleration = lastAcceleration,
                GroundedWheels = groundedWheels, SupportForce = totalSupportForce,
                ChassisLocalCentre = ToRender(chassisCentre), ChassisExtents = ToRender(chassisExtents),
                WheelContacts = contactPoints.Select(ToRender).ToArray(), WheelLoads = contactLoads.ToArray(),
                DriveImpulse = new Vector3f(lastDriveImpulse.X, lastDriveImpulse.Y, lastDriveImpulse.Z),
                throttle, steering, brake, Driving, Anchored,
                Fuel = career.Condition.FuelLiters, Health = career.Condition.Health,
                NativePose = new[] { pose.M11, pose.M12, pose.M13, pose.M14, pose.M21, pose.M22, pose.M23, pose.M24,
                    pose.M31, pose.M32, pose.M33, pose.M34, pose.M41, pose.M42, pose.M43, pose.M44 },
            };
        }
    }
    public bool Driving { get; private set; }
    public bool Anchored { get; private set; }
    internal PhysicsBody NativeBody=>body;
    public float SpeedMps => new Vector2(body.Velocity.X, body.Velocity.Z).Length();
    public float SpeedMph => SpeedMps / .44704f;
    public Vector3f Position => destruction.Physics.TryGetBodyPose(body, out var pose)
        ? ToRender(Vector3.Transform(-chassisCentre, pose)) : ToRender(body.Position - chassisCentre);
    public float YawRadians
    {
        get
        {
            if (!destruction.Physics.TryGetBodyPose(body, out var pose)) return lastYaw;
            var forward = Vector3.TransformNormal(Vector3.UnitZ, pose);
            return MathF.Atan2(forward.X, forward.Z);
        }
    }
    public StormChaseVehicles(StormChaseWorld world, StormChaseDestruction destruction, StormChaseCareer career,string stableBodyId="career/vehicle")
    {
        this.world = world; this.destruction = destruction; this.career = career;
        ArgumentException.ThrowIfNullOrWhiteSpace(stableBodyId);nativeBodyId=stableBodyId;
        var station = world.GasStations[0];
        Spawn(station.ParkingPosition);
    }
    public void Spawn(Vector3f feet)
    {
        if (body is not null) destruction.Physics.RemoveBody(body);
        vehicleId = career.SelectedVehicle.Id;
        ConfigureAuthoredCollision();
        var position = new Vector3(feet.X, world.HeightAt(feet.X, feet.Z) + .04f, feet.Z) + chassisCentre;
        destruction.EnsurePhysicsAround(new(position.X, position.Y, position.Z));
        body = destruction.Physics.CreateBody(new(nativeBodyId, position, Vector3.Zero, career.SelectedVehicle.MassKg,
            new BoxColliderShape(chassisExtents), PhysicsBodyType.Dynamic));
        destruction.Physics.SetBodyDamping(body, .05f, 2.2f);
        destruction.Physics.SetSpeculativeCollisionDetection(body, true);
        previousPosition = body.Position; lastYaw = 0; measuredYawRate = 0; Anchored = false;
        appliedThrottle = appliedSteering = 0;
    }
    public void SwitchIfChanged(Vector3f? pickupPosition = null, bool collectFromGarage = false)
    {
        if (vehicleId != career.SelectedVehicle.Id || collectFromGarage) Spawn(pickupPosition ?? Position);
    }
    public void SetInput(float forward, float turn, bool handbrake, RenderWeatherState? frameWeather, float delta)
    {
        throttle = Driving && !career.ShopOpen && !career.Refueling && float.IsFinite(forward) ? Math.Clamp(forward, -1, 1) : 0;
        steering = Driving && !career.ShopOpen && float.IsFinite(turn) ? Math.Clamp(turn, -1, 1) : 0;
        brake = handbrake || !Driving || career.ShopOpen || career.Refueling || Anchored;
        weather = frameWeather;
        pendingDelta = float.IsFinite(delta) ? Math.Max(0, delta) : 0;
    }
    public void StepForces(float delta)
    {
        if (!float.IsFinite(delta) || delta <= 0) return;
        if (!destruction.Physics.TryGetBodyPose(body, out var pose)) throw new InvalidOperationException("Chase vehicle lost its native PhysX body.");
        var forward = Vector3.TransformNormal(Vector3.UnitZ, pose);
        forward.Y = 0; forward = forward.LengthSquared() > .01f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        var right = new Vector3(forward.Z, 0, -forward.X);
        float speed = Vector3.Dot(body.Velocity, forward);
        float lateral = Vector3.Dot(body.Velocity, right);
        var spec = career.SelectedVehicle;
        var up = Vector3.TransformNormal(Vector3.UnitY, pose);
        float yaw = MathF.Atan2(forward.X, forward.Z);
        measuredYawRate = MathF.IEEERemainder(yaw - lastYaw, MathF.Tau) / delta;
        lastYaw = yaw;
        var supportNormal = ApplySuspension(pose, spec.MassKg, delta, up);
        bool onGround = groundedWheels >= 2 && totalSupportForce > spec.MassKg * 9.81f * .1f && up.Y > .35f;
        lastGrounded = onGround;
        appliedThrottle += (throttle - appliedThrottle) * (1 - MathF.Exp(-delta * 7));
        appliedSteering += (steering - appliedSteering) * (1 - MathF.Exp(-delta * 9));
        float acceleration = career.Condition.FuelLiters > .001f && career.Condition.Health > 0 && !Anchored ? appliedThrottle * 7.5f : 0;
        if (speed >= spec.TopSpeedMps && acceleration > 0 || speed < -9 && acceleration < 0) acceleration = 0;
        if (brake) acceleration += Math.Clamp(-speed * 5, -16, 16);
        else acceleration -= speed * MathF.Abs(speed) * .0014f;
        lastAcceleration = acceleration;
        lastDriveImpulse = Vector3.Zero;
        if (onGround)
        {
            var driveForward = forward - supportNormal * Vector3.Dot(forward, supportNormal);
            driveForward = driveForward.LengthSquared() > .01f ? Vector3.Normalize(driveForward) : forward;
            var driveRight = Vector3.Normalize(Vector3.Cross(supportNormal, driveForward));
            float tractionAcceleration = Math.Max(2, totalSupportForce / spec.MassKg * 1.15f);
            var requestedAcceleration = driveForward * acceleration - driveRight * lateral * 7;
            if (requestedAcceleration.Length() > tractionAcceleration)
                requestedAcceleration = Vector3.Normalize(requestedAcceleration) * tractionAcceleration;
            lastDriveImpulse = requestedAcceleration * spec.MassKg * delta;
            body.ApplyImpulse(lastDriveImpulse);
            float desiredYawRate = appliedSteering * Math.Clamp(speed / 12, -1, 1) * .55f;
            // Native torque controls the yaw rate; collisions, pitch and roll remain native.
            float yawInertia = spec.MassKg * (chassisExtents.X * chassisExtents.X + chassisExtents.Z * chassisExtents.Z) / 3;
            destruction.Physics.ApplyAngularImpulse(body, supportNormal * (desiredYawRate - measuredYawRate) * yawInertia * .42f);
        }
        var windPosition = new Vector3f(body.Position.X, body.Position.Y, body.Position.Z);
        var wind = StormChaseDestruction.SampleWindVelocity(windPosition, weather);
        float mph = StormChaseDestruction.SampleWindSpeedMph(windPosition, weather);
        // The storm's own circulation (RFD surge, outflow, inflow) acts on the vehicle outside tornadoes too.
        var storm = StormChaseStormFields.Sample(windPosition, weather?.ActiveStorms ?? []);
        wind += storm.Wind;
        // Hail: stones under ~0.75 in bounce off; larger ones dent panels and crack glass. The interceptor is armoured.
        if (storm.Hail01 > 0)
            career.Condition.Health = Math.Max(0, career.Condition.Health - storm.Hail01 * MathF.Max(0, storm.HailSizeInches - .75f)
                * (vehicleId == "interceptor" ? .25f : .9f) * delta);
        var relative = wind - body.Velocity;
        float protection = Anchored ? .08f : vehicleId == "interceptor" ? .35f : .8f;
        float aerodynamic = Math.Min(180000, .5f * 1.225f * 5.5f * relative.LengthSquared() * protection);
        if (relative.LengthSquared() > 1) body.ApplyImpulse(Vector3.Normalize(relative) * aerodynamic * delta);
        float threshold = Anchored ? 285 : spec.WindToleranceMph;
        if (mph > threshold)
        {
            float severity = Math.Clamp((mph - threshold) / 70, 0, 3);
            body.ApplyImpulse(Vector3.UnitY * spec.MassKg * 9.81f * severity * .75f * delta);
            destruction.Physics.ApplyAngularImpulse(body, Vector3.Cross(forward, wind) * spec.MassKg * delta * .04f);
            career.Condition.Health = Math.Max(0, career.Condition.Health - severity * 4 * delta);
        }
        if (Anchored && onGround)
            body.ApplyImpulse(-new Vector3(body.Velocity.X, 0, body.Velocity.Z) * spec.MassKg * Math.Min(1, 25 * delta));
    }

    private Vector3 ApplySuspension(Matrix4x4 pose, float mass, float delta, Vector3 up)
    {
        groundedWheels = 0;
        totalSupportForce = 0;
        Array.Clear(contactPoints);
        Array.Clear(contactLoads);
        if (up.Y <= .35f)
        {
            foreach (var wheel in wheels) wheel.PreviousContactDistance = float.NaN;
            return Vector3.UnitY;
        }
        float spring = mass * 35 / 4;
        float damping = 2 * MathF.Sqrt(spring * mass / 4) * .7f;
        var linearVelocity = body.Velocity;
        for (int index = 0; index < wheels.Length; index++)
        {
            var wheel = wheels[index];
            var rayStart = Vector3.Transform(wheel.RayLocalOrigin - chassisCentre, pose);
            if (!destruction.Physics.TryRaycast(new PhysicsRay(rayStart, -Vector3.UnitY), wheel.RestDistance + .38f, out var contact)
                || contact.BodyId == body.Id)
            {
                wheel.PreviousContactDistance = float.NaN;
                continue;
            }
            contactPoints[index] = contact.Point;
            groundedWheels++;
            var lever = rayStart - body.Position;
            // Contact-distance velocity includes the terrain rising beneath a
            // moving wheel. Damping world Y speed would incorrectly compress
            // all four springs whenever the car drives up a gentle hill.
            float pointVerticalSpeed = float.IsFinite(wheel.PreviousContactDistance)
                ? (contact.Distance - wheel.PreviousContactDistance) / delta : linearVelocity.Y;
            wheel.PreviousContactDistance = contact.Distance;
            float load = Math.Clamp(mass * 9.81f / 4 + (wheel.RestDistance - contact.Distance) * spring
                - pointVerticalSpeed * damping, 0, mass * 9.81f * 1.25f);
            contactLoads[index] = load;
            totalSupportForce += load;
            var impulse = Vector3.UnitY * load * delta;
            body.ApplyImpulse(impulse);
            destruction.Physics.ApplyAngularImpulse(body, Vector3.Cross(lever, impulse));
        }
        var supported = Enumerable.Range(0, wheels.Length).Where(index => contactLoads[index] > 0).ToArray();
        if (supported.Length < 3) return Vector3.UnitY;
        // Four native terrain/contact samples define the slope underneath the
        // real wheels. Wheel springs support the chassis; its box never scrapes
        // the ground as a substitute for rolling tires.
        var lateral = contactPoints[supported[2]] - contactPoints[supported[0]];
        var longitudinal = contactPoints[supported[1]] - contactPoints[supported[0]];
        var normal = Vector3.Cross(longitudinal, lateral);
        if (normal.LengthSquared() < .01f) return Vector3.UnitY;
        normal = Vector3.Normalize(normal);
        return normal.Y < 0 ? -normal : normal;
    }

    private void ConfigureAuthoredCollision()
    {
        var parts = world.Models.GetParts(vehicleId);
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var part in parts.Where(part => !part.Name.StartsWith("tire", StringComparison.Ordinal)
            && !part.Name.StartsWith("hub", StringComparison.Ordinal) && !part.Name.StartsWith("weather-", StringComparison.Ordinal)
            && !part.Name.StartsWith("mirror", StringComparison.Ordinal)
            // Flexible rubber mud flaps hang low; they must not lower the rigid chassis box toward the ground.
            && !part.Name.StartsWith("mud-flap", StringComparison.Ordinal)))
        {
            var rotation = Matrix4x4.CreateRotationX(part.LocalRotationRadians.X)
                * Matrix4x4.CreateRotationY(part.LocalRotationRadians.Y) * Matrix4x4.CreateRotationZ(part.LocalRotationRadians.Z);
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3((corner & 1) == 0 ? -.5f : .5f, (corner & 2) == 0 ? -.5f : .5f, (corner & 4) == 0 ? -.5f : .5f);
                offset *= ToNative(part.SizeMeters);
                var point = Vector3.TransformNormal(offset, rotation) + ToNative(part.LocalPositionMeters);
                minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
            }
        }
        chassisCentre = (minimum + maximum) * .5f;
        chassisExtents = (maximum - minimum) * .5f;
        wheels = parts.Where(part => part.Name.StartsWith("tire", StringComparison.Ordinal)).OrderBy(part => part.LocalPositionMeters.X)
            .ThenBy(part => part.LocalPositionMeters.Z).Select(part =>
            {
                var hub = ToNative(part.LocalPositionMeters);
                float radius = Math.Max(part.SizeMeters.X, part.SizeMeters.Z) * .5f;
                float rayHeight = Math.Min(hub.Y, minimum.Y - .04f);
                return new Wheel(new(hub.X, rayHeight, hub.Z), Math.Max(.05f, radius - (hub.Y - rayHeight)));
            }).ToArray();
        if (wheels.Length != 4) throw new InvalidDataException("A chase vehicle must have four Blender-authored tires.");
    }
    private double lastSeconds;
    public void AfterPhysics(double seconds)
    {
        lastSeconds = seconds;
        float traveled = Vector3.Distance(previousPosition, body.Position);
        if (Driving && traveled < 100)
            career.RecordDriving(traveled, SpeedMps, StormChaseDestruction.SampleWindSpeedMph(Position, weather), pendingDelta);
        previousPosition = body.Position;
        career.UpdateFuel(pendingDelta, Position, SpeedMps, seconds);
    }
    public bool Interact(FlyCamera camera, double seconds)
    {
        if (Driving)
        {
            if (SpeedMps > 2) { career.Notify("Stop the vehicle before exiting.", seconds); return false; }
            Driving = false; career.Refueling = false; Anchored = false;
            float yaw = YawRadians;
            float x = Position.X - MathF.Cos(yaw) * 3.1f, z = Position.Z + MathF.Sin(yaw) * 3.1f;
            camera.Grounded = true; camera.SetSpeed(6);
            camera.SetPosition(new(x, world.HeightAt(x, z) + 1.72f, z));
            career.Notify("On foot. B deploys a probe; J retrieves it; E enters the vehicle.", seconds);
            return true;
        }
        if (StormChaseCareer.HorizontalDistance(Position, camera.ToCameraState().Position) > 7)
        { career.Notify("Move beside your vehicle to enter it.", seconds); return false; }
        Driving = true; camera.Grounded = false; career.ShopOpen = false;
        cameraOrbit = 0;
        lastLookYaw = MathF.Atan2(camera.Forward.X, camera.Forward.Z);
        career.Notify("WASD drive; Space brakes; E exits; R refuels at a station; K anchors the interceptor.", seconds); return true;
    }
    public void ToggleRefuel(double seconds)
    {
        if (!career.AtServiceStation(Position) || SpeedMps > .5f)
        { career.Notify("Park at a city or town service station before refueling.", seconds); return; }
        career.Refueling = !career.Refueling;
        career.Notify(career.Refueling ? "Refueling at $1.35 per litre. R stops the pump." : "Refueling stopped.", seconds);
    }
    public void ExitForCameraMode() { Driving = false; Anchored = false; career.Refueling = false; }
    public void ToggleAnchors(double seconds)
    {
        if (!Driving || vehicleId != "interceptor") { career.Notify("The armored interceptor has deployable ground anchors.", seconds); return; }
        if (SpeedMps > 1) { career.Notify("Stop before deploying ground anchors.", seconds); return; }
        Anchored = !Anchored; career.Notify(Anchored ? "INTERCEPTOR ANCHORED. K retracts anchors." : "Anchors retracted.", seconds);
    }
    public void Recover(FlyCamera camera, double seconds)
    {
        var station = career.NearestStation(Position);
        if (station is null) { career.Notify("All service stations are damaged. Recovery is unavailable.", seconds); return; }
        career.Tow(camera.ToCameraState().Position, seconds);
        Spawn(station.ParkingPosition);
        Driving = false;
        camera.Grounded = true; camera.SetSpeed(6);
        camera.SetPosition(new(Position.X - 3, world.HeightAt(Position.X - 3, Position.Z) + 1.72f, Position.Z));
    }
    public CameraState Camera(FlyCamera look)
    {
        var lookForward = look.Forward;
        float lookYaw = MathF.Atan2(lookForward.X, lookForward.Z);
        cameraOrbit = MathF.IEEERemainder(cameraOrbit + MathF.IEEERemainder(lookYaw - lastLookYaw, MathF.Tau), MathF.Tau);
        lastLookYaw = lookYaw;
        float heading = YawRadians + cameraOrbit;
        float horizontal = MathF.Sqrt(Math.Max(0, 1 - lookForward.Y * lookForward.Y));
        var ahead = new Vector3(MathF.Sin(heading) * horizontal, lookForward.Y, MathF.Cos(heading) * horizontal);
        // A chase camera remains independently steerable with the mouse.
        var pivot = ToNative(Position) + Vector3.UnitY * 2.2f;
        var position = pivot - ahead * 7;
        position.Y = Math.Max(position.Y, world.HeightAt(position.X, position.Z) + 1);
        return new(new(position.X, position.Y, position.Z), new(ahead.X, ahead.Y, ahead.Z), 66, .15f, 16000);
    }

    public IReadOnlyList<RenderLight> CreateHeadlights()
    {
        if (!Driving || !destruction.Physics.TryGetBodyPose(body, out var pose)) return Array.Empty<RenderLight>();
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, pose));
        var color = new ColorRgba(1, .93f, .79f, 1);
        var lights = new[] { -.7f, .7f }.Select((x, index) => new RenderLight("vehicle-headlight-" + index, RenderLightType.Spot,
            ToRender(Vector3.Transform(new Vector3(x, 1, 2.3f) - chassisCentre, pose)), ToRender(forward),
            color, 1800, 150, InnerConeAngleDegrees: 15, OuterConeAngleDegrees: 27, CastsShadow: false)).ToList();
        // Department vehicles flash their beacons, alternating sides like a real light bar.
        lights.AddRange(StormChaseEmergencyLights.Create(world.Models, vehicleId, local => ToRender(Vector3.Transform(ToNative(local) - chassisCentre, pose)), lastSeconds));
        return lights;
    }
    public IReadOnlyList<RenderItem> CreateRenderItems()
    {
        var items = world.Models.CreateItems(vehicleId, Position, YawRadians, nativeBodyId);
        if (!destruction.Physics.TryGetBodyPose(body, out var pose)) return items;
        // Preserve native chassis pitch and roll on the authored model parts.
        var rotation = pose; rotation.Translation = Vector3.Zero;
        return items.Select(item =>
        {
            float yaw = YawRadians;
            var relative = new Vector3(item.Position.X - Position.X, item.Position.Y - Position.Y, item.Position.Z - Position.Z);
            relative = Vector3.TransformNormal(relative, Matrix4x4.CreateRotationY(-yaw));
            relative = Vector3.TransformNormal(relative, rotation);
            var itemEuler = item.RotationEulerDegrees ?? new Vector3f(0, item.RotationYawDegrees, 0);
            var localRotation = Matrix4x4.CreateRotationX(itemEuler.X * MathF.PI / 180)
                * Matrix4x4.CreateRotationY(itemEuler.Y * MathF.PI / 180)
                * Matrix4x4.CreateRotationZ(itemEuler.Z * MathF.PI / 180)
                * Matrix4x4.CreateRotationY(-yaw);
            return item with { Position = new(Position.X + relative.X, Position.Y + relative.Y, Position.Z + relative.Z),
                RotationYawDegrees = 0, RotationEulerDegrees = Euler(localRotation * rotation), AllowStaticInstancing = false };
        }).ToArray();
    }
    private static Vector3f Euler(Matrix4x4 rotation)
    {
        float sy = Math.Clamp(-rotation.M13, -1, 1), y = MathF.Asin(sy);
        float x = MathF.Abs(MathF.Cos(y)) > .00001f ? MathF.Atan2(rotation.M23, rotation.M33) : MathF.Atan2(MathF.Sign(sy) * rotation.M21, rotation.M22);
        float z = MathF.Abs(MathF.Cos(y)) > .00001f ? MathF.Atan2(rotation.M12, rotation.M11) : 0;
        return new(x * 180 / MathF.PI, y * 180 / MathF.PI, z * 180 / MathF.PI);
    }
    private static Vector3 ToNative(Vector3f value) => new(value.X, value.Y, value.Z);
    private static Vector3f ToRender(Vector3 value) => new(value.X, value.Y, value.Z);
    private sealed record Wheel(Vector3 RayLocalOrigin, float RestDistance)
    {
        public float PreviousContactDistance { get; set; } = float.NaN;
    }
    public void Dispose() { if (body is not null) destruction.Physics.RemoveBody(body); }
}

/// <summary>Flashing emergency beacons of department vehicles, placed at the model's authored "beacon" parts.</summary>
internal static class StormChaseEmergencyLights
{
    /// <param name="toWorld">Maps a model-local position (metres) to the world.</param>
    /// <returns>Two non-shadowing point lights at the leftmost and rightmost beacons, flashing in alternation
    /// (about 2.5 Hz). The renderer budgets 16 nearby lights per object, so this stays well inside it.</returns>
    public static IReadOnlyList<RenderLight> Create(StormChaseModelLibrary models, string vehicleId, Func<Vector3f, Vector3f> toWorld, double seconds)
    {
        if (!StormChaseJobs.IsDutyVehicle(vehicleId)) return [];
        var beacons = models.GetParts(vehicleId).Where(part => part.Name.StartsWith("beacon", StringComparison.Ordinal)
            || part.Name.StartsWith("module-beacon", StringComparison.Ordinal)).ToArray();
        if (beacons.Length == 0) return [];
        float phase = (float)((seconds * 2.5) % 1);
        var lights = new List<RenderLight>(2);
        foreach (var (part, on) in new[] { (beacons.MinBy(p => p.LocalPositionMeters.X)!, phase < .5f), (beacons.MaxBy(p => p.LocalPositionMeters.X)!, phase >= .5f) })
        {
            var color = part.Material.Id.EndsWith("beacon-blue", StringComparison.Ordinal) ? new ColorRgba(.15f, .32f, 1, 1)
                : part.Material.Id.EndsWith("lamp", StringComparison.Ordinal) ? new ColorRgba(1, .95f, .85f, 1) : new ColorRgba(1, .08f, .05f, 1);
            // The off side keeps a dim glow so the bar never goes fully dark between flashes.
            lights.Add(new RenderLight("vehicle-beacon-" + lights.Count, RenderLightType.Point, toWorld(part.LocalPositionMeters + new Vector3f(0, .25f, 0)),
                new(0, -1, 0), color, on ? 10 : 1.2f, 28, CastsShadow: false));
        }
        return lights;
    }
}
