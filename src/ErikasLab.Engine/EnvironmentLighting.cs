using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// One directional light inside an <see cref="EnvironmentLighting"/> policy.
/// Direction points from the surface toward the light (MonoGame convention);
/// a zero <see cref="DiffuseColor"/> marks the light disabled.
/// </summary>
/// <param name="Direction">Toward the light (normalized by the renderer).</param>
/// <param name="DiffuseColor">Per-channel intensity in [0, 1]; zero disables the light.</param>
/// <param name="SpecularColor">Per-channel specular intensity in [0, 1].</param>
public readonly record struct DirectionalLightPolicy(Vector3 Direction, Vector3 DiffuseColor, Vector3 SpecularColor)
{
    public bool Enabled => DiffuseColor.LengthSquared() > 0f;

    public bool IsValid =>
        float.IsFinite(Direction.X) && float.IsFinite(Direction.Y) && float.IsFinite(Direction.Z) &&
        (Direction.LengthSquared() > 0f || !Enabled) &&
        IsColorFinite(DiffuseColor) && IsColorFinite(SpecularColor);

    public static bool IsColorFinite(Vector3 color) =>
        float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z) &&
        color.X >= 0f && color.Y >= 0f && color.Z >= 0f;
}

/// <summary>
/// Phase 2R centralized environment-lighting policy: one immutable description
/// of ambient plus up to three directional lights that the platform renderer
/// applies to its <see cref="BasicEffect"/>. The mood target is a cold, dim,
/// overcast Fimbul Winter exterior: cool ambient, one subdued cool key light,
/// and one faint cool fill so unlit faces never crush to pure black. Interior
/// warmth is *not* faked with extra lights; it comes from warmer interior
/// material colors plus the emissive hearth (see <c>EnvironmentMaterials</c>).
/// </summary>
public readonly record struct EnvironmentLighting(
    Vector3 AmbientLightColor,
    DirectionalLightPolicy Directional0,
    DirectionalLightPolicy Directional1,
    DirectionalLightPolicy Directional2)
{
    /// <summary>
    /// The Fimbul Winter foundation policy: dark cool ambient, a subdued cool
    /// key from above-side, and a faint cool fill from the opposite side.
    /// All values finite; directions nonzero. Tuned so the longhouse exterior
    /// reads as a dark but visible silhouette against the forest while the
    /// interior stays warm through materials and the emissive hearth.
    /// </summary>
    public static EnvironmentLighting FimbulWinter => new(
        new Vector3(0.20f, 0.23f, 0.28f),
        new DirectionalLightPolicy(
            new Vector3(-0.45f, -1f, -0.35f),
            new Vector3(0.62f, 0.70f, 0.82f),
            Vector3.Zero),
        new DirectionalLightPolicy(
            new Vector3(0.55f, -0.45f, 0.60f),
            new Vector3(0.10f, 0.11f, 0.14f),
            Vector3.Zero),
        new DirectionalLightPolicy(Vector3.Zero, Vector3.Zero, Vector3.Zero));

    public bool IsValid =>
        DirectionalLightPolicy.IsColorFinite(AmbientLightColor) &&
        Directional0.IsValid && Directional1.IsValid && Directional2.IsValid;
}
