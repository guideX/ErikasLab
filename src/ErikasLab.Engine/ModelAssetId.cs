namespace ErikasLab.Engine;

/// <summary>
/// Portable identifier for a renderable model asset built through the
/// platform content pipeline (e.g. an MGCB asset name such as
/// "erika/idle_looking_around"). Carries no MonoGame types.
/// </summary>
public readonly record struct ModelAssetId(string Name)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("A model asset needs a non-empty name.", nameof(Name))
        : Name;

    public override string ToString() => Name;
}
