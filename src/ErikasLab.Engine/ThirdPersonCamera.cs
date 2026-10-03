using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2G third-person follow/orbit camera. Portable: it owns only orbit
/// angles, follow distance, look-at height, and the smoothed camera position,
/// and writes the final view state into <see cref="CameraState"/>. It observes
/// the target's authoritative world position; it never owns or mutates
/// gameplay movement or facing (that stays in <c>GameSession</c>).
///
/// Model (all System.Numerics):
/// <code>
///   target        = targetWorldPosition + (0, TargetHeightOffset, 0)
///   orbitForward  = ForwardFromOrbit(OrbitYawRadians, OrbitPitchRadians)
///   desiredPos    = target - orbitForward * Distance
///   camera.Position = smoothed(desiredPos)
///   camera.Forward  = orbitForward
/// </code>
/// The orientation is the player-controlled orbit basis (not an auto look-at),
/// so WASD stays camera-relative and does not rotate while the position
/// catches up. At rest <c>camera.Forward</c> equals
/// <c>normalize(target - camera.Position)</c>; during lateral follow lag the
/// two differ by a few degrees until the camera settles.
///
/// Follow smoothing is exponential and elapsed-time based
/// (<c>alpha = 1 - exp(-rate * dt)</c>): frame-rate independent, deterministic,
/// bounded, overshoot-free, and allocation-free. Very large or clearly
/// discontinuous target changes snap instead of flying across the world.
/// </summary>
public sealed class ThirdPersonCamera
{
    /// <summary>Initial follow distance in meters (behind and above Erika).</summary>
    public const float DefaultDistanceMeters = 4.5f;

    /// <summary>Initial vertical look-at offset (upper torso/head), in meters.</summary>
    public const float DefaultTargetHeightOffsetMeters = 1.25f;

    /// <summary>
    /// Initial orbit yaw (radians). 0 looks along the Phase 1 forward (-Z) and
    /// places the camera behind the initial movement heading.
    /// </summary>
    public const float DefaultOrbitYawRadians = 0f;

    /// <summary>Initial orbit pitch (radians): slightly above, looking down.</summary>
    public const float DefaultOrbitPitchRadians = -0.28f;

    /// <summary>
    /// Centralized follow-response rate (1/s) for exponential smoothing. The
    /// time constant is 1/rate (~0.10 s); larger is snappier.
    /// </summary>
    public const float DefaultFollowSmoothingRatePerSecond = 10f;

    /// <summary>
    /// Discontinuity threshold (meters). A target jump farther than this snaps
    /// the camera instead of smoothing across the world.
    /// </summary>
    public const float DefaultSnapDistanceMeters = 25f;

    /// <summary>Preserved Phase 1 mouse-look sensitivity (radians per pixel).</summary>
    public const float DefaultMouseLookSensitivity = 0.0025f;

    /// <summary>Preserved Phase 1 arrow-key look speed (radians per second).</summary>
    public const float DefaultKeyboardLookSpeed = 1.7f;

    /// <summary>Lowest pitch (~-68.8 deg): camera high above, looking down.</summary>
    public const float MinPitchRadians = -1.2f;

    /// <summary>Highest pitch (~+28.6 deg): camera low, looking up.</summary>
    public const float MaxPitchRadians = 0.5f;

    public ThirdPersonCamera(
        float distanceMeters = DefaultDistanceMeters,
        float targetHeightOffsetMeters = DefaultTargetHeightOffsetMeters,
        float orbitYawRadians = DefaultOrbitYawRadians,
        float orbitPitchRadians = DefaultOrbitPitchRadians,
        float followSmoothingRatePerSecond = DefaultFollowSmoothingRatePerSecond,
        float snapDistanceMeters = DefaultSnapDistanceMeters,
        float mouseLookSensitivity = DefaultMouseLookSensitivity,
        float keyboardLookSpeed = DefaultKeyboardLookSpeed)
    {
        Distance = distanceMeters;
        TargetHeightOffset = targetHeightOffsetMeters;
        FollowSmoothingRatePerSecond = followSmoothingRatePerSecond;
        SnapDistanceMeters = snapDistanceMeters;
        MouseLookSensitivity = mouseLookSensitivity;
        KeyboardLookSpeed = keyboardLookSpeed;
        SetOrbit(orbitYawRadians, orbitPitchRadians);
    }

    public float Distance { get; }

    public float TargetHeightOffset { get; }

    public float FollowSmoothingRatePerSecond { get; }

    public float SnapDistanceMeters { get; }

    public float MouseLookSensitivity { get; }

    public float KeyboardLookSpeed { get; }

    /// <summary>Horizontal orbit angle (radians), wrapped to (-pi, pi].</summary>
    public float OrbitYawRadians { get; private set; }

    /// <summary>Vertical orbit angle (radians), clamped to the pitch limits.</summary>
    public float OrbitPitchRadians { get; private set; }

