using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Portable root-motion math for traveling locomotion clips (Phase 2E).
/// The walk/run clips author Hips horizontal travel; this evaluator turns
/// that authored travel into a continuous world-space delta without
/// depending on frame rate, and provides the rendered-pose neutralization
/// so the same travel is not applied twice.
///
/// Loop policy (deterministic, wrap-aware):
/// let start = sample(0), end = sample(duration), net = end - start,
/// loops(t) = floor(t / duration), norm(t) = t - loops(t) * duration,
/// absolute(t) = loops(t) * net + (sample(norm(t)) - start),
/// delta(prev, curr) = absolute(curr) - absolute(prev).
/// N complete loops therefore produce exactly N * net with no discontinuity
/// at modulo wrap. Never compute sample(currNorm) - sample(prevNorm) alone.
/// </summary>
public static class RootMotionEvaluator
{
    /// <summary>
    /// Find the Hips translation channel for <paramref name="hipsBoneIndex"/>,
    /// or null when the clip carries no Hips translation (stationary).
    /// </summary>
    public static AnimationChannel? FindHipsTranslationChannel(AnimationClip clip, int hipsBoneIndex)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (var channel in clip.Channels)
        {
            if (channel.BoneIndex == hipsBoneIndex && channel.HasTranslation)
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>Sampled Hips translation at <paramref name="timeSeconds"/> (clamped to keys).</summary>
    public static Vector3 SampleHipsTranslation(AnimationClip clip, int hipsBoneIndex, float timeSeconds)
    {
        var channel = FindHipsTranslationChannel(clip, hipsBoneIndex);
        return channel is null ? Vector3.Zero : channel.SampleTranslation(timeSeconds);
    }

    /// <summary>Authored Hips translation at clip start (t = 0).</summary>
    public static Vector3 GetStartTranslation(AnimationClip clip, int hipsBoneIndex) =>
        SampleHipsTranslation(clip, hipsBoneIndex, 0f);

    /// <summary>Authored Hips translation at clip end (t = duration, clamped to last key).</summary>
    public static Vector3 GetEndTranslation(AnimationClip clip, int hipsBoneIndex)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return SampleHipsTranslation(clip, hipsBoneIndex, clip.DurationSeconds);
    }

    /// <summary>Authored net Hips displacement per complete loop (end - start).</summary>
    public static Vector3 GetNetDisplacement(AnimationClip clip, int hipsBoneIndex) =>
        GetEndTranslation(clip, hipsBoneIndex) - GetStartTranslation(clip, hipsBoneIndex);

