using System.Numerics;

namespace ErikasLab.Engine;

public readonly record struct Transform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static Transform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public Matrix4x4 WorldMatrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);
}
