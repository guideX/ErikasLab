using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2L centralized blockout layout for Erika's home area: a Viking-style
/// longhouse in a small forest clearing. This type is the single source of
/// truth for every environment dimension and deterministic placement rule, so
/// the geometry builder and the tests derive transforms from the same numbers
/// instead of scattering raw coordinates. All values are world-space meters
/// (1 unit = 1 meter); the longhouse is centered on <see cref="Origin"/>, its
/// long axis runs along Z, and the doorway is in the +Z end wall so the
/// default third-person spawn (facing -Z) looks straight at the entrance.
///
/// This is a layout prototype, not final art: every constant is expected to be
/// replaced when real textures/models arrive. Gameplay code must not depend on
/// primitive mesh identities.
/// </summary>
public static class LonghouseLayout
{
    // --- world placement -------------------------------------------------

    /// <summary>Longhouse center on the ground plane.</summary>
    public static readonly Vector3 Origin = Vector3.Zero;

    /// <summary>
    /// Erika's deliberate Phase 2L spawn: on the clearing just outside the
    /// front (+Z) doorway. She faces -Z (the Phase 2H-derived initial control
    /// forward), i.e. straight at the entrance, so both the exterior and the
    /// doorway are immediately readable.
    /// </summary>
    public static readonly Vector3 SpawnPosition = new(0f, 0f, 12f);

    /// <summary>
    /// Yaw (radians) Erika spawns with. Equals the default third-person control
    /// forward heading (<c>atan2(0, -1)</c>), matching the Phase 2H derivation
    /// rather than a hard-coded turn.
    /// </summary>
    public static readonly float SpawnFacingYawRadians = MathF.PI;

    // --- longhouse footprint ---------------------------------------------

    /// <summary>Long axis length along Z (meters).</summary>
    public const float Length = 16f;

    /// <summary>Short axis width along X (meters).</summary>
    public const float Width = 6f;

    /// <summary>Wall thickness (meters).</summary>
    public const float WallThickness = 0.25f;

    /// <summary>Eave / wall-top height (meters).</summary>
    public const float WallHeight = 2.6f;

    /// <summary>Roof ridge height (meters). Must exceed <see cref="WallHeight"/>.</summary>
    public const float RidgeHeight = 5f;

    // --- doorway (front +Z end wall) -------------------------------------

    /// <summary>Clear doorway width (meters).</summary>
    public const float DoorWidth = 1.2f;

    /// <summary>Clear doorway height (meters).</summary>
    public const float DoorHeight = 2f;

    // --- roof ------------------------------------------------------------

    /// <summary>Roof slab thickness (meters).</summary>
    public const float RoofThickness = 0.18f;

    /// <summary>Roof overhang past each long wall (meters).</summary>
    public const float RoofOverhangSide = 0.5f;

    /// <summary>Roof overhang past each gable end (meters).</summary>
    public const float RoofOverhangEnd = 0.5f;

    // --- timber structure ------------------------------------------------

    /// <summary>Square cross-section of posts (meters).</summary>
    public const float PostSize = 0.28f;

    /// <summary>Nominal spacing between repeated side/interior posts (meters).</summary>
    public const float PostSpacing = 3f;

    /// <summary>Cross-section of beams and rafters (meters).</summary>
    public const float BeamSize = 0.18f;

    // --- interior: hearth -------------------------------------------------

    /// <summary>Hearth length along the longhouse axis (meters).</summary>
    public const float HearthLength = 4f;

    /// <summary>Hearth width across the longhouse axis (meters).</summary>
    public const float HearthWidth = 1.4f;

    /// <summary>Hearth center Z (meters); pushed toward the rear to keep the entry clear.</summary>
    public const float HearthCenterZ = -1.5f;

    /// <summary>Stone rim height above the floor (meters).</summary>
    public const float HearthRimHeight = 0.28f;

    /// <summary>Stone rim thickness (meters).</summary>
    public const float HearthRimThickness = 0.2f;

    // --- interior: furniture ---------------------------------------------

    /// <summary>Bench seat height (meters).</summary>
    public const float BenchHeight = 0.45f;

    /// <summary>Bench depth from the wall (meters).</summary>
    public const float BenchDepth = 0.5f;

    /// <summary>Bench length along the long walls (meters).</summary>
    public const float BenchLength = 14f;

    /// <summary>Gap between a bench and its wall (meters).</summary>
    public const float BenchInset = 0.35f;

    /// <summary>Table top height (meters).</summary>
    public const float TableHeight = 0.75f;