    /// <summary>
    /// Authored horizontal (X/Z) locomotion speed in meters per second for a
    /// clip, derived from its net root displacement, its duration, and the world
    /// scale. This is the authoritative steady-state speed used to scale the
    /// Phase 2I movement-speed envelope; no literal walk/run constants are
    /// stored. Returns 0 (never NaN) for a missing root track, zero horizontal
    /// displacement, non-finite data, or a non-positive/non-finite scale.
    /// </summary>
    public static float ComputeHorizontalSpeedMetersPerSecond(
        AnimationClip clip,
        int hipsBoneIndex,
        float worldScale)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var start = GetStartTranslation(clip, hipsBoneIndex);
        var end = GetEndTranslation(clip, hipsBoneIndex);
        return HorizontalSpeedMetersPerSecond(start, end, clip.DurationSeconds, worldScale);
    }

    /// <summary>
    /// Low-level authored-speed math: horizontal distance between two native
    /// root samples, scaled to meters and divided by duration. Guards
    /// zero/non-finite duration, scale, and samples so callers never see a
    /// divide-by-zero or NaN.
    /// </summary>
    public static float HorizontalSpeedMetersPerSecond(
        Vector3 start,
        Vector3 end,
        double durationSeconds,
        float worldScale)
    {
        if (!float.IsFinite(worldScale) || worldScale <= 0f)
        {
            return 0f;
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            return 0f;
        }

        if (!IsFinite(start) || !IsFinite(end))
        {
            return 0f;
        }

        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var nativeNet = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(nativeNet) || nativeNet <= 0f)
        {
            return 0f;
        }

        var metersPerSecond = nativeNet * worldScale / (float)durationSeconds;
        return float.IsFinite(metersPerSecond) && metersPerSecond > 0f ? metersPerSecond : 0f;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Horizontal (X/Z) part of a native-space root vector; Y is preserved elsewhere.</summary>
    public static Vector3 HorizontalOnly(Vector3 value) => new(value.X, 0f, value.Z);

    /// <summary>
    /// Continuous authored root position at absolute clip elapsed time
    /// <paramref name="elapsedSeconds"/> (elapsed = absolute clock - clip start,
    /// normally non-negative but any finite value is handled deterministically).
    /// </summary>
    public static Vector3 ComputeAbsoluteRoot(AnimationClip clip, int hipsBoneIndex, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Elapsed time must be finite.");
        }

        var duration = (double)clip.DurationSeconds;
        var channel = FindHipsTranslationChannel(clip, hipsBoneIndex);
        if (channel is null)
        {
            return Vector3.Zero;
        }

        var start = channel.SampleTranslation(0f);
        var net = channel.SampleTranslation(clip.DurationSeconds) - start;

        var loops = (long)Math.Floor(elapsedSeconds / duration);
        var norm = elapsedSeconds - loops * duration;

        // Guard a negative remainder (floor edge) back to the loop start. Do NOT
        // fold a remainder that merely *rounds up* to the duration when narrowed
        // to float back to 0: that would silently drop one whole loop of net
        // displacement and emit a near-full-loop reverse snap at the seam.
        // Sampling at the duration clamps to the final key (start + net), which
        // is exactly continuous with the next loop's start.
        if (norm < 0.0)
        {
            norm = 0.0;
        }

        var current = channel.SampleTranslation((float)norm);
        return new Vector3(
            loops * net.X + (current.X - start.X),
            loops * net.Y + (current.Y - start.Y),
            loops * net.Z + (current.Z - start.Z));
    }

    /// <summary>
    /// Horizontal root-motion delta (native FBX units) from
    /// <paramref name="previousElapsedSeconds"/> to
    /// <paramref name="currentElapsedSeconds"/>. Correct across zero, one, or
    /// many loop wraps; zero elapsed yields zero; repeated evaluation is
    /// deterministic; no large reverse delta at wrap.
    /// </summary>
    public static Vector3 ComputeDelta(
        AnimationClip clip,
        int hipsBoneIndex,
        double previousElapsedSeconds,
        double currentElapsedSeconds)
    {
        var before = ComputeAbsoluteRoot(clip, hipsBoneIndex, previousElapsedSeconds);
        var after = ComputeAbsoluteRoot(clip, hipsBoneIndex, currentElapsedSeconds);
        return after - before;
    }

    /// <summary>
    /// Remove consumed horizontal Hips travel from an already-evaluated local
    /// pose: pin Hips X/Z to the clip's start reference while preserving the
    /// sampled Y, Hips rotation, and the rest of the hierarchy. No-op when the
    /// clip has no Hips translation track.
    /// </summary>
    public static void NeutralizeHipsHorizontal(
        AnimationClip clip,
        int hipsBoneIndex,
        Matrix4x4[] local)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(local);
        if (hipsBoneIndex < 0 || hipsBoneIndex >= local.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(hipsBoneIndex));
        }

        var channel = FindHipsTranslationChannel(clip, hipsBoneIndex);
        if (channel is null)
        {
            return;
        }

        var start = channel.SampleTranslation(0f);
        local[hipsBoneIndex].M41 = start.X;
        local[hipsBoneIndex].M43 = start.Z;
    }

    /// <summary>
    /// Neutralized rendered Hips translation: start X/Z plus sampled Y.
    /// Preserves vertical bob; rotation is untouched by this helper.
    /// </summary>
    public static Vector3 NeutralizedHipsTranslation(AnimationClip clip, int hipsBoneIndex, float normalizedTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var channel = FindHipsTranslationChannel(clip, hipsBoneIndex);
        if (channel is null)
        {
            return Vector3.Zero;
        }

        var start = channel.SampleTranslation(0f);
        var sampled = channel.SampleTranslation(normalizedTimeSeconds);
        return new Vector3(start.X, sampled.Y, start.Z);
    }
}
