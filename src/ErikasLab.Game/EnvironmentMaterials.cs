using ErikasLab.Engine;

namespace ErikasLab.Game;

/// <summary>
/// Phase 2R centralized environment material palette: one immutable
/// <see cref="StaticMaterial"/> per environment category, reused by every
/// <see cref="SceneObject"/> in that category. The longhouse reads as one
/// coherent material family — deep structural timber, muted weathered wall
/// planks, dark mossy turf roof, warm interior wood, dark hearth stone with a
/// warm emissive ember bed — against a dark cool forest with a slightly more
/// readable clearing.
///
/// These are placeholder material treatments, not final art: no texture ids are
/// set anywhere yet (the repository ships no environment textures), so every
/// material renders through its base/emissive colors. Future wood/turf/stone/
/// ground textures drop in by setting <see cref="StaticMaterial.TextureId"/>
/// (plus UVs on the shared primitives) without touching layout, collision, or
/// gameplay.
/// </summary>
public static class EnvironmentMaterials
{
    /// <summary>
    /// Deep, low-saturation brown for posts, tie beams, rafters, the ridge
    /// beam, and the door frame — noticeably darker and richer than the Phase
    /// 2L blockout timber so the frame reads as heavy Norse construction.
    /// </summary>
    public static StaticMaterial StructuralTimber { get; } = new()
    {
        BaseColor = new ColorRgba(56, 38, 24),
    };

    /// <summary>
    /// Muted, weathered grey-brown plank material for the wall shells and gable
    /// ends: lighter and less saturated than the structural timber so walls
    /// visually separate from the framing.
    /// </summary>
    public static StaticMaterial WallPlanks { get; } = new()
    {
        BaseColor = new ColorRgba(88, 76, 60),
    };

    /// <summary>
    /// Dark mossy earth/turf tone for the roof slabs: a subtle green-brown
    /// mixture, visually heavier (darker) than the walls, never a bright
    /// saturated green.
    /// </summary>
    public static StaticMaterial RoofTurf { get; } = new()
    {
        BaseColor = new ColorRgba(44, 48, 32),
    };

    /// <summary>
    /// Warm dark wood for the longhouse interior floor — the warmest large
    /// interior surface, anchoring the warm/cool separation from the forest.
    /// </summary>
    public static StaticMaterial InteriorFloor { get; } = new()
    {
        BaseColor = new ColorRgba(72, 50, 32),
    };

    /// <summary>
    /// Muted moss green-brown clearing ground: somewhat lighter and more
    /// readable than the deep forest floor so the traversable space around the
    /// longhouse reads as a clearing.
    /// </summary>
    public static StaticMaterial ClearingGround { get; } = new()
    {
        BaseColor = new ColorRgba(62, 74, 52),
    };

    /// <summary>
    /// Dark, cool forest-floor green-grey: darker and less inviting than the
    /// clearing, strengthening the boundary between home ground and deep wood.
    /// </summary>
    public static StaticMaterial ForestGround { get; } = new()
    {
        BaseColor = new ColorRgba(28, 34, 28),
    };

    /// <summary>Dark grey hearth-stone for the rim surrounding the ember bed.</summary>
    public static StaticMaterial HearthStone { get; } = new()
    {
        BaseColor = new ColorRgba(66, 64, 60),
    };

    /// <summary>
    /// The hearth focal point: a dark base with a restrained warm orange
    /// emissive so the ember bed glows under the cool scene lighting without
    /// any light transport, particles, or flicker.
    /// </summary>
    public static StaticMaterial HearthEmbers { get; } = new()
    {
        BaseColor = new ColorRgba(46, 20, 10),
        EmissiveColor = new ColorRgba(240, 104, 32),
    };

    /// <summary>
    /// Medium brown for benches and tables: clearly separate from both the
    /// deep structural timber and the muted wall planks.
    /// </summary>
    public static StaticMaterial Furniture { get; } = new()
    {
        BaseColor = new ColorRgba(98, 70, 42),
    };

    /// <summary>Dark brown tree trunks.</summary>
    public static StaticMaterial TreeTrunk { get; } = new()
    {
        BaseColor = new ColorRgba(42, 32, 24),
    };

    /// <summary>Deep muted green canopy tiers — dark, never neon.</summary>
    public static StaticMaterial TreeCanopy { get; } = new()
    {
        BaseColor = new ColorRgba(22, 38, 24),
    };

    /// <summary>Every environment category material, for diagnostics and tests.</summary>
    public static StaticMaterial[] All =>
    [
        StructuralTimber,
        WallPlanks,
        RoofTurf,
        InteriorFloor,
        ClearingGround,
        ForestGround,
        HearthStone,
        HearthEmbers,
        Furniture,
        TreeTrunk,
        TreeCanopy,
    ];
}
