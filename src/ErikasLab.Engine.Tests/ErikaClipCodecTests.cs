using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

public sealed class ErikaClipCodecTests
{
    private static (Skeleton Skeleton, AnimationClip Clip) Sample()
    {
        var skeleton = new Skeleton([
            new SkeletonBone("mixamorig:HipsModel", Skeleton.NoParent,
                new Vector3(0, 100, 0), Quaternion.Identity),
            new SkeletonBone("mixamorig:SpineModel", 0,
                new Vector3(0, 10, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f)),
        ]);
        var clip = new AnimationClip("Take 001", 4.0f, 30f, [
            new AnimationChannel(0,
                [0f, 2f, 4f],
                [Vector3.Zero, new Vector3(0, 1, 0), Vector3.Zero],
                [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity]),
            new AnimationChannel(1,
                [0f, 4f],
                null,
                [Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)]),
        ]);
        return (skeleton, clip);
    }

    [Fact]
    public void CodecRoundTripPreservesSkeletonAndClip()
    {
        var (skeleton, clip) = Sample();
        using var stream = new MemoryStream();
        ErikaClipCodec.Write(stream, skeleton, clip);
        Assert.True(stream.Length > 64);
        stream.Position = 0;

        var (skeleton2, clip2) = ErikaClipCodec.Read(stream);
        Assert.Equal(2, skeleton2.BoneCount);
        Assert.Equal("mixamorig:SpineModel", skeleton2.Bones[1].Name);
        Assert.Equal(0, skeleton2.Bones[1].ParentIndex);
        Assert.Equal(skeleton.Bones[1].BindTranslation, skeleton2.Bones[1].BindTranslation);
        Assert.Equal("Take 001", clip2.Name);
        Assert.Equal(4.0f, clip2.DurationSeconds);
        Assert.Equal(30f, clip2.FramesPerSecond);
        Assert.Equal(2, clip2.Channels.Count);

        var first = clip2.Channels[0];
        Assert.Equal(0, first.BoneIndex);
        Assert.Equal(3, first.Times.Length);
        Assert.Equal(0f, first.Times[0]);
        Assert.Equal(2f, first.Times[1]);
        Assert.Equal(4f, first.Times[2]);
        Assert.NotNull(first.Translations);
        Assert.Equal(new Vector3(0, 1, 0), first.Translations![1]);

        var second = clip2.Channels[1];
        Assert.False(second.HasTranslation);
        Assert.True(second.HasRotation);
        Assert.NotNull(second.Rotations);
        Assert.Equal(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            second.Rotations![1]);
    }

    [Fact]
    public void CodecRejectsBadMagic()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.Throws<InvalidDataException>(() => ErikaClipCodec.Read(stream));
    }

    [Fact]
    public void CodecRejectsTruncatedStream()
    {
        var (skeleton, clip) = Sample();
        using var stream = new MemoryStream();
        ErikaClipCodec.Write(stream, skeleton, clip);
        using var cut = new MemoryStream(stream.ToArray(), 0, 40);
        Assert.Throws<EndOfStreamException>(() => ErikaClipCodec.Read(cut));
    }
}
