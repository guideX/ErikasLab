using System.Numerics;

namespace ErikasLab.Engine;

public static class MeshFactory
{
    public static MeshData CreateGroundPlane(float size, ColorRgba color)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The ground plane size must be positive.");
        }

        var halfSize = size / 2;
        var vertices = new[]
        {
            new MeshVertex(new Vector3(-halfSize, 0, -halfSize), Vector3.UnitY, color),
            new MeshVertex(new Vector3(-halfSize, 0, halfSize), Vector3.UnitY, color),
            new MeshVertex(new Vector3(halfSize, 0, halfSize), Vector3.UnitY, color),
            new MeshVertex(new Vector3(halfSize, 0, -halfSize), Vector3.UnitY, color),
        };

        // Winding is clockwise-on-screen (MonoGame DirectX front-face convention
        // with back-face culling); the previous counter-clockwise order was
        // silently culled, so the Phase 1 ground never rendered.
        return new MeshData(vertices, [0, 2, 1, 0, 3, 2]);
    }

    public static MeshData CreateBox(Vector3 size, ColorRgba color)
    {
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Box dimensions must be positive.");
        }

        var half = size / 2;
        var vertices = new List<MeshVertex>(24);

        AddFace(vertices, new Vector3(-half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), Vector3.UnitZ, color);
        AddFace(vertices, new Vector3(half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), -Vector3.UnitZ, color);
        AddFace(vertices, new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, -half.Z), -Vector3.UnitX, color);
        AddFace(vertices, new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, half.Z), Vector3.UnitX, color);
        AddFace(vertices, new Vector3(-half.X, half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), Vector3.UnitY, color);
        AddFace(vertices, new Vector3(-half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(-half.X, -half.Y, half.Z), -Vector3.UnitY, color);

        var indices = new int[36];
        for (var face = 0; face < 6; face++)
        {
            var vertexOffset = face * 4;
            var indexOffset = face * 6;
            indices[indexOffset] = vertexOffset;
            indices[indexOffset + 1] = vertexOffset + 1;
            indices[indexOffset + 2] = vertexOffset + 2;
            indices[indexOffset + 3] = vertexOffset;
            indices[indexOffset + 4] = vertexOffset + 2;
            indices[indexOffset + 5] = vertexOffset + 3;
        }

        return new MeshData(vertices, indices);
    }

    private static void AddFace(List<MeshVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, ColorRgba color)
    {
        vertices.Add(new MeshVertex(a, normal, color));
        vertices.Add(new MeshVertex(b, normal, color));
        vertices.Add(new MeshVertex(c, normal, color));
        vertices.Add(new MeshVertex(d, normal, color));
    }
}
