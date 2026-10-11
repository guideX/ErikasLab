using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2S static primitive builder. Every primitive carries deterministic
/// planar UVs alongside positions/normals/colors:
///
/// - the shared unit box and triangular prism bake unit-normalized per-face UVs
///   (each face spans [0,1]²; side faces run v along local +Y so axis-aligned
///   instances share one world-vertical orientation, and the prism slopes/bottom
///   run v along the extrusion axis);
/// - the fixed-size ground planes bake UVs in local meters (u = x, v = z), so a
///   texture repeats once per meter before any material scale.
///
/// <see cref="ApplyUvScale"/> completes the world-repeat mapping for a specific
/// instance: effective UV = baked UV × (transform axis scale × material
/// TextureScale), which yields the documented "TextureScale = repeats per world
/// meter" semantic without rebuilding or re-authoring geometry.
/// </summary>
public static class MeshFactory
{
    // Per-face UV axis convention, shared by the unit-UV baking below and
    // ApplyUvScale. Axis indices: 0 = X, 1 = Y, 2 = Z. Side faces keep v = Y
    // (world-up for axis-aligned instances); top/bottom and prism slope/bottom
    // faces use the two in-plane horizontal axes.
    private static readonly (int UAxis, int VAxis)[] BoxFaceUvAxes =
    [
        (0, 1), // +Z
        (0, 1), // -Z
        (2, 1), // -X
        (2, 1), // +X
        (0, 2), // +Y
        (0, 2), // -Y
    ];

    private static readonly (int UAxis, int VAxis)[] PrismFaceUvAxes =
    [
        (0, 1), // front cap
        (0, 1), // back cap
        (0, 2), // bottom
        (0, 2), // left slope
        (0, 2), // right slope
    ];

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
    /// convention as the Phase 1 ground. Phase 2S: UVs are baked in local
    /// meters (u = x, v = z) so a bound texture tiles once per meter before the
    /// material texture scale is applied.
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
            new MeshVertex(new Vector3(-halfWidth, 0, -halfDepth), Vector3.UnitY, color, new Vector2(-halfWidth, -halfDepth)),
            new MeshVertex(new Vector3(-halfWidth, 0, halfDepth), Vector3.UnitY, color, new Vector2(-halfWidth, halfDepth)),
            new MeshVertex(new Vector3(halfWidth, 0, halfDepth), Vector3.UnitY, color, new Vector2(halfWidth, halfDepth)),
            new MeshVertex(new Vector3(halfWidth, 0, -halfDepth), Vector3.UnitY, color, new Vector2(halfWidth, -halfDepth)),
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

