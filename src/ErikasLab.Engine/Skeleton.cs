using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// One joint of a portable skeleton: bind (rest) local transform plus the
/// hierarchy link. No MonoGame types.
/// </summary>
public readonly record struct SkeletonBone(string Name, int ParentIndex, Vector3 BindTranslation, Quaternion BindRotation)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("A skeleton bone needs a non-empty name.", nameof(Name))
        : Name;
}

/// <summary>
/// Portable joint hierarchy. Bone order is not required to be topological:
/// <see cref="ResolveAbsolute"/> handles arbitrary ordering (and rejects cycles).
/// </summary>
public sealed class Skeleton
{
    public const int NoParent = -1;

    private readonly Dictionary<string, int> _indexByName;

    public Skeleton(IReadOnlyList<SkeletonBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        if (bones.Count == 0)
        {
            throw new ArgumentException("A skeleton needs at least one bone.", nameof(bones));
        }

        _indexByName = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);
        for (var i = 0; i < bones.Count; i++)
        {
            if (!_indexByName.TryAdd(bones[i].Name, i))
            {
                throw new ArgumentException($"Duplicate skeleton bone name '{bones[i].Name}'.", nameof(bones));
            }

            var parent = bones[i].ParentIndex;
            if (parent != NoParent && (parent < 0 || parent >= bones.Count || parent == i))
            {
                throw new ArgumentException(
                    $"Bone '{bones[i].Name}' has invalid parent index {parent}.", nameof(bones));
            }
        }

        Bones = bones.ToArray();
    }

    public IReadOnlyList<SkeletonBone> Bones { get; }

    public int BoneCount => Bones.Count;

    public bool TryGetBoneIndex(string name, out int index) =>
        _indexByName.TryGetValue(name, out index);

    public int GetBoneIndex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_indexByName.TryGetValue(name, out var index))
        {
            throw new KeyNotFoundException($"Skeleton has no bone named '{name}'.");
        }

        return index;
    }

    /// <summary>Bind-pose local matrices (rotation then translation, no scale).</summary>
    public Matrix4x4[] ComputeBindLocalMatrices()
    {
        var result = new Matrix4x4[BoneCount];
        for (var i = 0; i < BoneCount; i++)
        {
            result[i] = Compose(Bones[i].BindRotation, Bones[i].BindTranslation);
        }

        return result;
    }

    /// <summary>
    /// Absolute (model-space) matrices from local ones. Order-independent:
    /// resolves parents first regardless of bone order; throws on cycles.
    /// </summary>
    public void ResolveAbsolute(Matrix4x4[] local, Matrix4x4[] absolute)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(absolute);
        if (local.Length != BoneCount || absolute.Length != BoneCount)
        {
            throw new ArgumentException("Matrix arrays must match the bone count.");
        }

        var resolved = new bool[BoneCount];
        var remaining = BoneCount;
        while (remaining > 0)
        {
            var progressed = false;
            for (var i = 0; i < BoneCount; i++)
            {
                if (resolved[i])
                {
                    continue;
                }

                var parent = Bones[i].ParentIndex;
                if (parent == NoParent)
                {
                    absolute[i] = local[i];
                    resolved[i] = true;
                    remaining--;
                    progressed = true;
                }
                else if (resolved[parent])
                {
                    absolute[i] = local[i] * absolute[parent];
                    resolved[i] = true;
                    remaining--;
                    progressed = true;
                }
            }

            if (!progressed)
            {
                throw new InvalidOperationException("Skeleton hierarchy contains a cycle or dangling parent.");
            }
        }
    }

    /// <summary>
    /// Inverse bind-pose absolute matrices for GPU skinning:
    /// skin[i] = inverseBind[i] * animatedAbsolute[i].
    /// </summary>
    public Matrix4x4[] ComputeInverseBindMatrices()
    {
        var local = ComputeBindLocalMatrices();
        var absolute = new Matrix4x4[BoneCount];
        ResolveAbsolute(local, absolute);

        var inverse = new Matrix4x4[BoneCount];
        for (var i = 0; i < BoneCount; i++)
        {
            if (!Matrix4x4.Invert(absolute[i], out inverse[i]))
            {
                throw new InvalidOperationException($"Bind-pose matrix of bone '{Bones[i].Name}' is not invertible.");
            }
        }

        return inverse;
    }

    internal static Matrix4x4 Compose(Quaternion rotation, Vector3 translation) =>
        Matrix4x4.CreateFromQuaternion(rotation) *
        Matrix4x4.CreateTranslation(translation);
}
