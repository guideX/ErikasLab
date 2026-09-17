using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

public sealed class AnimationClipTests
{
    private static AnimationClip LoopClip() =>
        new("idle", 4.0f, 30f, [new AnimationChannel(0, [0f, 4.0f], [Vector3.Zero, Vector3.Zero], null)]);

    [Fact]
    public void NormalizeTimeKeepsInteriorTime()
    {
        Assert.Equal(1.5f, LoopClip().NormalizeTime(1.5), precision: 5);
    }

    [Fact]
    public void NormalizeTimeMapsExactEndToLoopStart()
    {
        Assert.Equal(0f, LoopClip().NormalizeTime(4.0), precision: 5);
    }

    [Fact]
    public void NormalizeTimeWrapsBeyondDuration()
    {
        Assert.Equal(0.5f, LoopClip().NormalizeTime(8.5), precision: 5);
    }

    [Fact]
    public void NormalizeTimeWrapsNegativeElapsed()
    {
        Assert.Equal(3.0f, LoopClip().NormalizeTime(-1.0), precision: 5);
    }

    [Fact]
    public void ChannelSamplesSingleKeyConstantly()
    {
        var channel = new AnimationChannel(0, [2f], [new Vector3(1, 2, 3)], null);
        Assert.Equal(new Vector3(1, 2, 3), channel.SampleTranslation(-99f));
        Assert.Equal(new Vector3(1, 2, 3), channel.SampleTranslation(99f));
    }

    [Fact]
    public void ChannelInterpolatesTranslationLinearly()
    {
        var channel = new AnimationChannel(0, [0f, 2f], [Vector3.Zero, new Vector3(0, 10, 0)], null);
        Assert.Equal(new Vector3(0, 5, 0), channel.SampleTranslation(1f));
    }

    [Fact]
    public void ChannelClampsOutsideKeyRange()
    {
        var channel = new AnimationChannel(0, [1f, 3f], [new Vector3(1, 0, 0), new Vector3(3, 0, 0)], null);
        Assert.Equal(new Vector3(1, 0, 0), channel.SampleTranslation(0f));
        Assert.Equal(new Vector3(3, 0, 0), channel.SampleTranslation(9f));
    }

    [Fact]
    public void ChannelInterpolatesRotationAlongShortestArc()
    {
        var from = Quaternion.Identity;
        var to = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var channel = new AnimationChannel(0, [0f, 1f], null, [from, to]);
        var mid = channel.SampleRotation(0.5f);
        var rotated = Vector3.Transform(Vector3.UnitX, mid);
        var expected = new Vector3(MathF.Cos(MathF.PI / 4), 0, -MathF.Sin(MathF.PI / 4));
        Assert.Equal(expected.X, rotated.X, precision: 4);
        Assert.Equal(expected.Z, rotated.Z, precision: 4);
        Assert.Equal(1f, mid.Length(), precision: 5);
    }

    [Fact]
    public void ChannelSurvivesAntipodalKeys()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        var channel = new AnimationChannel(0, [0f, 1f], null, [q, Quaternion.Negate(q)]);
        var mid = channel.SampleRotation(0.5f);
        Assert.False(float.IsNaN(mid.X + mid.Y + mid.Z + mid.W));
        Assert.Equal(1f, mid.Length(), precision: 4);
    }

    [Fact]
    public void ChannelRejectsUnsortedTimes()
    {
        Assert.Throws<ArgumentException>(
            () => new AnimationChannel(0, [0f, 2f, 1f], [Vector3.Zero, Vector3.Zero, Vector3.Zero], null));
    }

    [Fact]
    public void ChannelRejectsMismatchedTracks()
    {
        Assert.Throws<ArgumentException>(
            () => new AnimationChannel(0, [0f, 1f], [Vector3.Zero], null));
    }

    [Fact]
    public void ChannelRejectsMissingTracks()
    {
        Assert.Throws<ArgumentException>(() => new AnimationChannel(0, [0f], null, null));
    }

    [Fact]
    public void ChannelRejectsSamplingAbsentTrack()
    {
        var channel = new AnimationChannel(0, [0f], [Vector3.Zero], null);
        Assert.Throws<InvalidOperationException>(() => channel.SampleRotation(0f));
    }

    [Fact]
    public void ClipRejectsEmptyChannels()
    {
        Assert.Throws<ArgumentException>(
            () => new AnimationClip("empty", 1f, 30f, []));
    }
}
