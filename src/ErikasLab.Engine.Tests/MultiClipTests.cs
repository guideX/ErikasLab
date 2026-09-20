using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2D multi-clip coverage: three stable clips (idle/walk/run) share one
/// canonical skeleton and evaluate through the same <see cref="AnimationEvaluator"/>.
/// </summary>
public sealed class MultiClipTests
{
    private static Skeleton CanonicalSkeleton() => new([
        new SkeletonBone("mixamorig:Hips", Skeleton.NoParent, new Vector3(0, 100, 0), Quaternion.Identity),
        new SkeletonBone("mixamorig:Spine", 0, new Vector3(0, 10, 0), Quaternion.Identity),
        new SkeletonBone("mixamorig:Head", 1, new Vector3(0, 10, 0), Quaternion.Identity),
    ]);

    private static AnimationClip IdleClip() => new(
        "idle_looking_around", 4.0f, 30f, [
            new AnimationChannel(0,
                [0f, 2f, 4f],
                [Vector3.Zero, new Vector3(0, 1, 0), Vector3.Zero],
                [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity]),
            new AnimationChannel(1,
                [0f, 4f],
                null,
                [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f)]),
        ]);

    private static AnimationClip WalkClip() => new(
        "walk", 1.0333333f, 30f, [
            new AnimationChannel(0,
                [0f, 1.0333333f],
                [Vector3.Zero, new Vector3(0, 0, 5)],
                [Quaternion.Identity, Quaternion.Identity]),
            new AnimationChannel(2,
                [0f, 1.0333333f],
                null,
                [Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f)]),
        ]);

    private static AnimationClip RunClip() => new(
        "run", 0.6333333f, 30f, [
            new AnimationChannel(0,
                [0f, 0.6333333f],
                [Vector3.Zero, new Vector3(0, 0, 10)],
                [Quaternion.Identity, Quaternion.Identity]),
            new AnimationChannel(1,
                [0f, 0.6333333f],
                null,
                [Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.7f)]),
            new AnimationChannel(2,
                [0f, 0.6333333f],
                null,
                [Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.9f)]),
        ]);

    [Fact]
    public void MultipleClipsRoundTripIndependently()
    {
        var skeleton = CanonicalSkeleton();
        foreach (var clip in new[] { IdleClip(), WalkClip(), RunClip() })
        {
            using var stream = new MemoryStream();
            ErikaClipCodec.Write(stream, skeleton, clip);
            stream.Position = 0;
            var (skeleton2, clip2) = ErikaClipCodec.Read(stream);
            Assert.Equal(3, skeleton2.BoneCount);
            Assert.Equal(clip.Name, clip2.Name);
            Assert.Equal(clip.DurationSeconds, clip2.DurationSeconds, precision: 5);
            Assert.Equal(clip.Channels.Count, clip2.Channels.Count);
        }
    }

    [Fact]
    public void ClipIdentitySurvivesRoundTrip()
    {
        var skeleton = CanonicalSkeleton();
        using var stream = new MemoryStream();
        ErikaClipCodec.Write(stream, skeleton, WalkClip());
        stream.Position = 0;
        var (_, clip) = ErikaClipCodec.Read(stream);
        Assert.Equal("walk", clip.Name);
        Assert.Equal(1.0333333f, clip.DurationSeconds, precision: 5);
        Assert.Equal(30f, clip.FramesPerSecond);
    }

    [Fact]
    public void WalkAndRunMapToValidCanonicalBones()
    {
        var skeleton = CanonicalSkeleton();
        foreach (var clip in new[] { WalkClip(), RunClip() })
        {
            foreach (var channel in clip.Channels)
            {
                Assert.InRange(channel.BoneIndex, 0, skeleton.BoneCount - 1);
            }

            var local = new Matrix4x4[skeleton.BoneCount];
            AnimationEvaluator.EvaluateLocal(skeleton, clip, 0.1f, local);
        }
    }

    [Fact]
    public void DuplicateChannelsRemainRejected()
    {
        Assert.Throws<ArgumentException>(() => new AnimationClip("walk", 1f, 30f, [
            new AnimationChannel(0, [0f], [Vector3.Zero], null),
            new AnimationChannel(0, [0f], [Vector3.Zero], null),
        ]));
    }

    [Fact]
    public void InvalidBoneIndicesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnimationChannel(-1, [0f], [Vector3.Zero], null));

        var skeleton = CanonicalSkeleton();
        var bad = new AnimationClip("bad", 1f, 30f, [
            new AnimationChannel(99, [0f], [Vector3.Zero], null),
        ]);
        Assert.Throws<InvalidOperationException>(
            () => AnimationEvaluator.EvaluateLocal(skeleton, bad, 0f, new Matrix4x4[3]));

        using var stream = new MemoryStream();
        ErikaClipCodec.Write(stream, skeleton, bad);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => ErikaClipCodec.Read(stream));
    }

    [Fact]
    public void IncompatibleSkeletonMappingsFailClearly()
    {
        var skeleton = CanonicalSkeleton();
        Assert.False(skeleton.TryGetBoneIndex("mixamorig:Bow", out _));
        Assert.Throws<KeyNotFoundException>(() => skeleton.GetBoneIndex("mixamorig:Bow"));

        var other = new Skeleton([
            new SkeletonBone("other:Hips", Skeleton.NoParent, Vector3.Zero, Quaternion.Identity),
        ]);
        Assert.False(other.TryGetBoneIndex("mixamorig:Hips", out _));
    }

    [Theory]
    [InlineData(4.0f)]
    [InlineData(1.0333333f)]
    [InlineData(0.6333333f)]
    public void ExactLoopBoundariesEvaluateCorrectly(float duration)
    {
        var clip = new AnimationClip("loop", duration, 30f, [
            new AnimationChannel(0, [0f, duration], [Vector3.Zero, Vector3.Zero], null),
        ]);
        Assert.Equal(0f, clip.NormalizeTime(clip.DurationSeconds), precision: 4);
        Assert.Equal(0f, clip.NormalizeTime(clip.DurationSeconds * 3), precision: 3);
        Assert.True(clip.NormalizeTime(clip.DurationSeconds * 2 + clip.DurationSeconds / 2) > 0f);
        Assert.Equal(clip.NormalizeTime(-1.0), clip.NormalizeTime(clip.DurationSeconds * 10 - 1.0), precision: 4);
    }

    [Fact]
    public void SelectingOneClipDoesNotMutateAnother()
    {
        var skeleton = CanonicalSkeleton();
        var idle = IdleClip();
        var walk = WalkClip();
        var idleBefore = idle.Channels[0].Translations![1];
        var walkBefore = walk.Channels[0].Translations![1];

        var local = new Matrix4x4[skeleton.BoneCount];
        AnimationEvaluator.EvaluateLocal(skeleton, idle, 1f, local);
        var idleTranslation = local[0].Translation;
        AnimationEvaluator.EvaluateLocal(skeleton, walk, 0.5f, local);
        Assert.NotEqual(idleTranslation, local[0].Translation);
        AnimationEvaluator.EvaluateLocal(skeleton, idle, 1f, local);
        Assert.Equal(idleTranslation, local[0].Translation);

        Assert.Equal(idleBefore, idle.Channels[0].Translations![1]);
        Assert.Equal(walkBefore, walk.Channels[0].Translations![1]);
    }

    [Fact]
    public void EvaluatorOutputRemainsDeterministic()
    {
        var skeleton = CanonicalSkeleton();
        var run = RunClip();
        var first = new Matrix4x4[skeleton.BoneCount];
        var second = new Matrix4x4[skeleton.BoneCount];
        AnimationEvaluator.EvaluateLocal(skeleton, run, 0.25f, first);
        AnimationEvaluator.EvaluateLocal(skeleton, run, 0.25f, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void StaticBonesRemainAtBindPose()
    {
        var skeleton = CanonicalSkeleton();
        var clip = new AnimationClip("partial", 1f, 30f, [
            new AnimationChannel(0, [0f], [new Vector3(5, 0, 0)], null),
        ]);
        var local = new Matrix4x4[3];
        AnimationEvaluator.EvaluateLocal(skeleton, clip, 0f, local);
        Assert.Equal(new Vector3(5, 0, 0), local[0].Translation);
        Assert.Equal(skeleton.Bones[1].BindTranslation, local[1].Translation);
        Assert.Equal(skeleton.Bones[2].BindTranslation, local[2].Translation);
    }

    [Fact]
    public void MalformedArtifactsAreRejectedCleanly()
    {
        using var badMagic = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.Throws<InvalidDataException>(() => ErikaClipCodec.Read(badMagic));

        var (skeleton, clip) = (CanonicalSkeleton(), IdleClip());
        using var stream = new MemoryStream();
        ErikaClipCodec.Write(stream, skeleton, clip);
        using var cut = new MemoryStream(stream.ToArray(), 0, 40);
        Assert.Throws<EndOfStreamException>(() => ErikaClipCodec.Read(cut));

        using var empty = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => ErikaClipCodec.Read(empty));
    }

    [Fact]
    public void OldIdleBehaviorRemainsValid()
    {
        var skeleton = CanonicalSkeleton();
        var idle = IdleClip();
        Assert.Equal("idle_looking_around", idle.Name);
        Assert.Equal(4.0f, idle.DurationSeconds);
        Assert.Equal(0f, idle.NormalizeTime(4.0), precision: 5);
        var local = new Matrix4x4[skeleton.BoneCount];
        AnimationEvaluator.EvaluateLocal(skeleton, idle, 0f, local);
        Assert.Equal(Vector3.Zero, local[0].Translation);
    }
}
