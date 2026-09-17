using ErikasLab.Engine;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using PipelineAnimationChannel = Microsoft.Xna.Framework.Content.Pipeline.Graphics.AnimationChannel;

namespace ErikasLab.Content.Pipeline;

/// <summary>
/// Stock <see cref="ModelProcessor"/> behavior for the runtime <c>Model</c>
/// plus a portable animation sidecar: extracts the canonical skeleton and the
/// Take 001 clip from the same import and writes them (via
/// <see cref="ErikaClipCodec"/>) to a deterministic <c>erika_idle.bin</c>
/// sidecar registered with <see cref="ContentProcessorContext.AddOutputFile"/>.
/// Fails loudly on unmapped bones, unexpected scales, or unsorted keys instead
/// of silently producing corrupt clips.
/// </summary>
[ContentProcessor(DisplayName = "Erika model + skeletal animation")]
public sealed class ErikaModelProcessor : ModelProcessor
{
    public const string ClipName = "Take 001";

    public const string SidecarFilename = "erika_idle.bin";

    private const float SourceFramesPerSecond = 30f;
    private const float ScaleTolerance = 1e-3f;
    private const float VarianceEpsilon = 1e-6f;

    public override ModelContent Process(NodeContent input, ContentProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Extract before the base processor is allowed to reshape the DOM.
        var animation = ExtractAnimation(input, context);
        WriteSidecar(animation, context);

        return base.Process(input, context);
    }

    private static void WriteSidecar(SkeletalAnimation animation, ContentProcessorContext context)
    {
        // Written straight to the content output directory (a config-stable
        // location the build stages into the app); also registered so MGCB
        // tracks it as a build product.
        var path = Path.Combine(context.OutputDirectory, SidecarFilename);
        Directory.CreateDirectory(context.OutputDirectory);
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ErikaClipCodec.Write(stream, animation.Skeleton, animation.Clip);
        }

