namespace ErikasLab.Engine;

/// <summary>
/// Phase 2K portable visual-pose clock. Owns one finite, non-negative elapsed
/// time kept inside <c>[0, duration)</c> plus one finite, non-negative playback
/// rate. It is a *pose sampler only*: it never stores a position, velocity, or
/// world transform, so it can never become a second translation authority. It
/// is deliberately separate from the authoritative root-motion clock owned by
/// <c>GameSession</c>; the renderer samples a skeletal pose from this clock
/// while world displacement continues to come from the root-motion clock.
///
/// Advancement integrates the playback rate over elapsed time with a
/// trapezoidal step (the average of the previous and current rate), so a rate
/// that ramps across a frame advances by the area under that segment rather
/// than the endpoint sample. That matches the Phase 2I speed envelope, which
/// changes linearly through <c>MoveTowards</c>. Zero/negative/non-finite time
/// or rate changes nothing and never produces a negative or non-finite phase.
///
/// Wrapping has exact-boundary semantics: a value landing exactly on the
/// duration maps to 0, a value just below the duration is preserved (it does
/// *not* fold back to 0), and a value just above the duration wraps to the
/// small remainder. This avoids reintroducing the near-loop-boundary fold bug
/// fixed for root motion in Phase 2I.
/// </summary>
public sealed class AnimationPlaybackClock
{
    /// <summary>Current loop phase in <c>[0, duration)</c>, always finite and >= 0.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>Rate applied on the most recent advance (finite, >= 0).</summary>
    public float PlaybackRate { get; private set; } = 1f;

    /// <summary>
    /// Reset the phase and rate. Non-finite/negative phase becomes 0; a
    /// non-finite/negative rate becomes 0. The supplied rate seeds the
    /// trapezoidal integrator's "previous rate" so the first advance does not
    /// jump from an unrelated value.
    /// </summary>
    public void Reset(double elapsedSeconds = 0.0, float playbackRate = 1f)
    {
        ElapsedSeconds = double.IsFinite(elapsedSeconds) && elapsedSeconds > 0.0 ? elapsedSeconds : 0.0;
        PlaybackRate = float.IsFinite(playbackRate) && playbackRate >= 0f ? playbackRate : 0f;
    }

    /// <summary>
    /// Place the clock at a specific phase (wrapped into <c>[0, duration)</c>)
    /// and seed the rate. Used to resume an interrupted transition's pose for
    /// phase continuity. Non-finite/negative phase becomes 0; an invalid
    /// duration leaves the phase at 0.
    /// </summary>
    public void SetPhase(double elapsedSeconds, double durationSeconds, float playbackRate)
    {
        PlaybackRate = float.IsFinite(playbackRate) && playbackRate >= 0f ? playbackRate : 0f;
        ElapsedSeconds = Wrap(elapsedSeconds, durationSeconds);
    }

    /// <summary>
    /// Advance by <paramref name="deltaSeconds"/> at <paramref name="playbackRate"/>,
    /// wrapping into <c>[0, durationSeconds)</c>. The step uses the average of
    /// the previous and current rate (trapezoidal). Returns the new phase.
    /// Zero/negative/non-finite time is a no-op; a negative/non-finite rate is
    /// ignored (the previous rate is reused) so the phase can never go
    /// backwards or become non-finite.
    /// </summary>
    public double Advance(double deltaSeconds, float playbackRate, double durationSeconds)
    {
        var rate = float.IsFinite(playbackRate) && playbackRate >= 0f ? playbackRate : PlaybackRate;
        var dt = double.IsFinite(deltaSeconds) && deltaSeconds > 0.0 ? deltaSeconds : 0.0;
        var averageRate = ((double)PlaybackRate + rate) * 0.5;
        ElapsedSeconds = Wrap(ElapsedSeconds + averageRate * dt, durationSeconds);
        PlaybackRate = rate;
        return ElapsedSeconds;
    }

    /// <summary>
    /// Wrap <paramref name="elapsedSeconds"/> into <c>[0, durationSeconds)</c>
    /// with exact-boundary correctness. A non-positive/non-finite elapsed or a
    /// non-positive/non-finite duration yields 0. A value landing exactly on a
    /// duration multiple maps to 0; a value just below the duration is
    /// preserved; a value just above wraps to the small remainder.
    /// </summary>
    public static double Wrap(double elapsedSeconds, double durationSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
        {
            return 0.0;
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            return 0.0;
        }

        var wrapped = elapsedSeconds % durationSeconds;
        if (wrapped < 0.0)
        {
            wrapped += durationSeconds;
        }

        // `%` can return the duration itself on a rounding edge; only that exact
        // multiple folds to 0. A remainder merely *below* the duration stays put.
        if (wrapped >= durationSeconds)
        {
            wrapped = 0.0;
        }

        return wrapped;
    }
}