    /// <summary>Table width across the longhouse axis (meters).</summary>
    public const float TableWidth = 0.8f;

    /// <summary>Table length along the longhouse axis (meters).</summary>
    public const float TableLength = 3.5f;

    // --- ground / clearing / forest --------------------------------------

    /// <summary>Dark forest-floor plane size (meters).</summary>
    public const float ForestGroundSize = 140f;

    /// <summary>Square clearing plane size (meters); the forest ring sits outside it.</summary>
    public const float ClearingSize = 36f;

    /// <summary>Longhouse interior floor inset from the walls (meters).</summary>
    public const float FloorMargin = 0.25f;

    /// <summary>Radius of the deterministic tree ring around the clearing (meters).</summary>
    public const float TreeRingRadius = 26f;

    /// <summary>Number of blockout trees in the perimeter ring.</summary>
    public const int TreeCount = 24;

    /// <summary>Tree trunk width (meters).</summary>
    public const float TreeTrunkWidth = 0.5f;

    /// <summary>Tree trunk height to the first canopy tier (meters).</summary>
    public const float TreeTrunkHeight = 5.5f;

    // --- derived geometry -------------------------------------------------

    /// <summary>Half the long axis (meters).</summary>
    public static float HalfLength => Length / 2f;

    /// <summary>Half the short axis (meters).</summary>
    public static float HalfWidth => Width / 2f;

    /// <summary>World Z of the front (doorway) end wall.</summary>
    public static float FrontZ => Origin.Z + HalfLength;

    /// <summary>World Z of the rear end wall.</summary>
    public static float RearZ => Origin.Z - HalfLength;

    /// <summary>Roof half-span including the side overhang (meters).</summary>
    public static float RoofHalfSpanX => HalfWidth + RoofOverhangSide;

    /// <summary>Vertical rise from eave to ridge (meters); always positive.</summary>
    public static float RoofRise => RidgeHeight - WallHeight;

    /// <summary>Roof pitch angle from horizontal (radians).</summary>
    public static float RoofSlopeAngleRadians => MathF.Atan2(RoofRise, RoofHalfSpanX);

    /// <summary>Slope length from eave to ridge (meters).</summary>
    public static float RoofSlopeLength =>
        MathF.Sqrt(RoofHalfSpanX * RoofHalfSpanX + RoofRise * RoofRise);

    /// <summary>Roof slab length including the end overhangs (meters).</summary>
    public static float RoofLengthZ => Length + 2f * RoofOverhangEnd;

    /// <summary>Y of a roof slab's centerline (midway between eave and ridge).</summary>
    public static float RoofSlabCenterY => WallHeight + RoofRise / 2f;

    /// <summary>Number of repeated side/interior posts along one long wall.</summary>
    public static int SidePostCount => (int)(Length / PostSpacing);

    /// <summary>Y of the longhouse interior floor top (meters).</summary>
    public const float FloorTopY = 0.01f;

    /// <summary>Y of the dark forest-floor plane (meters).</summary>
    public const float ForestGroundY = -0.06f;

    /// <summary>Deterministic Z of repeated side/interior post <paramref name="index"/>.</summary>
    public static float SidePostZ(int index) =>
        Origin.Z + (index - (SidePostCount - 1) / 2f) * PostSpacing;

    /// <summary>Deterministic world position of blockout tree <paramref name="index"/>.</summary>
    public static Vector3 TreePosition(int index)
    {
        var angle = index * (MathF.Tau / TreeCount) + 0.13f * MathF.Sin(index * 1.7f);
        var radius = TreeRingRadius + 2.5f * MathF.Sin(index * 2.3f + 0.7f);
        return new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius) + Origin;
    }

    /// <summary>Deterministic uniform scale for blockout tree <paramref name="index"/>.</summary>
    public static float TreeScale(int index) =>
        0.9f + 0.2f * (0.5f + 0.5f * MathF.Sin(index * 1.1f + 0.3f));

    /// <summary>Deterministic yaw (radians) for blockout tree <paramref name="index"/>.</summary>
    public static float TreeYawRadians(int index) => 0.7f * MathF.Sin(index * 0.9f);

    /// <summary>
    /// True when a horizontal point lies within the longhouse footprint
    /// (expanded by <paramref name="margin"/> meters). Y is ignored.
    /// </summary>
    public static bool IsInsideFootprint(Vector3 point, float margin = 0f) =>
        MathF.Abs(point.X - Origin.X) <= HalfWidth + margin &&
        MathF.Abs(point.Z - Origin.Z) <= HalfLength + margin;
}
