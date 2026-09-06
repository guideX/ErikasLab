using System.Numerics;
using ErikasLab.Engine;

namespace ErikasLab.Game;

internal static class TestWorldFactory
{
    public static Scene Create()
    {
        var scene = new Scene();

        scene.Add(new SceneObject(
            "Ground",
            MeshFactory.CreateGroundPlane(50, ColorRgba.Ground),
            Transform.Identity));

        AddBox(scene, "Red Cube", new Vector3(-4, 1, -6), new Vector3(2, 2, 2), ColorRgba.Red);
        AddBox(scene, "Blue Tower", new Vector3(4, 1.5f, -11), new Vector3(2.4f, 3, 2.4f), ColorRgba.Blue);
        AddBox(scene, "Gold Cube", new Vector3(-1.5f, 0.75f, -14), new Vector3(1.5f, 1.5f, 1.5f), ColorRgba.Gold);
        AddBox(scene, "Floating Violet Cube", new Vector3(3.5f, 4, -7), new Vector3(1.5f, 1.5f, 1.5f), ColorRgba.Violet);
        AddBox(scene, "Far Teal Tower", new Vector3(-6, 2, -21), new Vector3(2.5f, 4, 2.5f), ColorRgba.Teal);

        return scene;
    }

    private static void AddBox(Scene scene, string name, Vector3 position, Vector3 size, ColorRgba color)
    {
        scene.Add(new SceneObject(
            name,
            MeshFactory.CreateBox(size, color),
            new Transform(position, Quaternion.Identity, Vector3.One)));
    }
}
