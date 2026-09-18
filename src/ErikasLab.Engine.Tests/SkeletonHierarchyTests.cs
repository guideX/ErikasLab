using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

public sealed class SkeletonHierarchyTests
{
    private static Skeleton Chain() => new([
        new SkeletonBone("root", Skeleton.NoParent, Vector3.Zero, Quaternion.Identity),
        new SkeletonBone("mid", 0, new Vector3(0, 1, 0), Quaternion.Identity),
        new SkeletonBone("tip", 1, new Vector3(0, 1, 0), Quaternion.Identity),
    ]);

    [Fact]
    public void ResolveAbsolutePropagatesParentTranslation()
    {
        var skeleton = Chain();
        var local = skeleton.ComputeBindLocalMatrices();
        var absolute = new Matrix4x4[3];
        skeleton.ResolveAbsolute(local, absolute);

        Assert.Equal(new Vector3(0, 0, 0), absolute[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), absolute[1].Translation);
        Assert.Equal(new Vector3(0, 2, 0), absolute[2].Translation);
    }

    [Fact]
    public void ResolveAbsolutePropagatesParentRotationToChildOffset()
    {
        var skeleton = new Skeleton([
            new SkeletonBone("root", Skeleton.NoParent, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)),
            new SkeletonBone("tip", 0, new Vector3(1, 0, 0), Quaternion.Identity),
        ]);
        var local = skeleton.ComputeBindLocalMatrices();
        var absolute = new Matrix4x4[2];
        skeleton.ResolveAbsolute(local, absolute);

        var tip = absolute[1].Translation;
        Assert.Equal(0f, tip.X, precision: 5);
        Assert.Equal(0f, tip.Y, precision: 5);
        Assert.Equal(-1f, tip.Z, precision: 5);
    }

    [Fact]
    public void ResolveAbsoluteHandlesShuffledBoneOrder()
    {
        var skeleton = new Skeleton([
            new SkeletonBone("tip", 2, new Vector3(0, 1, 0), Quaternion.Identity),
            new SkeletonBone("root", Skeleton.NoParent, Vector3.Zero, Quaternion.Identity),
            new SkeletonBone("mid", 1, new Vector3(0, 1, 0), Quaternion.Identity),
        ]);
        var local = skeleton.ComputeBindLocalMatrices();
        var absolute = new Matrix4x4[3];
        skeleton.ResolveAbsolute(local, absolute);

        Assert.Equal(new Vector3(0, 2, 0), absolute[0].Translation);
        Assert.Equal(new Vector3(0, 0, 0), absolute[1].Translation);
        Assert.Equal(new Vector3(0, 1, 0), absolute[2].Translation);
    }

    [Fact]
    public void SkeletonRejectsCycles()
    {
        var skeleton = new Skeleton([
            new SkeletonBone("a", 1, Vector3.Zero, Quaternion.Identity),
            new SkeletonBone("b", 0, Vector3.Zero, Quaternion.Identity),
        ]);
        var local = skeleton.ComputeBindLocalMatrices();
        Assert.Throws<InvalidOperationException>(
            () => skeleton.ResolveAbsolute(local, new Matrix4x4[2]));
    }

    [Fact]
    public void SkeletonRejectsDuplicateNames()
    {
        Assert.Throws<ArgumentException>(() => new Skeleton([
            new SkeletonBone("same", Skeleton.NoParent, Vector3.Zero, Quaternion.Identity),
            new SkeletonBone("same", 0, Vector3.Zero, Quaternion.Identity),
        ]));
    }

    [Fact]
    public void SkeletonRejectsBadParent()
    {
        Assert.Throws<ArgumentException>(() => new Skeleton([
            new SkeletonBone("orphan", 7, Vector3.Zero, Quaternion.Identity),
        ]));
    }

    [Fact]
    public void SkeletonRejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Skeleton([]));
    }

    [Fact]
    public void BoneLookupFindsAndMisses()
    {
        var skeleton = Chain();
        Assert.True(skeleton.TryGetBoneIndex("mid", out var index));
        Assert.Equal(1, index);
        Assert.False(skeleton.TryGetBoneIndex("nope", out _));
        Assert.Throws<KeyNotFoundException>(() => skeleton.GetBoneIndex("nope"));
    }

    [Fact]
    public void InverseBindTimesBindIsIdentity()
    {
        var skeleton = new Skeleton([
            new SkeletonBone("root", Skeleton.NoParent, new Vector3(1, 2, 3),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f)),
            new SkeletonBone("tip", 0, new Vector3(0, 1, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f)),
        ]);
        var local = skeleton.ComputeBindLocalMatrices();
        var absolute = new Matrix4x4[2];
        skeleton.ResolveAbsolute(local, absolute);
        var inverse = skeleton.ComputeInverseBindMatrices();

        for (var i = 0; i < 2; i++)
        {
            var product = inverse[i] * absolute[i];
            Assert.Equal(Matrix4x4.Identity.M11, product.M11, precision: 5);
            Assert.Equal(Matrix4x4.Identity.M22, product.M22, precision: 5);
            Assert.Equal(Matrix4x4.Identity.M33, product.M33, precision: 5);
            Assert.Equal(Matrix4x4.Identity.M44, product.M44, precision: 5);
            Assert.Equal(0f, product.M14, precision: 5);
            Assert.Equal(0f, product.M41, precision: 5);
            Assert.Equal(0f, product.M42, precision: 5);
            Assert.Equal(0f, product.M43, precision: 5);
        }
    }

    [Fact]
    public void EvaluatorUsesBindForUnanimatedBones()
    {
        var skeleton = Chain();
        var clip = new AnimationClip("one", 1f, 30f, [
            new AnimationChannel(0, [0f], [new Vector3(5, 0, 0)], null),
        ]);
        var local = new Matrix4x4[3];
        AnimationEvaluator.EvaluateLocal(skeleton, clip, 0f, local);

        Assert.Equal(new Vector3(5, 0, 0), local[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), local[1].Translation);
    }

    [Fact]
    public void EvaluatorRejectsChannelOutsideSkeleton()
    {
        var skeleton = Chain();
        var clip = new AnimationClip("bad", 1f, 30f, [
            new AnimationChannel(9, [0f], [Vector3.Zero], null),
        ]);
        Assert.Throws<InvalidOperationException>(
            () => AnimationEvaluator.EvaluateLocal(skeleton, clip, 0f, new Matrix4x4[3]));
    }

    [Fact]
    public void ClipRejectsDuplicateBoneIndex()
    {
        Assert.Throws<ArgumentException>(() => new AnimationClip("dup", 1f, 30f, [
            new AnimationChannel(0, [0f], [Vector3.Zero], null),
            new AnimationChannel(0, [0f], [Vector3.Zero], null),
        ]));
    }

    [Fact]
    public void SkinningMatricesCombineInverseBindWithPose()
    {
        var inverse = new[] { Matrix4x4.CreateTranslation(-1, 0, 0), Matrix4x4.Identity };
        var absolute = new[] { Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateScale(2) };
        var skinning = new Matrix4x4[2];
        AnimationEvaluator.ComputeSkinningMatrices(inverse, absolute, skinning);

        Assert.Equal(Matrix4x4.Identity, skinning[0]);
        Assert.Equal(Matrix4x4.CreateScale(2), skinning[1]);
    }
}
