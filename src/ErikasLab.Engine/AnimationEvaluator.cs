using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Portable skeletal-animation evaluation: sample channels, resolve the
/// hierarchy, build GPU skinning matrices. Allocates nothing per call beyond
/// the caller-provided destination arrays.
/// </summary>
public static class AnimationEvaluator
{
    /// <summary>
    /// Local (parent-space) matrices for every bone at <paramref name="timeSeconds"/>.
    /// Animated bones sample their channel; all others use the bind transform.
    /// </summary>
    public static void EvaluateLocal(
        Skeleton skeleton,
        AnimationClip clip,
        float timeSeconds,
        Matrix4x4[] destination)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != skeleton.BoneCount)
        {
            throw new ArgumentException("Destination must match the bone count.", nameof(destination));
        }

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            destination[i] = Skeleton.Compose(
                skeleton.Bones[i].BindRotation,
                skeleton.Bones[i].BindTranslation);
        }

        foreach (var channel in clip.Channels)
        {
            if (channel.BoneIndex < 0 || channel.BoneIndex >= skeleton.BoneCount)
            {
                throw new InvalidOperationException(
                    $"Channel targets bone index {channel.BoneIndex} outside the skeleton.");
            }

            var bone = skeleton.Bones[channel.BoneIndex];
            var translation = channel.HasTranslation
                ? channel.SampleTranslation(timeSeconds)
                : bone.BindTranslation;
            var rotation = channel.HasRotation
                ? channel.SampleRotation(timeSeconds)
                : bone.BindRotation;
            destination[channel.BoneIndex] = Skeleton.Compose(rotation, translation);
        }
    }

    /// <summary>
    /// Absolute (model-space) matrices from local ones via the skeleton hierarchy.
    /// </summary>
    public static void EvaluateAbsolute(
        Skeleton skeleton,
        Matrix4x4[] local,
        Matrix4x4[] absolute)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        skeleton.ResolveAbsolute(local, absolute);
    }

    /// <summary>
    /// Absolute matrices for importers (like MonoGame's FbxImporter) whose
    /// keyframes already are model-space bone transforms: animated bones use
    /// their sampled keys directly, all others use the bind-pose absolute.
    /// No hierarchy resolution is applied (applying it again would
    /// double-transform every joint).
    /// </summary>
    public static void EvaluateAbsoluteDirect(
        Skeleton skeleton,
        AnimationClip clip,
        float timeSeconds,
        Matrix4x4[] bindAbsolute,
        Matrix4x4[] absolute)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bindAbsolute);
        ArgumentNullException.ThrowIfNull(absolute);
        if (bindAbsolute.Length != skeleton.BoneCount || absolute.Length != skeleton.BoneCount)
        {
            throw new ArgumentException("Arrays must match the bone count.");
        }

        Array.Copy(bindAbsolute, absolute, skeleton.BoneCount);

        foreach (var channel in clip.Channels)
        {
            if (channel.BoneIndex < 0 || channel.BoneIndex >= skeleton.BoneCount)
            {
                throw new InvalidOperationException(
                    $"Channel targets bone index {channel.BoneIndex} outside the skeleton.");
            }

            var bone = skeleton.Bones[channel.BoneIndex];
            var translation = channel.HasTranslation
                ? channel.SampleTranslation(timeSeconds)
                : bindAbsolute[channel.BoneIndex].Translation;
            var rotation = channel.HasRotation
                ? channel.SampleRotation(timeSeconds)
                : Quaternion.CreateFromRotationMatrix(bindAbsolute[channel.BoneIndex]);
            absolute[channel.BoneIndex] = Skeleton.Compose(rotation, translation);
        }
    }

    /// <summary>
    /// GPU skinning matrices: skin[i] = inverseBind[i] * animatedAbsolute[i],
    /// so an unanimated bind pose yields identity (vertices untouched).
    /// </summary>
    public static void ComputeSkinningMatrices(
        Matrix4x4[] inverseBind,
        Matrix4x4[] absolute,
        Matrix4x4[] skinning)
    {
        ArgumentNullException.ThrowIfNull(inverseBind);
        ArgumentNullException.ThrowIfNull(absolute);
        ArgumentNullException.ThrowIfNull(skinning);
        if (inverseBind.Length != absolute.Length || absolute.Length != skinning.Length)
        {
            throw new ArgumentException("All arrays must have equal length.");
        }

        for (var i = 0; i < skinning.Length; i++)
        {
            skinning[i] = inverseBind[i] * absolute[i];
        }
    }
}
