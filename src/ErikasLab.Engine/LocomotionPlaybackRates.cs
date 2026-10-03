namespace ErikasLab.Engine;

/// <summary>
/// Phase 2K centralized locomotion visual playback-rate math. Given the current
/// Phase 2I movement speed and a locomotion clip's authored steady-state speed,
/// this derives how fast the visible stride should play. The raw rate is
/// intentionally the same ratio as the Phase 2I root-motion gain
/// (<c>currentSpeed / authoredSpeed</c>), so an uncapped pose clock advances in
/// lockstep with world travel and stride mismatch is near zero. The applied
/// rate is bounded so a run-to-walk deceleration (raw ratio up to the
/// run/walk authored ratio, observed ~3.1) cannot drive a comically fast walk
/// cycle.
///
/// This type is pure math: no state, no allocation, and it never touches the
/// root-motion clock. Gameplay/world displacement remains owned by the active
/// clip and the Phase 2I gain.
/// </summary>
public static class LocomotionPlaybackRates
{
    /// <summary>
    /// Minimum visual playback rate. Zero lets a stopped/coasting locomotion
    /// clip freeze rather than advance while world speed is ~0, which is the
    /// core foot-slide fix for idle→walk starts.
    /// </summary>
    public const float MinimumRate = 0f;

    /// <summary>
    /// Maximum visual playback rate. The authored run/walk speed ratio is
    /// ~3.31 and the observed Phase 2I run→walk root gain peaks near 3.08 at
    /// 60 Hz, so an uncapped walk pose would briefly play at ~3x. Capping at
    /// 2x keeps the worst-case walk cadence plausible (double-time) while
    /// leaving only a short, bounded residual slide during the ~0.1 s the
    /// walk clip's gain exceeds 2. A cap of 1.5x would leave a longer and
    /// larger mismatch; 2x is the chosen trade-off between plausibility and
    /// synchronization.
    /// </summary>
    public const float MaximumRate = 2f;

    /// <summary>
    /// Raw (unbounded) visual rate for a locomotion clip:
    /// <c>speed / authoredSpeed</c>. Returns 0 (never NaN/infinity) for a
    /// non-finite or non-positive speed, or a non-finite/non-positive authored
    /// speed (a degenerate clip has no meaningful cadence).
    /// </summary>
    public static float RawRate(float speedMetersPerSecond, float authoredSpeedMetersPerSecond)
    {
        if (!float.IsFinite(speedMetersPerSecond) || speedMetersPerSecond <= 0f)
        {
            return 0f;
        }

        if (!float.IsFinite(authoredSpeedMetersPerSecond) || authoredSpeedMetersPerSecond <= 1e-6f)
        {
            return 0f;
        }

        var raw = speedMetersPerSecond / authoredSpeedMetersPerSecond;
        return float.IsFinite(raw) && raw > 0f ? raw : 0f;
    }

    /// <summary>
    /// Applied visual rate: <see cref="RawRate"/> clamped to
    /// <c>[MinimumRate, MaximumRate]</c>. Always finite and non-negative.
    /// </summary>
    public static float AppliedRate(float speedMetersPerSecond, float authoredSpeedMetersPerSecond) =>
        Math.Clamp(RawRate(speedMetersPerSecond, authoredSpeedMetersPerSecond), MinimumRate, MaximumRate);
}
