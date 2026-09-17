using System.Numerics;
using ErikasLab.Engine;

namespace ErikasLab.Game;

/// <summary>
/// Phase 2B static-figure definition for canonical Erika. Portable: no MonoGame
/// types. Native (imported) measurements are taken at runtime by the platform
/// layer from the loaded model; this type declares intent (which asset, how
/// tall, where, facing which way). See README.md ("Erika content") and
/// docs/ERIKA_ASSET_AUDIT.md.
/// </summary>
public static class ErikaFigure
{
    /// <summary>Canonical static-render source (docs/ERIKA_ASSET_AUDIT.md section 4).</summary>
    public const string SourceFile = "erika/idle_looking_around.fbx";

    /// <summary>World convention: 1 game unit = 1 meter.</summary>
    public const float TargetHeightMeters = 1.7f;

    /// <summary>
    /// True bind-pose height in FBX units, measured offline from the Body mesh
    /// control points (Y -0.6 .. 179.5). Runtime mesh bounding spheres inflate
    /// this to ~238 units, so the spheres are diagnostics-only; this constant
    /// drives the world scale. See README.md ("Erika content").
    /// </summary>
    public const float NativeHeightUnits = 180.1f;

    /// <summary>
    /// True bind-pose lower bound in FBX units (Body control-point minimum).
    /// </summary>
    public const float NativeGroundUnits = -0.6f;

    /// <summary>Uniform world scale: native units to meters.</summary>
    public static float Scale =>
        ModelPlacement.UniformScaleForTargetHeight(NativeHeightUnits, TargetHeightMeters);

    /// <summary>World-space Y that rests her feet on the ground plane.</summary>
    public static float GroundLift => ModelPlacement.LiftToGround(NativeGroundUnits, Scale);

    /// <summary>
    /// Where Erika stands: front-center of the Phase 1 test scene, fully in
    /// the default camera frustum (camera at z=9.5 looking -Z).
    /// </summary>
    public static readonly Vector3 GroundPosition = new(0, 0, -1.0f);

    /// <summary>
    /// Yaw applied to the imported model so she faces world +Z (toward the
    /// Phase 1 camera start). Chosen from measured bind-pose facing (eyes vs
    /// head, reported in startup diagnostics); 0 means the import already
    /// faces +Z.
    /// </summary>
    public const float FacingYawRadians = 0f;

    public static ModelAssetId AssetId => new("erika/idle_looking_around");

    /// <summary>Phase 2C animation artifact built from the same source.</summary>
    public static ModelAssetId ClipAssetId => new("erika/erika_idle");

    /// <summary>Bone carrying the clip's root translation (importer form).</summary>
    public const string HipsBoneName = "mixamorig:Hips";

    public static ModelInstance CreateInstance() =>
        new(AssetId, new Transform(GroundPosition, Quaternion.Identity, Vector3.One));
}
