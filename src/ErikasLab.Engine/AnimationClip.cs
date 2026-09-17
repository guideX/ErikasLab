using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Original source keyframes for one bone: translations and/or rotations with
/// their own timestamps (times may differ between the two tracks). The source
/// does not animate scale, so none is stored.
/// </summary>
public sealed class AnimationChannel
{
    public AnimationChannel(
        int boneIndex,
        float[] times,
        Vector3[]? translations,
        Quaternion[]? rotations)
    {
        if (boneIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boneIndex), "Bone index must be non-negative.");
        }

        ArgumentNullException.ThrowIfNull(times);
        if (times.Length == 0)
        {
            throw new ArgumentException("A channel needs at least one key.", nameof(times));
        }

        if (translations is null && rotations is null)
        {
            throw new ArgumentException("A channel needs a translation or rotation track.", nameof(translations));
        }

        if (translations is not null && translations.Length != times.Length)
        {
            throw new ArgumentException("Translation count must match key count.", nameof(translations));
        }

        if (rotations is not null && rotations.Length != times.Length)
        {
            throw new ArgumentException("Rotation count must match key count.", nameof(rotations));
        }

        for (var i = 1; i < times.Length; i++)
        {
            if (times[i] < times[i - 1])
            {
                throw new ArgumentException("Keyframe times must be non-decreasing.", nameof(times));
            }
        }

        BoneIndex = boneIndex;
        Times = (float[])times.Clone();
        Translations = translations is null ? null : (Vector3[])translations.Clone();
        Rotations = rotations is null ? null : (Quaternion[])rotations.Clone();
    }

    public int BoneIndex { get; }

    public float[] Times { get; }

    public Vector3[]? Translations { get; }

    public Quaternion[]? Rotations { get; }

    public bool HasTranslation => Translations is not null;

    public bool HasRotation => Rotations is not null;

    public int KeyCount => Times.Length;

    public Vector3 SampleTranslation(float time)
    {
        if (Translations is null)
        {
            throw new InvalidOperationException("Channel has no translation track.");
        }

        var (before, after, amount) = LocateSegment(time);
        return Vector3.Lerp(Translations[before], Translations[after], amount);
    }

    public Quaternion SampleRotation(float time)
    {
        if (Rotations is null)
        {
            throw new InvalidOperationException("Channel has no rotation track.");
        }

        var (before, after, amount) = LocateSegment(time);
        return Quaternion.Normalize(Quaternion.Slerp(Rotations[before], Rotations[after], amount));
    }

    private (int Before, int After, float Amount) LocateSegment(float time)
    {
        if (time <= Times[0])
        {
            return (0, 0, 0f);
        }

        var last = Times.Length - 1;
        if (time >= Times[last])
        {
            return (last, last, 0f);
        }

        var low = 0;
        var high = last;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (Times[mid] <= time)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var span = Times[high] - Times[low];
        var amount = span > 0 ? (time - Times[low]) / span : 0f;
        return (low, high, amount);
    }
}

/// <summary>
/// One preserved source clip: original keyframes plus source timing.
/// </summary>
public sealed class AnimationClip
{
    public AnimationClip(
        string name,
        float durationSeconds,
        float framesPerSecond,
        IReadOnlyList<AnimationChannel> channels)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A clip needs a non-empty name.", nameof(name));
        }

        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive.");
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate must be positive.");
        }

        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0)
        {
            throw new ArgumentException("A clip needs at least one channel.", nameof(channels));
        }

        Name = name;
        DurationSeconds = durationSeconds;
        FramesPerSecond = framesPerSecond;
        Channels = channels.ToArray();
    }

    public string Name { get; }

    public float DurationSeconds { get; }

    public float FramesPerSecond { get; }

    public IReadOnlyList<AnimationChannel> Channels { get; }

    /// <summary>
    /// Loop <paramref name="elapsedSeconds"/> into [0, <see cref="DurationSeconds"/>).
    /// Exact duration multiples map to 0 (seamless loop); negatives wrap.
    /// </summary>
    public float NormalizeTime(double elapsedSeconds)
    {
        var wrapped = elapsedSeconds % DurationSeconds;
        if (wrapped < 0)
        {
            wrapped += DurationSeconds;
        }

        // A floating-point remainder can sit exactly on the duration for
        // negative inputs; fold it back to the loop start.
        if (wrapped >= DurationSeconds)
        {
            wrapped -= DurationSeconds;
        }

        return (float)wrapped;
    }
}
