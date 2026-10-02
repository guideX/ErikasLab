namespace ErikasLab.Engine;

/// <summary>
/// Deterministic, frame-rate-independent yaw smoothing (Phase 2F). Rotates
/// toward a target heading along the shortest angular path at a caller-supplied
/// constant angular speed, clamping exactly onto the target so it never
/// overshoots. Wrap-aware across the -π/+π boundary and NaN-safe.
/// </summary>
public static class YawSmoothing
{
    public const float TwoPi = MathF.PI * 2f;

    /// <summary>Wrap an angle to (-π, π]. Non-finite input maps to 0.</summary>
    public static float WrapToPi(float radians)
    {
        if (!float.IsFinite(radians))
        {
            return 0f;
        }

        var wrapped = radians % TwoPi;
        if (wrapped <= -MathF.PI)
        {
            wrapped += TwoPi;
        }
        else if (wrapped > MathF.PI)
        {
            wrapped -= TwoPi;
        }

        return wrapped;
    }

    /// <summary>
    /// Step <paramref name="current"/> toward <paramref name="target"/> by at
    /// most <paramref name="maxRadians"/> along the shortest path. Returns the
    /// wrapped target exactly once it is within reach (no overshoot); returns
    /// <paramref name="current"/> unchanged for a non-positive budget or
    /// non-finite input.
    /// </summary>
    public static float StepTowards(float current, float target, float maxRadians)
    {
        if (!float.IsFinite(current) || !float.IsFinite(target) || maxRadians <= 0f)
        {
            return current;
        }

        var delta = WrapToPi(target - current);
        if (MathF.Abs(delta) <= maxRadians)
        {
            return WrapToPi(target);
        }

        return WrapToPi(current + MathF.CopySign(maxRadians, delta));
    }
}