        context.AddOutputFile(path);
    }

    private static SkeletalAnimation ExtractAnimation(NodeContent input, ContentProcessorContext context)
    {
        var skeleton = BuildSkeleton(input, context);
        var clip = BuildClip(input, skeleton, context);
        return new SkeletalAnimation(skeleton, clip);
    }

    private static Skeleton BuildSkeleton(NodeContent input, ContentProcessorContext context)
    {
        var bones = new List<SkeletonBone>();
        CollectBones(input, Skeleton.NoParent, bones);
        if (bones.Count == 0)
        {
            throw new InvalidContentException("No BoneContent joints found in the imported scene.", input.Identity);
        }

        var roots = bones.Where(bone => bone.ParentIndex == Skeleton.NoParent).ToList();
        context.Logger.LogImportantMessage(
            "Erika skeleton: {0} joints, root(s): {1}.",
            bones.Count,
            string.Join(", ", roots.Select(root => root.Name)));

        return new Skeleton(bones);
    }

    private static void CollectBones(NodeContent node, int parentBone, List<SkeletonBone> into)
    {
        foreach (var child in node.Children)
        {
            if (child is BoneContent)
            {
                if (!child.Transform.Decompose(out var scale, out var rotation, out var translation))
                {
                    throw new InvalidContentException(
                        $"Bind transform of bone '{child.Name}' does not decompose.", child.Identity);
                }

                AssertUnitScale(scale, $"bind transform of bone '{child.Name}'", child.Identity);

                var index = into.Count;
                into.Add(new SkeletonBone(
                    child.Name,
                    parentBone,
                    new System.Numerics.Vector3(translation.X, translation.Y, translation.Z),
                    new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)));
                CollectBones(child, index, into);
            }
            else
            {
                // Transparent: meshes and other nodes never break joint chains.
                CollectBones(child, parentBone, into);
            }
        }
    }

    private static AnimationClip BuildClip(
        NodeContent input, Skeleton skeleton, ContentProcessorContext context)
    {
        // FbxImporter attaches the take to the Hips bone node rather than the
        // scene root, so candidates are gathered from the whole tree.
        var candidates = new List<(string Key, AnimationContent Animation)>();
        Walk(input);

        void Walk(NodeContent node)
        {
            foreach (var pair in node.Animations)
            {
                candidates.Add((pair.Key, pair.Value));
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        AnimationContent animation;
        var named = candidates.FirstOrDefault(candidate => candidate.Key == ClipName);
        if (named.Animation is not null)
        {
            animation = named.Animation;
        }
        else if (candidates.Count == 1)
        {
            animation = candidates[0].Animation;
            context.Logger.LogWarning(
                null,
                input.Identity,
                "Animation '{0}' not found by name (importer key was '{1}'); " +
                "using the single available animation and naming the clip '{0}'.",
                ClipName, candidates[0].Key);
        }
        else
        {
            throw new InvalidContentException(
                $"Animation '{ClipName}' not found. Candidates: {candidates.Count}.",
                input.Identity);
        }

        var durationSeconds = (float)animation.Duration.TotalSeconds;
        if (durationSeconds <= 0)
        {
            throw new InvalidContentException($"Clip '{ClipName}' has non-positive duration.", input.Identity);
        }

        var tracks = new Dictionary<(int Bone, bool Rotation), ChannelBuilder>();
        foreach (var pair in animation.Channels)
        {
            var boneIndex = MapBone(skeleton, pair.Key, input);
            Accumulate(pair.Key, pair.Value, boneIndex, tracks, input);
        }

        if (tracks.Count == 0)
        {
            throw new InvalidContentException($"Clip '{ClipName}' has no animated tracks.", input.Identity);
        }

        var channels = tracks
            .OrderBy(track => track.Key.Bone)
            .ThenBy(track => track.Key.Rotation)
            .Select(track => track.Value.Build(track.Key.Bone))
            .ToList();

        var keyTotal = channels.Sum(channel => channel.KeyCount);
        var frames = durationSeconds * SourceFramesPerSecond;
        context.Logger.LogImportantMessage(
            "Erika clip '{0}': {1:F3}s (~{2:F1} frames at {3} Hz), {4} channels, {5} keys.",
            ClipName, durationSeconds, frames, SourceFramesPerSecond, channels.Count, keyTotal);

        VerifyFrameCadence(channels, context, input);
        LogHipsTranslation(skeleton, channels, context);
        LogKeyVersusBind(skeleton, channels, context);

        return new AnimationClip(ClipName, durationSeconds, SourceFramesPerSecond, channels);
    }

    // TEMPORARY Phase 2C importer comparison (revert before commit).
    private static void LogKeyVersusBind(
        Skeleton skeleton, List<ErikasLab.Engine.AnimationChannel> channels, ContentProcessorContext context)
    {
        foreach (var want in new[] { "UpLeg", "Arm", "Shoulder", "Head", "Hips", "Foot" })
        {
            var bone = skeleton.Bones
                .Select((candidate, index) => (candidate, index))
                .FirstOrDefault(entry => entry.candidate.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (bone.candidate.Name is null)
            {
                continue;
            }

            var track = channels.FirstOrDefault(channel =>
                channel.BoneIndex == bone.index && channel.HasRotation);
            if (track is null || track.Rotations is null)
            {
                continue;
            }

            var mid = track.Rotations[track.Rotations.Length / 2];
            var bind = bone.candidate.BindRotation;
            var dot = Math.Abs(
                mid.X * bind.X + mid.Y * bind.Y + mid.Z * bind.Z + mid.W * bind.W);
            var angle = 2 * Math.Acos(Math.Min(1f, dot)) * 180 / Math.PI;
            context.Logger.LogImportantMessage(
                "Erika key-vs-bind: {0} mid-key {1:F1} deg from bind.",
                bone.candidate.Name, angle);
        }
    }

    private static int MapBone(Skeleton skeleton, string channelName, NodeContent input)
    {
        if (!skeleton.TryGetBoneIndex(channelName, out var boneIndex))
        {
            throw new InvalidContentException(
                $"Animation channel '{channelName}' matches no skeleton bone " +
                $"({skeleton.BoneCount} joints).",
                input.Identity);
        }

        return boneIndex;
    }

    private static void Accumulate(
        string channelName,
        PipelineAnimationChannel channel,
        int boneIndex,
        Dictionary<(int Bone, bool Rotation), ChannelBuilder> tracks,
        NodeContent input)
    {
        if (channel.Count == 0)
        {
            return;
        }

        var times = new float[channel.Count];
        var translations = new System.Numerics.Vector3[channel.Count];
        var rotations = new System.Numerics.Quaternion[channel.Count];
        for (var i = 0; i < channel.Count; i++)
        {
            var key = channel[i];
            if (i > 0 && (float)key.Time.TotalSeconds < times[i - 1])
            {
                throw new InvalidContentException(
                    $"Channel '{channelName}' has unsorted keyframes.", input.Identity);
            }

            if (!key.Transform.Decompose(out var scale, out var rotation, out var translation))
            {
                throw new InvalidContentException(
                    $"Keyframe {i} of channel '{channelName}' does not decompose.", input.Identity);
            }

            AssertUnitScale(scale, $"keyframe {i} of channel '{channelName}'", input.Identity);
            times[i] = (float)key.Time.TotalSeconds;
            translations[i] = new System.Numerics.Vector3(translation.X, translation.Y, translation.Z);
            rotations[i] = System.Numerics.Quaternion.Normalize(
                new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        }

        var variesTranslation = Varies(translations, static (a, b) =>
            System.Numerics.Vector3.DistanceSquared(a, b) > VarianceEpsilon);
        var variesRotation = Varies(rotations, static (a, b) =>
            1 - Math.Abs(System.Numerics.Quaternion.Dot(a, b)) > VarianceEpsilon);

        // Static channels (e.g. bind-pose layers) carry no motion; the runtime
        // falls back to the bind transform, so they are dropped here.
        if (variesTranslation)
        {
            AddTrack(tracks, boneIndex, rotation: false, channelName, times, translations, rotations, input);
        }

        if (variesRotation)
        {
            AddTrack(tracks, boneIndex, rotation: true, channelName, times, translations, rotations, input);
        }
    }

    private static void AddTrack(
        Dictionary<(int Bone, bool Rotation), ChannelBuilder> tracks,
        int boneIndex,
        bool rotation,
        string channelName,
        float[] times,
        System.Numerics.Vector3[] translations,
        System.Numerics.Quaternion[] rotations,
        NodeContent input)
    {
        var key = (boneIndex, rotation);
        if (tracks.ContainsKey(key))
        {
            throw new InvalidContentException(
                $"Duplicate {(rotation ? "rotation" : "translation")} track for bone index {boneIndex} " +
                $"(channel '{channelName}').",
                input.Identity);
        }

        tracks.Add(key, new ChannelBuilder(
            (float[])times.Clone(),
            rotation ? null : (System.Numerics.Vector3[])translations.Clone(),
            rotation ? (System.Numerics.Quaternion[])rotations.Clone() : null));
    }

    private static bool Varies<T>(T[] values, Func<T, T, bool> differs)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (differs(values[0], values[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertUnitScale(Microsoft.Xna.Framework.Vector3 scale, string what, ContentIdentity identity)
    {
        if (Math.Abs(scale.X - 1) > ScaleTolerance
            || Math.Abs(scale.Y - 1) > ScaleTolerance
            || Math.Abs(scale.Z - 1) > ScaleTolerance)
        {
            throw new InvalidContentException(
                $"Unexpected scale in {what}: ({scale.X}, {scale.Y}, {scale.Z}). " +
                "The source must not animate scale.",
                identity);
        }
    }

    private static void VerifyFrameCadence(
        List<ErikasLab.Engine.AnimationChannel> channels, ContentProcessorContext context, NodeContent input)
    {
        var interval = 1 / SourceFramesPerSecond;
        foreach (var channel in channels)
        {
            foreach (var time in channel.Times)
            {
                var steps = time / interval;
                if (Math.Abs(steps - MathF.Round(steps)) > 1e-3f)
                {
                    context.Logger.LogWarning(
                        null,
                        input.Identity,
                        "Key time {0:F6}s is not on the {1} Hz cadence.",
                        time, SourceFramesPerSecond);
                    return;
                }
            }
        }
    }

    private static void LogHipsTranslation(
        Skeleton skeleton, List<ErikasLab.Engine.AnimationChannel> channels, ContentProcessorContext context)
    {
        var hips = skeleton.Bones
            .Select((bone, index) => (bone, index))
            .FirstOrDefault(entry =>
                entry.bone.Name.Contains("Hips", StringComparison.OrdinalIgnoreCase));
        if (hips.bone.Name is null)
        {
            context.Logger.LogMessage("Erika root motion: no Hips bone found.");
            return;
        }

        var track = channels.FirstOrDefault(channel =>
            channel.BoneIndex == hips.index && channel.HasTranslation);
        if (track is null)
        {
            context.Logger.LogImportantMessage("Erika root motion: Hips has no translation track (rotation only).");
            return;
        }

        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var value in track.Translations!)
        {
            min = System.Numerics.Vector3.Min(min, value);
            max = System.Numerics.Vector3.Max(max, value);
        }

        context.Logger.LogImportantMessage(
            "Erika root motion: Hips '{0}' translation range X [{1:F2}, {2:F2}] Y [{3:F2}, {4:F2}] Z [{5:F2}, {6:F2}] over {7} keys.",
            hips.bone.Name, min.X, max.X, min.Y, max.Y, min.Z, max.Z, track.KeyCount);
    }

    private sealed class ChannelBuilder(
        float[] times,
        System.Numerics.Vector3[]? translations,
        System.Numerics.Quaternion[]? rotations)
    {
        public ErikasLab.Engine.AnimationChannel Build(int boneIndex) =>
            new(boneIndex, times, translations, rotations);
    }
}
