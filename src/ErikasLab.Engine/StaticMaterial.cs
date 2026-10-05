using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2R minimal static-world material description. One immutable value type
/// shared by many <see cref="SceneObject"/>s: material identity is separate from
/// mesh identity, so a whole environment can reuse a handful of materials and a
/// future textured material can replace a placeholder without rebuilding layout.
///
/// This is deliberately not a material graph, shader graph, or PBR framework:
/// base color + emissive color + optional texture slot + lighting toggle, which
/// is everything <see cref="BasicEffect"/> consumes.
/// </summary>
public readonly record struct StaticMaterial
{
    /// <summary>
    /// Untextured, unlit-safe default: plain white diffuse, no emissive
    /// contribution, lighting enabled. Valid for any geometry; the environment
    /// factory assigns explicit category materials instead of relying on this.
    /// </summary>
    public static StaticMaterial Default => new();

    /// <summary>Diffuse surface color (bytes; always finite and in [0, 255]).</summary>
    public ColorRgba BaseColor { get; init; }

    /// <summary>
    /// Emissive surface color (bytes; always finite and non-negative). Added on
    /// top of the lit result, so a warm hearth bed glows without any light
    /// transport. Black (the default) contributes nothing.
    /// </summary>
    public ColorRgba EmissiveColor { get; init; }

    /// <summary>
    /// Optional texture reference. Null (the default) means the material is
    /// untextured and renders with <see cref="BaseColor"/> alone; a non-null id
    /// marks the material as textured and the renderer binds the matching
    /// registered texture, falling back to <see cref="BaseColor"/> when the id
    /// is unavailable (never a crash).
    /// </summary>
    public string? TextureId { get; init; }

    /// <summary>World-space UV tiling applied when a texture is bound (1 = one texture repeat per meter).</summary>
    public Vector2 TextureScale { get; init; }

    /// <summary>True when this material requests a texture bind.</summary>
    public bool TextureEnabled => TextureId is not null;

    /// <summary>
    /// When false the surface renders with its base/emissive colors only and
    /// ignores scene lighting (reserved for special unlit surfaces; the
    /// environment keeps lighting enabled everywhere).
    /// </summary>
    public bool LightingEnabled { get; init; } = true;

    public StaticMaterial()
    {
        BaseColor = ColorRgba.White;
        EmissiveColor = new ColorRgba(0, 0, 0);
        TextureId = null;
        TextureScale = Vector2.One;
        LightingEnabled = true;
    }

    /// <summary>Validate that every field holds a finite, usable value.</summary>
    public bool IsValid =>
        float.IsFinite(TextureScale.X) && float.IsFinite(TextureScale.Y) &&
        TextureScale.X > 0f && TextureScale.Y > 0f &&
        (TextureId is null || !string.IsNullOrWhiteSpace(TextureId));
}
