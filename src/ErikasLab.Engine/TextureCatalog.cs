namespace ErikasLab.Engine;

/// <summary>
/// Phase 2R portable registry of texture ids that have real pixel data behind
/// them. The renderer asks the catalog whether a material's
/// <see cref="StaticMaterial.TextureId"/> can be honored before binding, so the
/// "texture requested but absent" fallback decision is portable and testable
/// without a GPU; the platform renderer keeps the actual GPU texture objects
/// keyed by the same ids.
///
/// The catalog starts empty (Phase 2R ships no environment textures) and is
/// meant to be populated once per texture asset, never per frame.
/// </summary>
public sealed class TextureCatalog
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public int Count => _ids.Count;

    public void Register(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A texture id must be non-blank.", nameof(id));
        }

        _ids.Add(id);
    }

    public bool Contains(string id) =>
        !string.IsNullOrWhiteSpace(id) && _ids.Contains(id);

    /// <summary>
    /// Decide whether a material may render textured: it must request a texture
    /// and the catalog must know that id. Anything else falls back to the
    /// material's base color (untextured materials are always safe).
    /// </summary>
    public bool CanUseTexture(StaticMaterial material) =>
        material.TextureEnabled && Contains(material.TextureId!);
}
