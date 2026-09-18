using ErikasLab.Engine;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;

namespace ErikasLab.Content.Pipeline;

/// <summary>
/// Stock <see cref="ModelProcessor"/> behavior for the runtime <c>Model</c>
/// plus a portable animation sidecar: extracts the canonical skeleton (from the
/// import DOM) and the Take 001 clip (re-imported raw via Assimp) and writes
/// them (via <see cref="ErikaClipCodec"/>) to a deterministic
/// <c>erika_idle.bin</c> sidecar registered with
/// <see cref="ContentProcessorContext.AddOutputFile"/>.
/// Fails loudly on unmapped bones, unexpected scales, or unsorted keys instead
/// of silently producing corrupt clips.
///
/// Animation note: stock MonoGame <c>AnimationContent</c> strips FBX joint
/// orientation (pre-rotation pivots) from keys (e.g. UpLeg bind 180 deg becomes
/// a 25 deg key, Shoulder 133 deg becomes 87 deg), producing unusable clips
/// that explode/collapse at runtime (proven by screenshots). Raw Assimp
/// <c>NodeAnimationChannel</c> keys preserve correct full parent-relative
/// locals (UpLeg ~168 deg ~= bind 180 deg, Shoulder ~139 deg ~= bind 133 deg),
/// so the processor re-imports the source FBX via AssimpNetter (same version
/// as MGCB) for animation only. Skeleton still comes from the import DOM
/// (BoneContent, OffsetMatrix-derived, verified against the runtime Model).
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
        var clip = BuildClipRaw(input, skeleton, context);
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

    private static AnimationClip BuildClipRaw(
        NodeContent input, Skeleton skeleton, ContentProcessorContext context)
    {
        var sourcePath = input.Identity.SourceFilename;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidContentException("Source FBX path unavailable for raw animation import.", input.Identity);
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            sourcePath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(context.OutputDirectory) ?? ".", sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            // MGCB passes the intermediate/source path; fall back to the
            // absolute erika source if the identity path does not resolve.
            throw new InvalidContentException(
                $"Source FBX for raw animation import not found: '{sourcePath}'.",
                input.Identity);
        }

        using var importer = new Assimp.AssimpContext();
        // No mesh post-process needed for animation; keep import minimal and
        // deterministic. Animation keys are unaffected by mesh flags.
        var scene = importer.ImportFile(
            sourcePath,
            Assimp.PostProcessSteps.Triangulate | Assimp.PostProcessSteps.FlipUVs);

        if (scene.AnimationCount == 0)
        {
            throw new InvalidContentException($"No animations in '{sourcePath}'.", input.Identity);
        }

        // Mixamo files carry takes 'Take 001' + 'mixamo.com' as animation
        // stacks; Assimp exposes the single stack (here 'mixamo.com').
        // There is exactly one animation; name the clip canonically.
        var aiAnimation = scene.Animations[0];
        if (scene.AnimationCount != 1)
        {
            context.Logger.LogImportantMessage(
                "Erika animation: {0} stacks, using '{1}' as '{2}'.",
                scene.AnimationCount, aiAnimation.Name, ClipName);
        }

        var durationSeconds = (float)(aiAnimation.DurationInTicks / aiAnimation.TicksPerSecond);
        if (durationSeconds <= 0)
        {
            throw new InvalidContentException($"Clip '{ClipName}' has non-positive duration.", input.Identity);
        }

        // One merged channel per bone (translation and/or rotation tracks share
        // the same key times for this source). Static bones (single-key
        // fingertip segments, unanimated joints) are dropped; runtime falls
        // back to bind.
        var merged = new Dictionary<int, RawChannel>(skeleton.BoneCount);
        foreach (var aiChannel in aiAnimation.NodeAnimationChannels)
        {
            if (!skeleton.TryGetBoneIndex(aiChannel.NodeName, out var boneIndex))
            {
                // Raw animation also animates no skeleton-external nodes for
                // this source (51 channels, all bone-level); fail loudly if
                // that ever changes instead of silently dropping motion.
                throw new InvalidContentException(
                    $"Animation channel '{aiChannel.NodeName}' matches no skeleton bone " +
                    $"({skeleton.BoneCount} joints).",
                    input.Identity);
            }

            if (merged.ContainsKey(boneIndex))
            {
                throw new InvalidContentException(
                    $"Duplicate animation channel for bone '{aiChannel.NodeName}'.",
                    input.Identity);
            }

            var channel = ConvertChannel(aiChannel, boneIndex, (float)aiAnimation.TicksPerSecond, input);
            if (channel is not null)
            {
                merged.Add(boneIndex, channel);
            }
        }

        if (merged.Count == 0)
        {
            throw new InvalidContentException($"Clip '{ClipName}' has no animated tracks.", input.Identity);
        }

        var channels = merged.Values
            .OrderBy(channel => channel.BoneIndex)
            .Select(channel => channel.Build())
            .ToList();

        var keyTotal = channels.Sum(channel => channel.KeyCount);
        var frames = durationSeconds * SourceFramesPerSecond;
        context.Logger.LogImportantMessage(
            "Erika clip '{0}': {1:F3}s (~{2:F1} frames at {3} Hz), {4} channels, {5} keys (raw Assimp).",
            ClipName, durationSeconds, frames, SourceFramesPerSecond, channels.Count, keyTotal);

        VerifyFrameCadence(channels, context, input);
        LogHipsTranslation(skeleton, channels, context);

        return new AnimationClip(ClipName, durationSeconds, SourceFramesPerSecond, channels);
    }

    private static RawChannel? ConvertChannel(
        Assimp.NodeAnimationChannel aiChannel,
        int boneIndex,
        float ticksPerSecond,
        NodeContent input)
    {
        // Position/Rotation/Scaling keys share times for this source (121 keys
        // for animated bones, 1 key for static fingertip segments). Scaling
        // must stay unit; the source carries no scale animation (audit).
        var posKeys = aiChannel.PositionKeys;
        var rotKeys = aiChannel.RotationKeys;
        var scaleKeys = aiChannel.ScalingKeys;

        foreach (var skey in scaleKeys)
        {
            if (Math.Abs(skey.Value.X - 1) > ScaleTolerance
                || Math.Abs(skey.Value.Y - 1) > ScaleTolerance
                || Math.Abs(skey.Value.Z - 1) > ScaleTolerance)
            {
                throw new InvalidContentException(
                    $"Unexpected scale in channel '{aiChannel.NodeName}': " +
                    $"({skey.Value.X}, {skey.Value.Y}, {skey.Value.Z}). " +
                    "The source must not animate scale.",
                    input.Identity);
            }
        }

        // Use rotation keys as the master timebase (all animated bones have
        // 121 rotation keys; static ones have 1). Position keys mirror the
        // same times; look up by index (counts match for this source).
        if (rotKeys.Count != posKeys.Count)
        {
            throw new InvalidContentException(
                $"Channel '{aiChannel.NodeName}' has mismatched key counts " +
                $"(P={posKeys.Count}, R={rotKeys.Count}).",
                input.Identity);
        }

        if (rotKeys.Count == 0)
        {
            return null;
        }

        var times = new float[rotKeys.Count];
        var translations = new System.Numerics.Vector3[rotKeys.Count];
        var rotations = new System.Numerics.Quaternion[rotKeys.Count];
        for (var i = 0; i < rotKeys.Count; i++)
        {
            var rkey = rotKeys[i];
            var pkey = posKeys[i];
            if (i > 0 && rkey.Time < rotKeys[i - 1].Time)
            {
                throw new InvalidContentException(
                    $"Channel '{aiChannel.NodeName}' has unsorted keyframes.", input.Identity);
            }

            times[i] = (float)(rkey.Time / ticksPerSecond);
            translations[i] = new System.Numerics.Vector3(pkey.Value.X, pkey.Value.Y, pkey.Value.Z);
            var q = new System.Numerics.Quaternion(
                rkey.Value.X, rkey.Value.Y, rkey.Value.Z, rkey.Value.W);
            rotations[i] = System.Numerics.Quaternion.Normalize(q);
        }

        var variesTranslation = Varies(translations, static (a, b) =>
            System.Numerics.Vector3.DistanceSquared(a, b) > VarianceEpsilon);
        var variesRotation = Varies(rotations, static (a, b) =>
            1 - Math.Abs(System.Numerics.Quaternion.Dot(a, b)) > VarianceEpsilon);

        // Static tracks (single-key fingertip segments, constant translations
        // on non-root bones) carry no motion; runtime falls back to bind.
        System.Numerics.Vector3[]? keptTrans = variesTranslation ? translations : null;
        System.Numerics.Quaternion[]? keptRot = variesRotation ? rotations : null;
        if (keptTrans is null && keptRot is null)
        {
            return null;
        }

        return new RawChannel(boneIndex, times, keptTrans, keptRot);
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

    private sealed class RawChannel(
        int boneIndex,
        float[] times,
        System.Numerics.Vector3[]? translations,
        System.Numerics.Quaternion[]? rotations)
    {
        public int BoneIndex { get; } = boneIndex;

        public ErikasLab.Engine.AnimationChannel Build() =>
            new(BoneIndex, times, translations, rotations);
    }
}
