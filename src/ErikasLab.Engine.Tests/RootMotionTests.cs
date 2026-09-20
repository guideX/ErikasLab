using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>Phase 2E root-motion coverage: wrap-aware deltas, net displacement, pose neutralization.</summary>
public sealed class RootMotionTests
{
    private static Skeleton HipsSkeleton() => new([
        new SkeletonBone("mixamorig:Hips", Skeleton.NoParent, new Vector3(0, 100, 0), Quaternion.Identity),
        new SkeletonBone("mixamorig:Spine", 0, new Vector3(0, 10, 0), Quaternion.Identity),
    ]);

    private static AnimationClip TravelClip(string name, float duration, float startZ, float endZ) => new(
        name, duration, 30f, [
            new AnimationChannel(0,
                [0f, duration],
                [new Vector3(1f, 100f, startZ), new Vector3(1.5f, 101f, endZ)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip WalkSynthetic() => TravelClip("walk", 1.0f, 0f, 5f);

    private static AnimationClip RunSynthetic() => TravelClip("run", 0.5f, 0f, 10f);

    [Fact]
    public void WithinLoopDeltaMatchesSampledDifference()
    {
        var clip = WalkSynthetic();
        var delta = RootMotionEvaluator.ComputeDelta(clip, 0, 0.2, 0.7);
        // Linear 0->5 over 1s: 0.5s span => 2.5 units forward.
        Assert.Equal(2.5f, delta.Z, precision: 4);
        Assert.Equal(0.5f, delta.Y, precision: 4);
        Assert.Equal(RootMotionEvaluator.HorizontalOnly(delta).Z, delta.Z, precision: 5);
    }

    [Fact]
    public void ExactLoopBoundaryCrossingIsContinuous()
    {
        var clip = WalkSynthetic();
        var before = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 0.9);
        var at = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 1.0);
        var after = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 1.1);
        // At exact duration the loop restarts: absolute = 1 * net.
        Assert.Equal(5f, at.Z, precision: 4);
        Assert.True(after.Z > at.Z);
        Assert.True(at.Z > before.Z);
        var crossing = RootMotionEvaluator.ComputeDelta(clip, 0, 0.9, 1.1);
        Assert.Equal(1.0f, crossing.Z, precision: 4);
    }

    [Fact]
    public void OneWrapHasNoReverseDelta()
    {
        var clip = WalkSynthetic();
        var delta = RootMotionEvaluator.ComputeDelta(clip, 0, 0.8, 1.2);
        // 0.4s of travel at 5u/s = 2.0, never negative.
        Assert.Equal(2.0f, delta.Z, precision: 4);
        Assert.True(delta.Z > 0);
    }

    [Fact]
    public void MultipleWrapsInOneUpdateAccumulateNet()
    {
        var clip = WalkSynthetic();
        var delta = RootMotionEvaluator.ComputeDelta(clip, 0, 0.2, 3.7);
        // 3 full loops (15) + 0.5 loop remainder (1.5) = 16.5? Compute via absolute.
        var expected = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 3.7)
            - RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 0.2);
        Assert.Equal(expected.Z, delta.Z, precision: 5);
        // 3.5s span at 5u/s = 17.5.
        Assert.Equal(17.5f, delta.Z, precision: 3);
    }

    [Fact]
    public void ZeroElapsedYieldsZeroDelta()
    {
        var clip = WalkSynthetic();
        Assert.Equal(Vector3.Zero, RootMotionEvaluator.ComputeDelta(clip, 0, 0.4, 0.4));
        Assert.Equal(Vector3.Zero, RootMotionEvaluator.ComputeDelta(clip, 0, 0.0, 0.0));
    }

    [Fact]
    public void RepeatedEvaluationIsDeterministic()
    {
        var clip = RunSynthetic();
        var first = RootMotionEvaluator.ComputeDelta(clip, 0, 0.33, 1.21);
        var second = RootMotionEvaluator.ComputeDelta(clip, 0, 0.33, 1.21);
        Assert.Equal(first, second);
        var abs1 = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 2.75);
        var abs2 = RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 2.75);
        Assert.Equal(abs1, abs2);
    }

    [Fact]
    public void CompleteLoopsProduceExactMultiplesOfNet()
    {
        var walk = WalkSynthetic();
        var one = RootMotionEvaluator.ComputeDelta(walk, 0, 0.0, 1.0);
        var two = RootMotionEvaluator.ComputeDelta(walk, 0, 0.0, 2.0);
        var three = RootMotionEvaluator.ComputeDelta(walk, 0, 1.0, 4.0);
        Assert.Equal(5f, one.Z, precision: 4);
        Assert.Equal(10f, two.Z, precision: 4);
        Assert.Equal(15f, three.Z, precision: 4);

        var run = RunSynthetic();
        Assert.Equal(10f, RootMotionEvaluator.ComputeDelta(run, 0, 0.0, 0.5).Z, precision: 4);
        Assert.Equal(20f, RootMotionEvaluator.ComputeDelta(run, 0, 0.0, 1.0).Z, precision: 4);
    }

    [Fact]
    public void NetDisplacementMatchesEndMinusStart()
    {
        var walk = WalkSynthetic();
        var net = RootMotionEvaluator.GetNetDisplacement(walk, 0);
        Assert.Equal(5f, net.Z, precision: 5);
        var run = RunSynthetic();
        Assert.Equal(10f, RootMotionEvaluator.GetNetDisplacement(run, 0).Z, precision: 5);
    }

    [Fact]
    public void NoLargeReverseDeltaAtWrap()
    {
        var clip = WalkSynthetic();
        // Straddle the seam tightly: must be small forward, never ~-5.
        var delta = RootMotionEvaluator.ComputeDelta(clip, 0, 0.99, 1.01);
        Assert.Equal(0.1f, delta.Z, precision: 3);
        Assert.True(delta.Z > -0.5f);
    }

    [Fact]
    public void MissingHipsTrackYieldsZeroMotion()
    {
        var skeleton = HipsSkeleton();
        var clip = new AnimationClip("noroot", 1f, 30f, [
            new AnimationChannel(1, [0f, 1f], null,
                [Quaternion.Identity, Quaternion.Identity]),
        ]);
        Assert.Equal(Vector3.Zero, RootMotionEvaluator.ComputeDelta(clip, 0, 0.0, 1.0));
        Assert.Equal(Vector3.Zero, RootMotionEvaluator.ComputeAbsoluteRoot(clip, 0, 0.7));
    }

    [Fact]
    public void NeutralizedPosePinsHorizontalToStart()
    {
        var clip = WalkSynthetic();
        var skeleton = HipsSkeleton();
        var local = new Matrix4x4[skeleton.BoneCount];
        AnimationEvaluator.EvaluateLocal(skeleton, clip, 0.7f, local);
        var beforeY = local[0].Translation.Y;
        RootMotionEvaluator.NeutralizeHipsHorizontal(clip, 0, local);
        // X/Z pinned to start (1, *, 0); Y preserved.
        Assert.Equal(1f, local[0].M41, precision: 5);
        Assert.Equal(0f, local[0].M43, precision: 5);
        Assert.Equal(beforeY, local[0].M42, precision: 5);
        // Helper agrees.
        var neutral = RootMotionEvaluator.NeutralizedHipsTranslation(clip, 0, 0.7f);
        Assert.Equal(1f, neutral.X, precision: 5);
        Assert.Equal(beforeY, neutral.Y, precision: 4);
        Assert.Equal(0f, neutral.Z, precision: 5);
    }

    [Fact]
    public void NeutralizationPreservesRotation()
    {
        var skeleton = HipsSkeleton();
        var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f);
        var clip = new AnimationClip("spinwalk", 1f, 30f, [
            new AnimationChannel(0, [0f, 1f],
                [Vector3.Zero, new Vector3(0, 0, 4)],
                [Quaternion.Identity, spin]),
        ]);
        var local = new Matrix4x4[skeleton.BoneCount];
        AnimationEvaluator.EvaluateLocal(skeleton, clip, 0.5f, local);
        var rotationBefore = local[0];
        RootMotionEvaluator.NeutralizeHipsHorizontal(clip, 0, local);
        // Rotation block (upper 3x3) unchanged; only translation XZ pinned.
        Assert.Equal(rotationBefore.M11, local[0].M11, precision: 5);
        Assert.Equal(rotationBefore.M22, local[0].M22, precision: 5);
        Assert.Equal(0f, local[0].M41, precision: 5);
        Assert.Equal(0f, local[0].M43, precision: 5);
        Assert.True(float.IsFinite(local[0].M42));
    }
}
