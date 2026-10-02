namespace ErikasLab.Engine;

/// <summary>
/// A single in-flight locomotion crossfade (Phase 2F). Retains the outgoing
/// clip and its loop origin, the incoming clip and its loop origin, and the
/// transition clock, so the renderer can evaluate both poses and blend them.
///
/// Bounded by design: at most one transition exists at a time. Interruptions
/// replace it rather than chaining, so there is no animation graph and no
/// unbounded history. Root motion is never blended here; the destination clip
/// is the sole authority (see <c>GameSession</c>).
/// </summary>
public readonly record struct AnimationTransition(
    string SourceClipName,
    double SourceClipStartSeconds,
    string DestinationClipName,
    double DestinationClipStartSeconds,
    double StartSeconds,
    float DurationSeconds)
{
    /// <summary>
    /// Normalized blend weight in [0, 1] at <paramref name="totalSeconds"/>:
    /// 0 = exact source pose, 1 = exact destination pose. A non-positive
    /// duration is treated as already complete.
    /// </summary>
    public float ProgressAt(double totalSeconds)
    {
        if (DurationSeconds <= 0f)
        {
            return 1f;
        }

        var progress = (totalSeconds - StartSeconds) / DurationSeconds;
        if (progress <= 0.0)
        {
            return 0f;
        }

        if (progress >= 1.0)
        {
            return 1f;
        }

        return (float)progress;
    }

    /// <summary>True once the blend has fully reached the destination pose.</summary>
    public bool IsCompleteAt(double totalSeconds) => ProgressAt(totalSeconds) >= 1f;
}
