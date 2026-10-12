namespace ErikasLab.Game;

/// <summary>
/// Phase 2S centralized environment texture-id slots: the deterministic ids the
/// environment materials reference. Phase 2T connects real art for six of the
/// nine slots via runtime PNGs under <c>Content/environment/</c> (see
/// <c>textures/</c> for the committed sources); clearing ground, forest ground,
/// and tree bark ship no suitable asset yet and keep rendering through the
/// Phase 2R base colors.
///
/// To replace an asset later: overwrite the PNG at
/// <c>Content/environment/{file-name}.png</c> — the MonoGame host loads every
/// <c>*.png</c> under <c>Content/environment/</c> once at startup and registers
/// each file under <c>environment/{file-name without extension}</c>, matching
/// the ids below — or register the id with the renderer through any other
/// content path. No layout, collision, material, or gameplay change is needed;
/// the material keeps its Phase 2R base color as a tint/fallback around the
/// bound texture.
///
/// The ember bed and the tree canopy intentionally have no slot: the ember bed
/// stays an untextured emissive focal point, and flat-color canopy stays
/// acceptable until alpha-tested foliage rendering exists.
/// </summary>
public static class EnvironmentTextureIds
{
    public const string StructuralTimber = "environment/structural_timber";
    public const string WallPlanks = "environment/wall_planks";
    public const string RoofTurf = "environment/roof_turf";
    public const string HearthStone = "environment/hearth_stone";
    public const string InteriorFloor = "environment/interior_floor";
    public const string ClearingGround = "environment/clearing_ground";
    public const string ForestGround = "environment/forest_ground";
    public const string Furniture = "environment/furniture_wood";
    public const string TreeTrunk = "environment/tree_bark";

    /// <summary>Every ready-to-fill environment texture slot, for diagnostics and tests.</summary>
    public static string[] All =>
    [
        StructuralTimber,
        WallPlanks,
        RoofTurf,
        HearthStone,
        InteriorFloor,
        ClearingGround,
        ForestGround,
        Furniture,
        TreeTrunk,
    ];
}
