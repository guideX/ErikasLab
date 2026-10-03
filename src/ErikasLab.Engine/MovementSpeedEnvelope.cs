namespace ErikasLab.Engine;

/// <summary>
/// Phase 2I gameplay-owned scalar movement-speed state. A single finite,
/// non-negative meters-per-second value that ramps toward a target using
/// elapsed time. It is a *response filter* only: it never stores a position,
/// velocity vector, or heading, so it can never become a second translation
/// authority. Root displacement remains driven by the active animation clip
/// (see <c>GameSession</c>); this type merely says how much authored
/// displacement is currently consumed.
///
/// Integration is <c>MoveTowards(current, target, rate * dt)</c> with a
/// separate acceleration rate when speeding up and deceleration rate when
/// slowing down. It is elapsed-time based (frame-rate independent), clamped to
/// the target (no overshoot), stable under large or non-finite <c>dt</c>,
/// NaN-safe, and allocation-free.
/// </summary>
public sealed class MovementSpeedEnvelope
{
    /// <summary>
    /// Centralized acceleration (m/s^2). Tuned for a responsive third-person
    /// character rather than a heavy simulation: from rest the walk target
    /// (~1.69 m/s) is reached in ~0.11 s and the run target (~5.59 m/s) in
    /// ~0.35 s.
    /// </summary>
    public const float DefaultAccelerationMetersPerSecondSquared = 16f;

    /// <summary>
    /// Centralized deceleration (m/s^2). Slightly stronger than acceleration so
    /// stops read as deliberate: walk (~1.69 m/s) stops in ~0.07 s and run
    /// (~5.59 m/s) in ~0.23 s.
    /// </summary>
    public const float DefaultDecelerationMetersPerSecondSquared = 24f;

    /// <summary>
    /// Speeds at or below this (m/s) are snapped to exactly zero once the
    /// target is zero, so the locomotion clip can hand off to idle cleanly.
    /// </summary>
    public const float ZeroSpeedThresholdMetersPerSecond = 0.01f;

    /// <summary>Current filtered movement speed (m/s), always finite and >= 0.</summary>
    public float CurrentMetersPerSecond { get; private set; }

    /// <summary>Reset to a non-negative finite speed (invalid input becomes zero).</summary>
    public void Reset(float metersPerSecond)
    {
        CurrentMetersPerSecond = float.IsFinite(metersPerSecond) && metersPerSecond > 0f
            ? metersPerSecond
            : 0f;
    }

    /// <summary>Snap to exactly zero (stop hand-off).</summary>
    public void ClampToZero() => CurrentMetersPerSecond = 0f;

    /// <summary>
    /// Advance <see cref="CurrentMetersPerSecond"/> toward
    /// <paramref name="targetMetersPerSecond"/> by at most one rate-limited
    /// step. Speeding up uses <paramref name="acceleration"/>, slowing down uses
    /// <paramref name="deceleration"/>. Zero or non-finite elapsed time changes
    /// nothing; a non-finite/negative target is treated as zero; the result
    /// never overshoots the target and never goes negative. Returns the new
    /// current speed.
    /// </summary>
    public float Advance(
        float targetMetersPerSecond,
        double deltaSeconds,
        float acceleration = DefaultAccelerationMetersPerSecondSquared,
        float deceleration = DefaultDecelerationMetersPerSecondSquared)
    {
        if (!float.IsFinite(targetMetersPerSecond) || targetMetersPerSecond < 0f)
        {
            targetMetersPerSecond = 0f;
        }

        var dt = (float)deltaSeconds;
        if (!float.IsFinite(dt) || dt <= 0f)
        {
            return CurrentMetersPerSecond;
        }

        var rate = targetMetersPerSecond > CurrentMetersPerSecond ? acceleration : deceleration;
        if (!float.IsFinite(rate) || rate <= 0f)
        {
            return CurrentMetersPerSecond;
        }

        var maxDelta = rate * dt;
        if (!float.IsFinite(maxDelta))
        {
            maxDelta = MathF.Abs(targetMetersPerSecond - CurrentMetersPerSecond);
        }

        CurrentMetersPerSecond = MoveTowards(CurrentMetersPerSecond, targetMetersPerSecond, maxDelta);
        if (!float.IsFinite(CurrentMetersPerSecond) || CurrentMetersPerSecond < 0f)
        {
            CurrentMetersPerSecond = 0f;
        }

        return CurrentMetersPerSecond;
    }

    /// <summary>
    /// Step <paramref name="current"/> toward <paramref name="target"/> by at
    /// most <paramref name="maxDelta"/>, clamping exactly onto the target (no
    /// overshoot). Non-finite inputs fall back to a safe value.
    /// </summary>
    public static float MoveTowards(float current, float target, float maxDelta)
    {
        if (!float.IsFinite(current))
        {
            current = 0f;
        }

        if (!float.IsFinite(target))
        {
            target = current;
        }

        if (!float.IsFinite(maxDelta) || maxDelta <= 0f)
        {
            return current;
        }

        var delta = target - current;
        if (MathF.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + MathF.CopySign(maxDelta, delta);
    }
}
