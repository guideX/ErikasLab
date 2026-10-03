using ErikasLab.Engine;
using ErikasLab.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Numerics = System.Numerics;

namespace ErikasLab.Platform.MonoGame;

/// <summary>
/// Renders <see cref="ModelInstance"/> entries. Instances matching the Erika
/// figure play the prepared skeletal clip through GPU skinning
/// (<see cref="SkinnedEffect"/>); anything else renders in its imported
/// default pose exactly as Phase 2B did (static path preserved).
///
/// Portable Engine owns skeleton/clip math; this class owns effects, the bone
/// palette, and per-frame sampling. The FBX-to-world correction (uniform
/// scale + facing yaw + ground lift) stays centralized in
/// <see cref="PrepareCharacter"/>.
/// </summary>
internal sealed class AnimatedModelRenderer
{
    private readonly ModelLibrary _library;
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Dictionary<string, PreparedCharacter> _prepared = new(StringComparer.Ordinal);

    public AnimatedModelRenderer(ModelLibrary library, GraphicsDevice graphicsDevice)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
    }

    /// <summary>Measured native (imported) bounds, in FBX units, default pose.</summary>
    public record struct NativeBounds(Vector3 Min, Vector3 Max)
    {
        public float Height => Max.Y - Min.Y;
    }

    /// <summary>
    /// Advances playback bookkeeping. The absolute game clock (not accumulated
    /// deltas) is retained only for crossfade blend alpha, which stays
    /// transition-elapsed based. Pose sample times are supplied explicitly by
    /// the host from the Phase 2K visual pose clocks, so the renderer never
    /// integrates gameplay animation timing itself. When a transition is in
    /// flight both clips are evaluated at their own explicit pose phases and
    /// blended; otherwise the active clip renders alone.
    /// </summary>
    public void Update(FrameTime frameTime)
    {
        foreach (var prepared in _prepared.Values)
        {
            prepared.TotalSeconds = frameTime.TotalSeconds;
        }
    }

    /// <summary>Active stable clip id (idle_looking_around/walk/run). Set by the host from the session.</summary>
    public string ActiveClipName { get; set; } = ErikaFigure.IdleClipName;

    /// <summary>
    /// Phase 2K active-clip visual pose phase (seconds) in the clip's loop.
    /// Owned by <see cref="GameSession.VisualPoseElapsedSeconds"/>; set by the
    /// host each frame. The renderer samples from this instead of deriving a
    /// time from the absolute clock, so world-motion timing and visible timing
    /// stay independent.
    /// </summary>
    public double PoseElapsedSeconds { get; set; }

    /// <summary>Phase 2K outgoing-clip visual pose phase during a crossfade. Set by the host.</summary>
    public double SourcePoseElapsedSeconds { get; set; }

    /// <summary>Phase 2K incoming-clip visual pose phase during a crossfade. Set by the host.</summary>
    public double DestinationPoseElapsedSeconds { get; set; }

    /// <summary>
    /// Phase 2F in-flight crossfade (or null). Set by the host from the session.
    /// Purely visual: the renderer blends the two local poses, while world root
    /// motion stays owned by <see cref="GameSession"/>.
    /// </summary>
    public AnimationTransition? Transition { get; set; }

    public void Draw(Scene scene, Matrix view, Matrix projection)
    {
        ArgumentNullException.ThrowIfNull(scene);

        foreach (var instance in scene.Models)
        {
            var prepared = GetOrPrepare(instance);
            var world = prepared.Correction * ToMonoGameMatrix(instance.Transform.WorldMatrix);

            if (prepared.Skeleton is null)
            {
                DrawStatic(prepared, world, view, projection);
            }
            else
            {
                DrawAnimated(prepared, world, view, projection);
            }
        }
    }

    /// <summary>Concise startup diagnostics; does not dump bones or keyframes.</summary>
    public IReadOnlyList<string> Diagnostics(Scene scene)
    {
        var lines = new List<string>();
        foreach (var instance in scene.Models)
        {
            var prepared = GetOrPrepare(instance);
            lines.Add($"Erika source: {ErikaFigure.SourceFile} -> asset '{instance.Asset}'");
            lines.Add($"Erika model: meshes={prepared.Model.Meshes.Count} " +
                $"parts={prepared.Model.Meshes.Sum(m => m.MeshParts.Count)} " +
                $"bones={prepared.Model.Bones.Count}");
            lines.Add($"Erika native bounds (sphere-merged, conservative): " +
                $"min={Fmt(prepared.Bounds.Min)} max={Fmt(prepared.Bounds.Max)} " +
                $"height={prepared.Bounds.Height:F1} units");
            lines.Add($"Erika scale: true height {ErikaFigure.NativeHeightUnits:F1} units -> " +
                $"{ErikaFigure.TargetHeightMeters:F2} m (x{ErikaFigure.Scale:F5}); " +
                $"resulting height ~{ErikaFigure.NativeHeightUnits * ErikaFigure.Scale:F2} m");
            lines.Add($"Erika orientation: bind facing {Fmt(prepared.BindFacing)} -> " +
                $"yaw correction {prepared.YawRadians:F3} rad (configured {ErikaFigure.FacingYawRadians:F3})");
            lines.Add($"Erika ground: true minY {ErikaFigure.NativeGroundUnits:F1} units -> " +
                $"lift {ErikaFigure.GroundLift:F3} m");
            lines.Add($"Erika effects: {prepared.EffectSummary}; {prepared.TextureSummary}");

            if (prepared.Skeleton is null)
            {
                lines.Add("Erika animation: none (static default pose)");
            }
            else
            {
                foreach (var clip in new[] { prepared.IdleClip!, prepared.WalkClip!, prepared.RunClip! })
                {
                    var keyTotal = clip.Channels.Sum(channel => channel.KeyCount);
                    var frames = clip.DurationSeconds * clip.FramesPerSecond;
                    lines.Add($"Erika clip: '{clip.Name}' {clip.DurationSeconds:F3}s @ {clip.FramesPerSecond:F0}Hz " +
                        $"({frames:F0} frames), {clip.Channels.Count} channels, {keyTotal} keys");
                }

                lines.Add($"Erika skeleton: {prepared.Skeleton!.BoneCount} joints " +
                    $"mapped {prepared.MappedBones}/{prepared.Skeleton.BoneCount} runtime bones, " +
                    $"palette {prepared.PaletteSize}");
                foreach (var asset in new[] { ErikaFigure.IdleClipAssetId, ErikaFigure.WalkClipAssetId, ErikaFigure.RunClipAssetId })
                {
                    lines.Add($"Erika artifact: {asset}.bin " +
                        $"({TryArtifactSize(asset, ".bin")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes)");
                }

                lines.Add($"Erika root motion: {prepared.RootMotionPolicy}; active '{ActiveClipName}' ({prepared.HipsRangeFor(ActiveClipName)})");
                lines.Add($"Erika bind check: max inverse-bind deviation {prepared.BindDeviation:F4} units");
            }
        }

        return lines;
    }

    private static void DrawStatic(PreparedCharacter prepared, Matrix world, Matrix view, Matrix projection)
    {
        foreach (var mesh in prepared.Model.Meshes)
        {
            foreach (var part in mesh.MeshParts)
            {
                if (part.Effect is IEffectMatrices matrices)
                {
                    matrices.World = prepared.BoneTransforms[mesh.ParentBone.Index] * world;
                    matrices.View = view;
                    matrices.Projection = projection;
                }
            }

            mesh.Draw();
        }
    }

    private void DrawAnimated(PreparedCharacter prepared, Matrix world, Matrix view, Matrix projection)
    {
        // Raw Assimp keys are full parent-relative locals (FBX pivots baked),
        // matching BoneContent bind space. Standard hierarchical path: sample
        // locals (single clip or a crossfade blend), resolve absolute via
        // parents, skin with inverseBind * absolute.
        if (Transition is { } transition && !transition.IsCompleteAt(prepared.TotalSeconds))
        {
            EvaluateCrossfade(prepared, transition);
        }
        else
        {
            EvaluateSingle(prepared);
        }

        AnimationEvaluator.EvaluateAbsolute(
            prepared.Skeleton!, prepared.Local, prepared.Absolute);
        AnimationEvaluator.ComputeSkinningMatrices(prepared.InverseBind, prepared.Absolute, prepared.Skin);
        for (var i = 0; i < prepared.Skin.Length; i++)
        {
            prepared.Palette[i] = ToMonoGameMatrix(prepared.Skin[i]);
        }

        foreach (var (_, effect) in prepared.PartEffects)
        {
            effect.SetBoneTransforms(prepared.Palette);
            effect.World = world;
            effect.View = view;
            effect.Projection = projection;
        }

        foreach (var mesh in prepared.Model.Meshes)
        {
            mesh.Draw();
        }
    }

    /// <summary>
    /// Single-clip path (Phase 2E preserved): evaluate the active clip's local
    /// pose and pin consumed walk/run Hips X/Z to the clip start reference.
    /// </summary>
    private void EvaluateSingle(PreparedCharacter prepared)
    {
        var clip = prepared.ClipFor(ActiveClipName);
        var time = clip.NormalizeTime(PoseElapsedSeconds);
        AnimationEvaluator.EvaluateLocal(prepared.Skeleton!, clip, time, prepared.Local);

        if (prepared.HipsBoneIndex >= 0)
        {
            if (string.Equals(ActiveClipName, ErikaFigure.WalkClipName, StringComparison.Ordinal))
            {
                prepared.Local[prepared.HipsBoneIndex].M41 = prepared.WalkStart.X;
                prepared.Local[prepared.HipsBoneIndex].M43 = prepared.WalkStart.Z;
            }
            else if (string.Equals(ActiveClipName, ErikaFigure.RunClipName, StringComparison.Ordinal))
            {
                prepared.Local[prepared.HipsBoneIndex].M41 = prepared.RunStart.X;
                prepared.Local[prepared.HipsBoneIndex].M43 = prepared.RunStart.Z;
            }
        }
    }

    /// <summary>
    /// Phase 2F crossfade path: evaluate the outgoing and incoming clips at their
    /// own loop phases, neutralize each locomotion pose's consumed Hips X/Z to
    /// its own clip start, then blend bone-by-bone. Alpha 0 reproduces the source
    /// pose and alpha 1 the destination pose exactly, so both endpoints stay
    /// continuous with the single-clip path; neither pose adds a second world
    /// root delta (that stays in <see cref="GameSession"/>).
    /// </summary>
    private void EvaluateCrossfade(PreparedCharacter prepared, AnimationTransition transition)
    {
        var sourceClip = prepared.ClipFor(transition.SourceClipName);
        var destinationClip = prepared.ClipFor(transition.DestinationClipName);
        var sourceTime = sourceClip.NormalizeTime(SourcePoseElapsedSeconds);
        var destinationTime = destinationClip.NormalizeTime(DestinationPoseElapsedSeconds);

        AnimationEvaluator.EvaluateLocalTransforms(
            prepared.Skeleton!, sourceClip, sourceTime,
            prepared.SourceTranslations, prepared.SourceRotations);
        AnimationEvaluator.EvaluateLocalTransforms(
            prepared.Skeleton!, destinationClip, destinationTime,
            prepared.DestinationTranslations, prepared.DestinationRotations);

        NeutralizeLocomotionHips(prepared, transition.SourceClipName, prepared.SourceTranslations);
        NeutralizeLocomotionHips(prepared, transition.DestinationClipName, prepared.DestinationTranslations);

        PoseBlender.BlendLocal(
            prepared.SourceTranslations,
            prepared.SourceRotations,
            prepared.DestinationTranslations,
            prepared.DestinationRotations,
            transition.ProgressAt(prepared.TotalSeconds),
            prepared.Local);
    }

    /// <summary>
    /// Pin consumed walk/run Hips X/Z to the clip start (Phase 2E policy),
    /// preserving sampled Y; idle stays verbatim. Applied per participating pose
    /// so a blend cannot reintroduce consumed travel.
    /// </summary>
    private static void NeutralizeLocomotionHips(
        PreparedCharacter prepared, string clipName, Numerics.Vector3[] translations)
    {
        if (prepared.HipsBoneIndex < 0)
        {
            return;
        }

        Numerics.Vector3 start;
        if (string.Equals(clipName, ErikaFigure.WalkClipName, StringComparison.Ordinal))
        {
            start = prepared.WalkStart;
        }
        else if (string.Equals(clipName, ErikaFigure.RunClipName, StringComparison.Ordinal))
        {
            start = prepared.RunStart;
        }
        else
        {
            return;
        }

        var hips = translations[prepared.HipsBoneIndex];
        translations[prepared.HipsBoneIndex] = new Numerics.Vector3(start.X, hips.Y, start.Z);
    }

    private PreparedCharacter GetOrPrepare(ModelInstance instance)
    {
        if (_prepared.TryGetValue(instance.Asset.Name, out var prepared))
        {
            return prepared;
        }

        prepared = PrepareCharacter(instance.Asset);
        _prepared.Add(instance.Asset.Name, prepared);
        return prepared;
    }

    private PreparedCharacter PrepareCharacter(ModelAssetId asset)
    {
        var model = _library.Get(asset);

        var boneTransforms = new Matrix[model.Bones.Count];
        model.CopyAbsoluteBoneTransformsTo(boneTransforms);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in model.Meshes)
        {
            var sphere = mesh.BoundingSphere.Transform(boneTransforms[mesh.ParentBone.Index]);
            min = Vector3.Min(min, sphere.Center - new Vector3(sphere.Radius));
            max = Vector3.Max(max, sphere.Center + new Vector3(sphere.Radius));
        }

        var bounds = new NativeBounds(min, max);
        var bindFacing = DetectBindFacing(model, boneTransforms);
        var yaw = ModelPlacement.YawToFacePlusZ(
                new Numerics.Vector3(bindFacing.X, bindFacing.Y, bindFacing.Z))
            + ErikaFigure.FacingYawRadians;
        var correction =
            Matrix.CreateScale(ErikaFigure.Scale) *
            Matrix.CreateRotationY(yaw) *
            Matrix.CreateTranslation(0, ErikaFigure.GroundLift, 0);

        if (!string.Equals(asset.Name, ErikaFigure.AssetId.Name, StringComparison.Ordinal))
        {
            return PreparedCharacter.Static(
                model, boneTransforms, bounds, bindFacing, yaw, correction,
                SummarizeEffects(model), SummarizeTextures(model));
        }

        var idle = _library.GetClip(ErikaFigure.IdleClipAssetId);
        var walk = _library.GetClip(ErikaFigure.WalkClipAssetId);
        var run = _library.GetClip(ErikaFigure.RunClipAssetId);
        AssertCompatibleSkeleton(idle.Skeleton, walk.Skeleton, ErikaFigure.WalkClipAssetId.Name);
        AssertCompatibleSkeleton(idle.Skeleton, run.Skeleton, ErikaFigure.RunClipAssetId.Name);
        return PrepareAnimated(
            model, idle.Skeleton, idle.Clip, walk.Clip, run.Clip,
            boneTransforms, bounds, bindFacing, yaw, correction);
    }

    /// <summary>
    /// Fail loudly if a locomotion sidecar skeleton diverges from the
    /// canonical idle skeleton (names, parents, bind pose). Static bones may
    /// ride bind at runtime, but a wrongly-bound channel would explode the
    /// mesh, so compatibility is strict.
    /// </summary>
    private static void AssertCompatibleSkeleton(Skeleton canonical, Skeleton other, string assetName)
    {
        if (other.BoneCount != canonical.BoneCount)
        {
            throw new ErikaContentException(
                $"Clip asset '{assetName}' skeleton has {other.BoneCount} joints, " +
                $"expected canonical {canonical.BoneCount}. Rebuild from the base-variant source.",
                new InvalidOperationException("Skeleton bone count mismatch."));
        }

        for (var i = 0; i < canonical.BoneCount; i++)
        {
            var expected = canonical.Bones[i];
            var actual = other.Bones[i];
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                || actual.ParentIndex != expected.ParentIndex)
            {
                throw new ErikaContentException(
                    $"Clip asset '{assetName}' bone {i} is '{actual.Name}' parent {actual.ParentIndex}, " +
                    $"expected '{expected.Name}' parent {expected.ParentIndex}.",
                    new InvalidOperationException("Skeleton hierarchy mismatch."));
            }

            var dt = Numerics.Vector3.Distance(actual.BindTranslation, expected.BindTranslation);
            var dq = 1 - Math.Abs(Numerics.Quaternion.Dot(actual.BindRotation, expected.BindRotation));
            const float BindTolerance = 1e-3f;
            if (dt > BindTolerance || dq > 1e-6)
            {
                throw new ErikaContentException(
                    $"Clip asset '{assetName}' bone '{actual.Name}' bind differs " +
                    $"(dT={dt:F5}, dQ={dq:E2}). Sources must share the canonical bind pose.",
                    new InvalidOperationException("Skeleton bind mismatch."));
            }
        }
    }

    private PreparedCharacter PrepareAnimated(
        Model model,
        Skeleton skeleton,
        AnimationClip idleClip,
        AnimationClip walkClip,
        AnimationClip runClip,
        Matrix[] boneTransforms,
        NativeBounds bounds,
        Vector3 bindFacing,
        float yaw,
        Matrix correction)
    {
        var map = new int[skeleton.BoneCount];
        var missing = new List<string>();
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var found = -1;
            for (var b = 0; b < model.Bones.Count; b++)
            {
                if (string.Equals(model.Bones[b].Name, skeleton.Bones[i].Name, StringComparison.Ordinal))
                {
                    found = b;
                    break;
                }
            }

            if (found < 0)
            {
                missing.Add(skeleton.Bones[i].Name);
            }

            map[i] = found;
        }

        if (missing.Count > 0)
        {
            throw new ErikaContentException(
                $"Animation skeleton bones missing from runtime model '{ErikaFigure.AssetId}': " +
                $"{string.Join(", ", missing)} ({model.Bones.Count} runtime bones). " +
                "Rebuild content from the canonical source; see README.md ('Erika content').",
                new KeyNotFoundException(missing[0]));
        }

        foreach (var clip in new[] { idleClip, walkClip, runClip })
        {
            foreach (var channel in clip.Channels)
            {
                if (channel.BoneIndex < 0 || channel.BoneIndex >= skeleton.BoneCount)
                {
                    throw new ErikaContentException(
                        $"Clip '{clip.Name}' targets bone index {channel.BoneIndex} " +
                        $"outside the {skeleton.BoneCount}-joint skeleton.",
                        new InvalidOperationException("Channel bone index out of range."));
                }
            }
        }

        // Cross-validate the Engine bind pose against the processed model bind
        // pose: both derive from the same import and must agree.
        var bindLocal = skeleton.ComputeBindLocalMatrices();
        var bindAbsolute = new Numerics.Matrix4x4[skeleton.BoneCount];
        skeleton.ResolveAbsolute(bindLocal, bindAbsolute);
        var engineInverse = skeleton.ComputeInverseBindMatrices();
        var deviation = 0f;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var engine = ToMonoGameMatrix(engineInverse[i]);
            Matrix modelInverse;
            Matrix.Invert(ref boneTransforms[map[i]], out modelInverse);
            deviation = Math.Max(deviation, MaxAbsDiff(engine, modelInverse));
        }

        const float BindToleranceUnits = 0.1f;
        if (deviation > BindToleranceUnits)
        {
            throw new ErikaContentException(
                $"Bind-pose mismatch for '{ErikaFigure.AssetId}': max inverse-bind deviation " +
                $"{deviation:F3} units exceeds {BindToleranceUnits} (skeleton/palette mapping suspect).",
                new InvalidOperationException());
        }

        // BlendIndices in the imported vertex buffers are mesh-relative
        // (0..66 into the 67 deformation bones, skeleton order), NOT
        // Model-relative (verified: Eyes weighted to slots 7/8 = LeftEye/
        // RightEye in skeleton order, Body torso to slot 0 = Hips, Eyelashes
        // to slot 5 = Head; Model order would map those to Spine/mesh nodes).
        // Stock ModelProcessor does not remap them for this pivot-rich Mixamo
        // source, so the GPU palette must be skeleton-ordered (67) to match.
        // Model-space extras (RootNode + 4 mesh nodes) are never indexed.
        var palette = new Matrix[skeleton.BoneCount];
        var inverseBind = new Numerics.Matrix4x4[skeleton.BoneCount];
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            inverseBind[i] = engineInverse[i];
            palette[i] = ToMonoGameMatrix(engineInverse[i]);
        }

        var partEffects = ReplaceWithSkinnedEffects(model);

        var hipsBoneIndex = skeleton.TryGetBoneIndex(ErikaFigure.HipsBoneName, out var hips)
            ? hips
            : -1;
        var walkStart = hipsBoneIndex >= 0
            ? RootMotionEvaluator.GetStartTranslation(walkClip, hipsBoneIndex)
            : new Numerics.Vector3();
        var runStart = hipsBoneIndex >= 0
            ? RootMotionEvaluator.GetStartTranslation(runClip, hipsBoneIndex)
            : new Numerics.Vector3();

        return new PreparedCharacter
        {
            Model = model,
            BoneTransforms = boneTransforms,
            Bounds = bounds,
            BindFacing = bindFacing,
            YawRadians = yaw,
            Correction = correction,
            EffectSummary = SummarizeEffects(model),
            TextureSummary = SummarizeSkinnedTextures(model),
            Skeleton = skeleton,
            IdleClip = idleClip,
            WalkClip = walkClip,
            RunClip = runClip,
            HipsBoneIndex = hipsBoneIndex,
            WalkStart = walkStart,
            RunStart = runStart,
            Map = map,
            Palette = palette,
            InverseBind = inverseBind,
            BindAbsolute = bindAbsolute,
            Local = new Numerics.Matrix4x4[skeleton.BoneCount],
            Absolute = new Numerics.Matrix4x4[skeleton.BoneCount],
            Skin = new Numerics.Matrix4x4[skeleton.BoneCount],
            SourceTranslations = new Numerics.Vector3[skeleton.BoneCount],
            SourceRotations = new Numerics.Quaternion[skeleton.BoneCount],
            DestinationTranslations = new Numerics.Vector3[skeleton.BoneCount],
            DestinationRotations = new Numerics.Quaternion[skeleton.BoneCount],
            PartEffects = partEffects,
            BindDeviation = deviation,
        };
    }

    private List<(ModelMesh Mesh, SkinnedEffect Effect)> ReplaceWithSkinnedEffects(Model model)
    {
        var result = new List<(ModelMesh, SkinnedEffect)>();
        foreach (var mesh in model.Meshes)
        {
            foreach (var part in mesh.MeshParts)
            {
                if (part.Effect is not BasicEffect basic)
                {
                    throw new ErikaContentException(
                        $"Mesh part of '{ErikaFigure.AssetId}' uses {part.Effect.GetType().Name}, " +
                        "expected the stock BasicEffect the model pipeline emits.",
                        new InvalidOperationException());
                }

                var skinned = new SkinnedEffect(_graphicsDevice)
                {
                    WeightsPerVertex = DetectWeightsPerVertex(part),
                    DiffuseColor = basic.DiffuseColor,
                    EmissiveColor = basic.EmissiveColor,
                    SpecularColor = basic.SpecularColor,
                    SpecularPower = basic.SpecularPower,
                    Alpha = basic.Alpha,
                    Texture = basic.TextureEnabled ? basic.Texture : null,
                };
                skinned.EnableDefaultLighting();
                part.Effect = skinned;
                result.Add((mesh, skinned));
            }
        }

        return result;
    }

    private static int DetectWeightsPerVertex(ModelMeshPart part)
    {
        foreach (var element in part.VertexBuffer.VertexDeclaration.GetVertexElements())
        {
            if (element.VertexElementUsage == VertexElementUsage.BlendWeight)
            {
                return element.VertexElementFormat switch
                {
                    VertexElementFormat.Single => 1,
                    VertexElementFormat.Vector2 => 2,
                    VertexElementFormat.Vector3 => 3,
                    _ => 4,
                };
            }
        }

        throw new ErikaContentException(
            $"Mesh part of '{ErikaFigure.AssetId}' has no blend-weight channel; " +
            "stock skinning import expected.",
            new InvalidOperationException());
    }

    private static string DescribeHipsRange(Skeleton skeleton, AnimationClip clip)
    {
        var hips = -1;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (string.Equals(skeleton.Bones[i].Name, ErikaFigure.HipsBoneName, StringComparison.Ordinal))
            {
                hips = i;
                break;
            }
        }

        if (hips < 0)
        {
            return "Hips bone absent (unexpected)";
        }

        foreach (var channel in clip.Channels)
        {
            if (channel.BoneIndex == hips && channel.HasTranslation && channel.Translations is not null)
            {
                var min = new Numerics.Vector3(float.MaxValue);
                var max = new Numerics.Vector3(float.MinValue);
                foreach (var value in channel.Translations)
                {
                    min = Numerics.Vector3.Min(min, value);
                    max = Numerics.Vector3.Max(max, value);
                }

                return $"'{clip.Name}' Hips T range X [{min.X:F2}, {max.X:F2}] Y [{min.Y:F2}, {max.Y:F2}] " +
                    $"Z [{min.Z:F2}, {max.Z:F2}] over {channel.KeyCount} keys (verbatim policy)";
            }
        }

        return $"'{clip.Name}' Hips rotation-only (no translation keys)";
    }

    private static long? TryArtifactSize(ModelAssetId asset, string extension)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Content", asset.Name + extension);
            return new FileInfo(path).Exists ? new FileInfo(path).Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static Vector3 DetectBindFacing(Model model, Matrix[] boneTransforms)
    {
        ModelBone? head = null;
        var eyes = new List<ModelBone>();
        foreach (var bone in model.Bones)
        {
            var name = bone.Name ?? string.Empty;
            if (head is null && name.EndsWith("Head", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("HeadTop", StringComparison.OrdinalIgnoreCase))
            {
                head = bone;
            }

            if (name.Contains("Eye", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("lash", StringComparison.OrdinalIgnoreCase))
            {
                eyes.Add(bone);
            }
        }

        if (head is null || eyes.Count == 0)
        {
            return Vector3.UnitZ;
        }

        var headPos = boneTransforms[head.Index].Translation;
        var eyeMid = Vector3.Zero;
        foreach (var eye in eyes)
        {
            eyeMid += boneTransforms[eye.Index].Translation;
        }

        eyeMid /= eyes.Count;
        var facing = eyeMid - headPos;
        facing.Y = 0;
        return facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : Vector3.UnitZ;
    }

    private static string SummarizeEffects(Model model)
    {
        var groups = model.Meshes
            .SelectMany(mesh => mesh.Effects)
            .GroupBy(effect => effect.GetType().Name)
            .Select(group => $"{group.Key}x{group.Count()}")
            .ToArray();
        return groups.Length == 0 ? "none" : string.Join(", ", groups);
    }

    private static string SummarizeTextures(Model model)
    {
        var withTexture = 0;
        var total = 0;
        foreach (var effect in model.Meshes.SelectMany(mesh => mesh.Effects))
        {
            if (effect is BasicEffect basic)
            {
                total++;
                if (basic.TextureEnabled && basic.Texture is not null)
                {
                    withTexture++;
                }
            }
        }

        return total == 0
            ? "no BasicEffect parts"
            : $"textured BasicEffect parts {withTexture}/{total}";
    }

    private static string SummarizeSkinnedTextures(Model model)
    {
        var withTexture = 0;
        var total = 0;
        foreach (var effect in model.Meshes.SelectMany(mesh => mesh.Effects))
        {
            if (effect is SkinnedEffect skinned)
            {
                total++;
                if (skinned.Texture is not null)
                {
                    withTexture++;
                }
            }
        }

        return total == 0
            ? "no SkinnedEffect parts"
            : $"textured SkinnedEffect parts {withTexture}/{total}";
    }

    private static float MaxAbsDiff(Matrix a, Matrix b)
    {
        var row1 = Math.Abs(a.M11 - b.M11) + Math.Abs(a.M12 - b.M12) + Math.Abs(a.M13 - b.M13) + Math.Abs(a.M14 - b.M14);
        var row2 = Math.Abs(a.M21 - b.M21) + Math.Abs(a.M22 - b.M22) + Math.Abs(a.M23 - b.M23) + Math.Abs(a.M24 - b.M24);
        var row3 = Math.Abs(a.M31 - b.M31) + Math.Abs(a.M32 - b.M32) + Math.Abs(a.M33 - b.M33) + Math.Abs(a.M34 - b.M34);
        var row4 = Math.Abs(a.M41 - b.M41) + Math.Abs(a.M42 - b.M42) + Math.Abs(a.M43 - b.M43) + Math.Abs(a.M44 - b.M44);
        return Math.Max(Math.Max(row1, row2), Math.Max(row3, row4));
    }

    private static Matrix ToMonoGameMatrix(Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);

    private static string Fmt(Vector3 value) => $"({value.X:F1}, {value.Y:F1}, {value.Z:F1})";

    private sealed class PreparedCharacter
    {
        public required Model Model { get; init; }

        public required Matrix[] BoneTransforms { get; init; }

        public required NativeBounds Bounds { get; init; }

        public required Vector3 BindFacing { get; init; }

        public required float YawRadians { get; init; }

        public required Matrix Correction { get; init; }

        public required string EffectSummary { get; init; }

        public required string TextureSummary { get; init; }

        /// <summary>Canonical shared skeleton (idle source). Null for static models.</summary>
        public Skeleton? Skeleton { get; init; }

        public AnimationClip? IdleClip { get; init; }

        public AnimationClip? WalkClip { get; init; }

        public AnimationClip? RunClip { get; init; }

        /// <summary>Canonical Hips bone index for root-motion consumption (-1 when absent).</summary>
        public int HipsBoneIndex { get; init; } = -1;

        /// <summary>Authored Hips translation at walk clip start (rendered XZ reference).</summary>
        public Numerics.Vector3 WalkStart { get; init; }

        /// <summary>Authored Hips translation at run clip start (rendered XZ reference).</summary>
        public Numerics.Vector3 RunStart { get; init; }

        public int[] Map { get; init; } = [];

        public Matrix[] Palette { get; init; } = [];

        public Numerics.Matrix4x4[] InverseBind { get; init; } = [];

        public Numerics.Matrix4x4[] BindAbsolute { get; init; } = [];

        public Numerics.Matrix4x4[] Local { get; init; } = [];

        public Numerics.Matrix4x4[] Absolute { get; init; } = [];

        public Numerics.Matrix4x4[] Skin { get; init; } = [];

        /// <summary>Reusable crossfade scratch (Phase 2F): outgoing local TRS.</summary>
        public Numerics.Vector3[] SourceTranslations { get; init; } = [];

        public Numerics.Quaternion[] SourceRotations { get; init; } = [];

        /// <summary>Reusable crossfade scratch (Phase 2F): incoming local TRS.</summary>
        public Numerics.Vector3[] DestinationTranslations { get; init; } = [];

        public Numerics.Quaternion[] DestinationRotations { get; init; } = [];

        public List<(ModelMesh Mesh, SkinnedEffect Effect)> PartEffects { get; init; } = [];

        public float BindDeviation { get; init; }

        /// <summary>
        /// Phase 2E consumes Hips horizontal travel into the character world
        /// transform (GameSession, authored displacement authority): walk/run
        /// rendered Hips X/Z are pinned to the clip start reference (Y,
        /// rotation, and limb hierarchy preserved), idle stays verbatim and
        /// stationary. No loop snap-back; N loops produce N * net displacement.
        /// </summary>
        public string RootMotionPolicy { get; init; } =
            "consumed (walk/run Hips XZ drives world transform, rendered XZ pinned to clip start; idle stationary)";

        public int MappedBones => Map.Length;

        public int PaletteSize => Palette.Length;

        public double TotalSeconds { get; set; }

        /// <summary>Resolve the active stable id to its clip (unknown ids fall back to idle).</summary>
        public AnimationClip ClipFor(string clipName)
        {
            if (string.Equals(clipName, ErikaFigure.WalkClipName, StringComparison.Ordinal))
            {
                return WalkClip!;
            }

            if (string.Equals(clipName, ErikaFigure.RunClipName, StringComparison.Ordinal))
            {
                return RunClip!;
            }

            return IdleClip!;
        }

        public string HipsRangeFor(string clipName) =>
            Skeleton is null ? string.Empty : DescribeHipsRange(Skeleton, ClipFor(clipName));

        public static PreparedCharacter Static(
            Model model,
            Matrix[] boneTransforms,
            NativeBounds bounds,
            Vector3 bindFacing,
            float yaw,
            Matrix correction,
            string effectSummary,
            string textureSummary) => new()
            {
                Model = model,
                BoneTransforms = boneTransforms,
                Bounds = bounds,
                BindFacing = bindFacing,
                YawRadians = yaw,
                Correction = correction,
                EffectSummary = effectSummary,
                TextureSummary = textureSummary,
            };
    }
}
