using ErikasLab.Engine;
using ErikasLab.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Numerics = System.Numerics;

namespace ErikasLab.Platform.MonoGame;

/// <summary>
/// Renders <see cref="ModelInstance"/> entries of a scene in their imported
/// default pose. No animation time is advanced here (Phase 2B is static only).
///
/// The single FBX-to-world correction (uniform scale + facing yaw + ground
/// lift) is centralized in <see cref="Prepare"/>; per-frame drawing only
/// applies the cached correction times the portable instance transform.
/// </summary>
internal sealed class StaticModelRenderer
{
    private readonly ModelLibrary _library;
    private readonly Dictionary<string, PreparedModel> _prepared = new(StringComparer.Ordinal);

    public StaticModelRenderer(ModelLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>Measured native (imported) bounds, in FBX units, default pose.</summary>
    public record struct NativeBounds(Vector3 Min, Vector3 Max)
    {
        public float Height => Max.Y - Min.Y;
    }

    public void Draw(Scene scene, Matrix view, Matrix projection)
    {
        ArgumentNullException.ThrowIfNull(scene);

        foreach (var instance in scene.Models)
        {
            var prepared = GetOrPrepare(instance);
            var world = prepared.Correction * ToMonoGameMatrix(instance.Transform.WorldMatrix);

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
    }

    /// <summary>Concise startup diagnostics; does not dump every bone.</summary>
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
            lines.Add($"Erika skinning channels: {prepared.SkinningSummary}");
        }

        return lines;
    }

    private PreparedModel GetOrPrepare(ModelInstance instance)
    {
        if (_prepared.TryGetValue(instance.Asset.Name, out var prepared))
        {
            return prepared;
        }

        prepared = FromModel(_library.Get(instance.Asset));
        _prepared.Add(instance.Asset.Name, prepared);
        return prepared;
    }

    private static PreparedModel FromModel(Model model)
    {
        EnsureLighting(model);

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

        // Scale/lift come from the true bind-pose measurements centralized in
        // ErikaFigure (sphere-merged bounds above inflate the height, so they
        // are diagnostics-only).
        var scale = ErikaFigure.Scale;
        var lift = ErikaFigure.GroundLift;

        var bindFacing = DetectBindFacing(model, boneTransforms);
        var yaw = ModelPlacement.YawToFacePlusZ(
                new Numerics.Vector3(bindFacing.X, bindFacing.Y, bindFacing.Z))
            + ErikaFigure.FacingYawRadians;

        // Correction carries only the import-space fix (scale, facing, lift);
        // world placement itself comes from the portable instance transform.
        var correction =
            Matrix.CreateScale(scale) *
            Matrix.CreateRotationY(yaw) *
            Matrix.CreateTranslation(0, lift, 0);

        return new PreparedModel(
            model,
            boneTransforms,
            bounds,
            scale,
            lift,
            bindFacing,
            yaw,
            correction,
            SummarizeEffects(model),
            SummarizeTextures(model),
            SummarizeSkinning(model));
    }

    private static void EnsureLighting(Model model)
    {
        // Stock ModelProcessor effects arrive unlit; enable the same default
        // directional lighting the Phase 1 renderer uses so Erika is shaded
        // like the rest of the scene.
        foreach (var effect in model.Meshes.SelectMany(mesh => mesh.Effects))
        {
            if (effect is BasicEffect basic && !basic.LightingEnabled)
            {
                basic.LightingEnabled = true;
                basic.EnableDefaultLighting();
            }
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

    private static string SummarizeSkinning(Model model)
    {
        foreach (var part in model.Meshes.SelectMany(mesh => mesh.MeshParts))
        {
            foreach (var element in part.VertexBuffer.VertexDeclaration.GetVertexElements())
            {
                if (element.VertexElementUsage is VertexElementUsage.BlendIndices or VertexElementUsage.BlendWeight)
                {
                    return "blend-indices/weights present";
                }
            }
        }

        return "no blend channels (rigid bind-pose vertices)";
    }

    private static Matrix ToMonoGameMatrix(Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);

    private static string Fmt(Vector3 value) => $"({value.X:F1}, {value.Y:F1}, {value.Z:F1})";

    private sealed record PreparedModel(
        Model Model,
        Matrix[] BoneTransforms,
        NativeBounds Bounds,
        float Scale,
        float Lift,
        Vector3 BindFacing,
        float YawRadians,
        Matrix Correction,
        string EffectSummary,
        string TextureSummary,
        string SkinningSummary);
}