        AddFace(vertices, new Vector3(-half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), Vector3.UnitZ, color, 0);
        AddFace(vertices, new Vector3(half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), -Vector3.UnitZ, color, 1);
        AddFace(vertices, new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, -half.Z), -Vector3.UnitX, color, 2);
        AddFace(vertices, new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, half.Z), Vector3.UnitX, color, 3);
        AddFace(vertices, new Vector3(-half.X, half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), Vector3.UnitY, color, 4);
        AddFace(vertices, new Vector3(-half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(-half.X, -half.Y, half.Z), -Vector3.UnitY, color, 5);

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
    /// Phase 2S: caps bake unit UVs with v along local Y; the bottom and both
    /// slope faces bake unit UVs with u along the slope run (local X) and v along
    /// the extrusion (local Z), so a roof texture maps along the pitched surface.
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
            new(new Vector3(-halfWidth, 0, halfDepth), Vector3.UnitZ, color, Vector2.Zero),
            new(new Vector3(halfWidth, 0, halfDepth), Vector3.UnitZ, color, Vector2.Zero),
            new(new Vector3(0, height, halfDepth), Vector3.UnitZ, color, Vector2.Zero),
            // Back cap (-Z)
            new(new Vector3(halfWidth, 0, -halfDepth), -Vector3.UnitZ, color, Vector2.Zero),
            new(new Vector3(-halfWidth, 0, -halfDepth), -Vector3.UnitZ, color, Vector2.Zero),
            new(new Vector3(0, height, -halfDepth), -Vector3.UnitZ, color, Vector2.Zero),
            // Bottom (-Y)
            new(new Vector3(-halfWidth, 0, -halfDepth), -Vector3.UnitY, color, Vector2.Zero),
            new(new Vector3(halfWidth, 0, -halfDepth), -Vector3.UnitY, color, Vector2.Zero),
            new(new Vector3(halfWidth, 0, halfDepth), -Vector3.UnitY, color, Vector2.Zero),
            new(new Vector3(-halfWidth, 0, halfDepth), -Vector3.UnitY, color, Vector2.Zero),
            // Left slope (outward -X/+Y)
            new(new Vector3(-halfWidth, 0, halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(0, height, halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(0, height, -halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(-halfWidth, 0, -halfDepth), Vector3.Normalize(new Vector3(-height, halfWidth, 0)), color, Vector2.Zero),
            // Right slope (outward +X/+Y)
            new(new Vector3(halfWidth, 0, halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(halfWidth, 0, -halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(0, height, -halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color, Vector2.Zero),
            new(new Vector3(0, height, halfDepth), Vector3.Normalize(new Vector3(height, halfWidth, 0)), color, Vector2.Zero),
        };

        var indices = new[]
        {
            0, 1, 2,             // front
            3, 4, 5,             // back
            6, 7, 8, 6, 8, 9,    // bottom
            10, 11, 12, 10, 12, 13, // left slope
            14, 15, 16, 14, 16, 17, // right slope
        };

        var mesh = new MeshData(vertices, indices);
        return ApplyUnitUvAxes(mesh, PrismFaceUvAxes, [3, 3, 4, 4, 4]);
    }

    /// <summary>
    /// Phase 2S world-repeat UV mapping for one primitive instance. Returns a
    /// mesh identical to <paramref name="mesh"/> except every UV is scaled to
    /// world repeats: per-face baked UV (unit-normalized for the unit box/prism,
    /// local meters for fixed-size planes) × the instance's transform axis scale
    /// × the material <c>TextureScale</c> (repeats per world meter). The result
    /// is cached by the renderer per (mesh, world scale, texture scale), so the
    /// shared unit primitives stay shared and no UV work happens per frame.
    /// </summary>
    public static MeshData ApplyUvScale(MeshData mesh, Vector3 worldScale, Vector2 textureScale)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.Vertices.Count == 0)
        {
            return mesh;
        }

        var axes = FaceUvAxesFor(mesh, out var faceVertexCounts);
        var vertices = new MeshVertex[mesh.Vertices.Count];
        var vertexOffset = 0;
        for (var face = 0; face < faceVertexCounts.Length; face++)
        {
            var (uAxis, vAxis) = axes[face];
            var uScale = Component(worldScale, uAxis) * textureScale.X;
            var vScale = Component(worldScale, vAxis) * textureScale.Y;
            for (var i = 0; i < faceVertexCounts[face]; i++)
            {
                var vertex = mesh.Vertices[vertexOffset + i];
                vertices[vertexOffset + i] = vertex with
                {
                    TexCoord = new Vector2(vertex.TexCoord.X * uScale, vertex.TexCoord.Y * vScale),
                };
            }

            vertexOffset += faceVertexCounts[face];
        }

        return new MeshData(vertices, mesh.Indices);
    }

    private static float Component(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private static (int UAxis, int VAxis)[] FaceUvAxesFor(MeshData mesh, out int[] faceVertexCounts)
    {
        // The face layout is a pure function of the vertex/index structure the
        // factory bakes: 24 vertices/36 indices = unit box (6 quad faces),
        // 18 vertices/24 indices = triangular prism (2 triangle caps + 3 quad
        // faces), 4 vertices/6 indices = ground plane (single XZ face).
        if (mesh.Vertices.Count == 24 && mesh.Indices.Count == 36)
        {
            faceVertexCounts = [4, 4, 4, 4, 4, 4];
            return BoxFaceUvAxes;
        }

        if (mesh.Vertices.Count == 18 && mesh.Indices.Count == 24)
        {
            faceVertexCounts = [3, 3, 4, 4, 4];
            return PrismFaceUvAxes;
        }

        if (mesh.Vertices.Count == 4 && mesh.Indices.Count == 6)
        {
            faceVertexCounts = [4];
            return [(0, 2)];
        }

        throw new ArgumentException("Unrecognized primitive shape; UV axes are only defined for factory primitives.", nameof(mesh));
    }

    private static void AddFace(List<MeshVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, ColorRgba color, int faceIndex)
    {
        var (uAxis, vAxis) = BoxFaceUvAxes[faceIndex];
        var faceVertices = new[] { a, b, c, d };
        var uvs = new Vector2[4];
        var uMin = Component(a, uAxis);
        var uMax = uMin;
        var vMin = Component(a, vAxis);
        var vMax = vMin;
        foreach (var point in faceVertices)
        {
            uMin = MathF.Min(uMin, Component(point, uAxis));
            uMax = MathF.Max(uMax, Component(point, uAxis));
            vMin = MathF.Min(vMin, Component(point, vAxis));
            vMax = MathF.Max(vMax, Component(point, vAxis));
        }

        var uSpan = uMax - uMin;
        var vSpan = vMax - vMin;
        for (var i = 0; i < faceVertices.Length; i++)
        {
            var point = faceVertices[i];
            uvs[i] = uSpan > 0f && vSpan > 0f
                ? new Vector2((Component(point, uAxis) - uMin) / uSpan, (Component(point, vAxis) - vMin) / vSpan)
                : Vector2.Zero;
        }

        for (var i = 0; i < faceVertices.Length; i++)
        {
            vertices.Add(new MeshVertex(faceVertices[i], normal, color, uvs[i]));
        }
    }

    private static MeshData ApplyUnitUvAxes(MeshData mesh, (int UAxis, int VAxis)[] faceAxes, int[] faceVertexCounts)
    {
        var vertices = new MeshVertex[mesh.Vertices.Count];
        var vertexOffset = 0;
        for (var face = 0; face < faceAxes.Length; face++)
        {
            var (uAxis, vAxis) = faceAxes[face];
            var count = faceVertexCounts[face];
            var uMin = float.MaxValue;
            var uMax = float.MinValue;
            var vMin = float.MaxValue;
            var vMax = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                var point = mesh.Vertices[vertexOffset + i].Position;
                uMin = MathF.Min(uMin, Component(point, uAxis));
                uMax = MathF.Max(uMax, Component(point, uAxis));
                vMin = MathF.Min(vMin, Component(point, vAxis));
                vMax = MathF.Max(vMax, Component(point, vAxis));
            }

            var uSpan = uMax - uMin;
            var vSpan = vMax - vMin;
            for (var i = 0; i < count; i++)
            {
                var vertex = mesh.Vertices[vertexOffset + i];
                var point = vertex.Position;
                vertices[vertexOffset + i] = vertex with
                {
                    TexCoord = uSpan > 0f && vSpan > 0f
                        ? new Vector2((Component(point, uAxis) - uMin) / uSpan, (Component(point, vAxis) - vMin) / vSpan)
                        : Vector2.Zero,
                };
            }

            vertexOffset += count;
        }

        return new MeshData(vertices, mesh.Indices);
    }
}
