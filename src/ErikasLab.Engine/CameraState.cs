using System.Numerics;

namespace ErikasLab.Engine;

public sealed class CameraState
{
    public CameraState(Vector3 position, float yawRadians = 0, float pitchRadians = 0, float fieldOfViewRadians = MathF.PI / 3, float nearClip = 0.1f, float farClip = 200)
    {
        if (fieldOfViewRadians <= 0 || fieldOfViewRadians >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewRadians));
        }

        if (nearClip <= 0 || farClip <= nearClip)
        {
            throw new ArgumentOutOfRangeException(nameof(nearClip), "Clip planes must be positive and farClip must be greater than nearClip.");
        }

        Position = position;
        YawRadians = yawRadians;
        PitchRadians = Math.Clamp(pitchRadians, -MaxPitchRadians, MaxPitchRadians);
        FieldOfViewRadians = fieldOfViewRadians;
        NearClip = nearClip;
        FarClip = farClip;
        AspectRatio = 16f / 9f;
    }

    private const float MaxPitchRadians = MathF.PI / 2 - 0.05f;

    public Vector3 Position { get; set; }

    public float YawRadians { get; set; }

    public float PitchRadians { get; private set; }

    public float FieldOfViewRadians { get; }

    public float NearClip { get; }

    public float FarClip { get; }

    public float AspectRatio { get; private set; }

    public Vector3 Forward
    {
        get
        {
            var cosPitch = MathF.Cos(PitchRadians);
            return Vector3.Normalize(new Vector3(
                MathF.Sin(YawRadians) * cosPitch,
                MathF.Sin(PitchRadians),
                -MathF.Cos(YawRadians) * cosPitch));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewRadians, AspectRatio, NearClip, FarClip);

    public void SetViewportSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        AspectRatio = (float)width / height;
    }

    public void Rotate(float yawDeltaRadians, float pitchDeltaRadians)
    {
        YawRadians += yawDeltaRadians;
        PitchRadians = Math.Clamp(PitchRadians + pitchDeltaRadians, -MaxPitchRadians, MaxPitchRadians);
    }

    /// <summary>
    /// Set the view orientation directly (Phase 2G third-person orbit). Yaw is
    /// applied verbatim; pitch is clamped to the same safe range as
    /// <see cref="Rotate"/>, so a follow/orbit controller can drive the view
    /// without the free-look delta bookkeeping.
    /// </summary>
    public void SetLook(float yawRadians, float pitchRadians)
    {
        YawRadians = yawRadians;
        PitchRadians = Math.Clamp(pitchRadians, -MaxPitchRadians, MaxPitchRadians);
    }

    /// <summary>
    /// Point the view along an explicit world direction (Phase 2H look-at
    /// targeting). Yaw/pitch are recovered from <paramref name="forward"/> using
    /// the same convention as <see cref="Forward"/> (<c>yaw = atan2(x, -z)</c>,
    /// <c>pitch = asin(y)</c>) so <see cref="Forward"/> reproduces the direction
    /// with no roll. A non-finite or near-zero direction is ignored, retaining
    /// the prior orientation instead of producing NaNs.
    /// </summary>
    public void SetLookDirection(Vector3 forward)
    {
        var lengthSquared = forward.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
        {
            return;
        }

        var direction = Vector3.Normalize(forward);
        YawRadians = MathF.Atan2(direction.X, -direction.Z);
        PitchRadians = Math.Clamp(
            MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)),
            -MaxPitchRadians,
            MaxPitchRadians);
    }
}
