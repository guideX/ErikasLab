using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

public sealed class ModelSceneTests
{
    [Fact]
    public void AssetIdKeepsName()
    {
        var asset = new ModelAssetId("erika/idle_looking_around");
        Assert.Equal("erika/idle_looking_around", asset.Name);
        Assert.Equal("erika/idle_looking_around", asset.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AssetIdRejectsEmptyName(string name)
    {
        Assert.Throws<ArgumentException>(() => new ModelAssetId(name));
    }

    [Fact]
    public void SceneHoldsModelInstancesAlongsideMeshes()
    {
        var scene = new Scene();
        var mesh = MeshFactory.CreateBox(new Vector3(1, 1, 1), ColorRgba.White);
        scene.Add(new SceneObject("Box", mesh, Transform.Identity));

        var instance = new ModelInstance(
            new ModelAssetId("erika/idle_looking_around"),
            new Transform(new Vector3(0, 0, -1), Quaternion.Identity, Vector3.One));
        scene.AddModel(instance);

        Assert.Single(scene.Objects);
        var stored = Assert.Single(scene.Models);
        Assert.Equal("erika/idle_looking_around", stored.Asset.Name);
        Assert.Equal(new Vector3(0, 0, -1), stored.Transform.Position);
    }

    [Fact]
    public void SceneRejectsNullModel()
    {
        Assert.Throws<ArgumentNullException>(() => new Scene().AddModel(null!));
    }

    [Fact]
    public void TransformWorldMatrixAppliesScaleRotationTranslation()
    {
        var transform = new Transform(
            new Vector3(0, 0.006f, -1),
            Quaternion.Identity,
            new Vector3(0.00944f));
        var world = transform.WorldMatrix;
        var origin = Vector3.Transform(Vector3.Zero, world);
        Assert.Equal(new Vector3(0, 0.006f, -1), origin);

        var up = Vector3.TransformNormal(Vector3.UnitY, world);
        Assert.Equal(0.00944f, up.Length(), precision: 5);
    }
}
