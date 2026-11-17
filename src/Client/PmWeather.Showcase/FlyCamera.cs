using System.Numerics;
using Shared.Contracts.Rendering;

internal sealed class FlyCamera
{
    private const float LookSensitivity = 0.0025f;
    private const float MaximumPitch = 85f * (MathF.PI / 180f);
    private float yaw;
    private float pitch;

    public FlyCamera() => SetPreset(close: true);

    public Vector3 Position { get; private set; }

    public Vector3 Forward
    {
        get
        {
            float horizontal = MathF.Cos(pitch);
            return new(MathF.Sin(yaw) * horizontal, MathF.Sin(pitch), MathF.Cos(yaw) * horizontal);
        }
    }

    public float SpeedMetersPerSecond { get; private set; } = 350f;
    public bool Grounded { get; set; }

    public void SetPose(Vector3 position, Vector3 target)
    {
        Position = position;
        var direction = Vector3.Normalize(target - position);
        yaw = MathF.Atan2(direction.X, direction.Z);
        pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1));
    }

    public void SetPosition(Vector3 position) => Position = position;
    public void SetSpeed(float speed) => SpeedMetersPerSecond = Math.Clamp(speed, 2, 6000);

    public void SetPreset(bool close)
    {
        Position = close ? new(-300f, 85f, 100f) : new(-6000f, 1100f, -7800f);
        Vector3 target = close ? new(0f, 250f, 1000f) : new(1500f, 1500f, 0f);
        Vector3 direction = Vector3.Normalize(target - Position);
        yaw = MathF.Atan2(direction.X, direction.Z);
        pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
    }

    public void Look(float deltaX, float deltaY)
    {
        if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY)) return;
        yaw = MathF.IEEERemainder(yaw + deltaX * LookSensitivity, MathF.Tau);
        pitch = Math.Clamp(pitch - deltaY * LookSensitivity, -MaximumPitch, MaximumPitch);
    }

    public void Move(float deltaSeconds, float forward, float strafe, float vertical, bool boost, bool slow)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f ||
            !float.IsFinite(forward) || !float.IsFinite(strafe) || !float.IsFinite(vertical)) return;

        Vector3 direction = Forward;
        if (Grounded) direction = Vector3.Normalize(new Vector3(direction.X, 0, direction.Z));
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        Vector3 movement = direction * forward + right * strafe + Vector3.UnitY * (Grounded ? 0 : vertical);
        float lengthSquared = movement.LengthSquared();
        if (lengthSquared <= 0.000001f) return;

        float speed = SpeedMetersPerSecond * (boost ? 4f : 1f) * (slow ? 0.25f : 1f);
        Position += movement / MathF.Sqrt(lengthSquared) * speed * MathF.Min(deltaSeconds, 0.25f);
    }

    public void AdjustSpeed(float wheelNotches)
    {
        if (!float.IsFinite(wheelNotches)) return;
        SpeedMetersPerSecond = Math.Clamp(SpeedMetersPerSecond * MathF.Pow(1.25f, wheelNotches), Grounded ? 2 : 10, Grounded ? 8 : 6000);
    }

    public CameraState ToCameraState()
    {
        Vector3 direction = Forward;
        return new(new(Position.X, Position.Y, Position.Z), new(direction.X, direction.Y, direction.Z), 58f, 0.5f, 16000f);
    }
}