    /// <summary>Smoothed world-space camera position.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>True once a follow/snap has established a finite position.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Orbit forward basis (unit length), matching <see cref="CameraState.Forward"/>.
    /// Yaw 0 / pitch 0 looks along -Z; negative pitch looks down.
    /// </summary>
    public static Vector3 ForwardFromOrbit(float yawRadians, float pitchRadians)
    {
        var cosPitch = MathF.Cos(pitchRadians);
        return new Vector3(
            MathF.Sin(yawRadians) * cosPitch,
            MathF.Sin(pitchRadians),
            -MathF.Cos(yawRadians) * cosPitch);
    }

    /// <summary>
    /// Set the orbit angles directly (spawn/session setup and tests). Yaw is
    /// wrapped and pitch clamped; non-finite yaw maps to 0.
    /// </summary>
    public void SetOrbit(float yawRadians, float pitchRadians)
    {
        OrbitYawRadians = YawSmoothing.WrapToPi(yawRadians);
        OrbitPitchRadians = Math.Clamp(pitchRadians, MinPitchRadians, MaxPitchRadians);
    }

    /// <summary>
    /// Apply mouse/arrow look input to the orbit. Yaw wraps cleanly; pitch is
    /// clamped; elapsed-time based, so frame-rate independent.
    /// </summary>
    public void UpdateOrbit(InputState input, FrameTime frameTime)
    {
        var deltaSeconds = Math.Max(0, frameTime.DeltaSeconds);
        var keyboardYaw = (input.LookRight ? 1 : 0) - (input.LookLeft ? 1 : 0);
        var keyboardPitch = (input.LookUp ? 1 : 0) - (input.LookDown ? 1 : 0);
        var yawDelta = input.MouseDelta.X * MouseLookSensitivity
            + keyboardYaw * KeyboardLookSpeed * (float)deltaSeconds;
        var pitchDelta = -input.MouseDelta.Y * MouseLookSensitivity
            + keyboardPitch * KeyboardLookSpeed * (float)deltaSeconds;
        SetOrbit(OrbitYawRadians + yawDelta, OrbitPitchRadians + pitchDelta);
    }

    /// <summary>World-space point the camera looks toward (target + height offset).</summary>
    public Vector3 TargetPoint(Vector3 targetWorldPosition) =>
        targetWorldPosition + new Vector3(0f, TargetHeightOffset, 0f);

    /// <summary>Unsmoothed desired camera position for the current orbit/target.</summary>
    public Vector3 DesiredPosition(Vector3 targetWorldPosition) =>
        TargetPoint(targetWorldPosition) - ForwardFromOrbit(OrbitYawRadians, OrbitPitchRadians) * Distance;

    /// <summary>Orbit from look input, then follow the target (one convenient call).</summary>
    public void Update(CameraState camera, Vector3 targetWorldPosition, InputState input, FrameTime frameTime)
    {
        UpdateOrbit(input, frameTime);
        Follow(camera, targetWorldPosition, frameTime);
    }

    /// <summary>
    /// Advance the smoothed camera position toward the desired follow position
    /// and write position + orientation into <paramref name="camera"/>. Snaps on
    /// first use, on non-finite state, or when the discontinuity threshold is
    /// exceeded; zero elapsed time produces no movement.
    /// </summary>
    public void Follow(CameraState camera, Vector3 targetWorldPosition, FrameTime frameTime)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var desired = DesiredPosition(targetWorldPosition);
        var deltaSeconds = Math.Max(0, frameTime.DeltaSeconds);

        if (!IsInitialized || !IsFinite(Position) || Vector3.Distance(Position, desired) > SnapDistanceMeters)
        {
            Position = desired;
            IsInitialized = true;
        }
        else if (deltaSeconds > 0)
        {
            var alpha = 1f - MathF.Exp(-FollowSmoothingRatePerSecond * (float)deltaSeconds);
            Position += (desired - Position) * alpha;
        }

        camera.Position = Position;
        camera.SetLook(OrbitYawRadians, OrbitPitchRadians);
    }

    /// <summary>Force the next <see cref="Follow"/> to snap (session reset/teleport).</summary>
    public void Reset() => IsInitialized = false;

    /// <summary>Snap immediately to the desired framing (spawn/session load).</summary>
    public void SnapToTarget(CameraState camera, Vector3 targetWorldPosition)
    {
        Reset();
        Follow(camera, targetWorldPosition, default);
    }

    /// <summary>One-line startup diagnostic describing the active camera policy.</summary>
    public string DescribePolicy() =>
        $"third-person follow: distance {Distance:F2} m, target height {TargetHeightOffset:F2} m, " +
        $"orbit yaw {OrbitYawRadians:F2} rad pitch {OrbitPitchRadians:F2} rad " +
        $"limits [{MinPitchRadians:F2}, {MaxPitchRadians:F2}], " +
        $"follow rate {FollowSmoothingRatePerSecond:F1}/s, snap {SnapDistanceMeters:F0} m, " +
        $"mouse sensitivity {MouseLookSensitivity}";

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
