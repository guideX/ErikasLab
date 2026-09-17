namespace ErikasLab.Engine;

/// <summary>
/// A portable placed reference to a renderable model asset. Transform is the
/// single source of placement truth (position, rotation, uniform scale); the
/// platform layer resolves <see cref="Asset"/> to its own model object.
/// </summary>
public sealed class ModelInstance
{
    public ModelInstance(ModelAssetId asset, Transform transform)
    {
        Asset = asset;
        Transform = transform;
    }

    public ModelAssetId Asset { get; }

    public Transform Transform { get; set; }
}
