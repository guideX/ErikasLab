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

        return CreateGroundRectangle(size, size, color);
    }

    /// <summary>
    /// Phase 2L rectangular ground/floor plane in the XZ plane, normal +Y,
    /// centered on the local origin. Reuses the proven ground-plane winding so
    /// the longhouse floor and clearing render with the same front-face
    /// convention as the Phase 1 ground.
    /// </summary>
    public static MeshData CreateGroundRectangle(float width, float depth, ColorRgba color)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The ground width must be positive.");
        }

        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "The ground depth must be positive.");
        }

        var halfWidth = width / 2;
        var halfDepth = depth / 2;
        var vertices = new[]
        {
            new MeshVertex(new Vector3(-halfWidth, 0, -halfDepth), Vector3.UnitY, color),
            new MeshVertex(new Vector3(-halfWidth, 0, halfDepth), Vector3.UnitY, color),
            new MeshVertex(new Vector3(halfWidth, 0, halfDepth), Vector3.UnitY, color),
            new MeshVertex(new Vector3(halfWidth, 0, -halfDepth), Vector3.UnitY, color),
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

    /// <summary>
    /// Phase 2L gable/roof triangular prism. The triangular cross-section lies
    /// in the local XY plane (base width along X at y=0, apex at
    /// <c>(0, size.Y)</c>) and is extruded along Z by <c>size.Z</c>, centered on
    /// the local origin. Face winding matches the box convention (right-hand
    /// outward normal) so the gable ends render with the same front-face rule.
    /// </summary>
    public static MeshData CreateTriangularPrism(Vector3 size, ColorRgba color)
    {
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Prism dimensions must be positive.");
        }

        var halfWidth = size.X / 2;
        var halfDepth = size.Z / 2;
        var height = size.Y;

        var vertices = new List<MeshVertex>(18)
        {
            // Front cap (+Z)
            new(new Vector3(-halfWidth, 0, halfDepth), Vector3.UnitZ, color),
            new(new Vector3(halfWidth, 0, halfDepth), Vector3.UnitZ, color),
            new(new Vector3(0, height, halfDepth), Vector3.UnitZ, color),
            // Back cap (-Z)
            new(new Vector3(halfWidth, 0, -halfDepth), -Vector3.UnitZ, color),
            new(new Vector3(-halfWidth, 0, -halfDepth), -Vector3.UnitZ, color),
            new(new Vector3(0, height, -halfDepth), -Vector3.UnitZ, color),
            // Bottom (-Y)
            new(new Vector3(-halfWidth, 0, -halfDepth), -Vector3.UnitY, color),
            new(new Vector3(halfWidth, 0, -halfDepth), -Vector3.UnitY, color),
            new(new Vector3(halfWidth, 0, halfDepth), -Vector3.UnitY, color),
            new(new Vector3(-halfWidth, 0, halfDepth), -Vector3.UnitY, color),
            // Left slope (outward -X/+Y)
            new(new Vector3(-halfWidth, 0, halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color),
            new(new Vector3(0, height, halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color),
            new(new Vector3(0, height, -halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color),
            new(new Vector3(-halfWidth, 0, -halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color),
            // Right slope (outward +X/+Y)
            new(new Vector3(halfWidth, 0, halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color),
            new(new Vector3(halfWidth, 0, -halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color),
            new(new Vector3(0, height, -halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color),
            new(new Vector3(0, height, halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color),
        };

        var indices = new[]
        {
            0, 1, 2,             // front
            3, 4, 5,             // back
            6, 7, 8, 6, 8, 9,    // bottom
            10, 11, 12, 10, 12, 13, // left slope
            14, 15, 16, 14, 16, 17, // right slope
        };

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
