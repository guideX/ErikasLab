using System.Numerics;

namespace ErikasLab.Engine;

public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, ColorRgba Color);

public sealed class MeshData
{
    public MeshData(IReadOnlyList<MeshVertex> vertices, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        if (vertices.Count == 0)
        {
            throw new ArgumentException("A mesh must contain at least one vertex.", nameof(vertices));
        }

        if (indices.Count == 0 || indices.Count % 3 != 0)
        {
            throw new ArgumentException("A triangle-list mesh must contain a non-zero multiple of three indices.", nameof(indices));
        }

        if (indices.Any(index => index < 0 || index >= vertices.Count))
        {
            throw new ArgumentException("Mesh indices must reference an existing vertex.", nameof(indices));
        }

        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
    }

    public IReadOnlyList<MeshVertex> Vertices { get; }

    public IReadOnlyList<int> Indices { get; }
}
