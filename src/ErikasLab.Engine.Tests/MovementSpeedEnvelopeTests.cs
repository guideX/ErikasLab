using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2I movement-speed envelope and authored-speed derivation. Covers the
/// scalar accel/decel filter in isolation plus the root-motion speed math, so
/// the gameplay response is verifiable without a running app.
/// </summary>
public sealed class MovementSpeedEnvelopeTests
{
    // --- envelope integration -------------------------------------------

    [Fact]
    public void StartsAtZero()
    {
        var envelope = new MovementSpeedEnvelope();
        Assert.Equal(0f, envelope.CurrentMetersPerSecond);
    }

    [Fact]
    public void AcceleratesMonotonicallyTowardTargetWithoutOvershoot()
    {
        var envelope = new MovementSpeedEnvelope();
        var target = 2f;
        var previous = envelope.CurrentMetersPerSecond;
        for (var i = 0; i < 200; i++)
        {
            var current = envelope.Advance(target, 1.0 / 60.0, acceleration: 16f, deceleration: 24f);
            Assert.True(current >= previous);
            Assert.True(current <= target);
            previous = current;
        }

        Assert.Equal(target, envelope.CurrentMetersPerSecond, precision: 4);
    }

    [Fact]
    public void LargeDtClampsExactlyToTarget()
    {
        var envelope = new MovementSpeedEnvelope();
        envelope.Advance(5f, 1000.0, acceleration: 16f, deceleration: 24f);
        Assert.Equal(5f, envelope.CurrentMetersPerSecond, precision: 5);
    }

    [Fact]
    public void EquivalentElapsedTimeSplitAcrossFramesIsEquivalent()
    {
        var single = new MovementSpeedEnvelope();
        single.Advance(10f, 0.1, acceleration: 16f, deceleration: 24f);

        var split = new MovementSpeedEnvelope();
        for (var i = 0; i < 10; i++)
        {
            split.Advance(10f, 0.01, acceleration: 16f, deceleration: 24f);
        }

        Assert.Equal(single.CurrentMetersPerSecond, split.CurrentMetersPerSecond, precision: 4);
    }

    [Fact]
    public void DeceleratesMonotonicallyToExactZero()
    {
        var envelope = new MovementSpeedEnvelope();
        envelope.Reset(3f);
        var previous = envelope.CurrentMetersPerSecond;
        for (var i = 0; i < 200; i++)
        {
            var current = envelope.Advance(0f, 1.0 / 60.0, acceleration: 16f, deceleration: 24f);
            Assert.True(current <= previous);
            Assert.True(current >= 0f);
            previous = current;
        }

        Assert.Equal(0f, envelope.CurrentMetersPerSecond);
    }

    [Fact]
    public void ZeroElapsedTimeChangesNothing()
    {
        var envelope = new MovementSpeedEnvelope();
        envelope.Reset(1.5f);
        envelope.Advance(0f, 0.0, acceleration: 16f, deceleration: 24f);
        Assert.Equal(1.5f, envelope.CurrentMetersPerSecond, precision: 5);
    }

    [Fact]
    public void NonFiniteInputsAreContained()
    {
        var envelope = new MovementSpeedEnvelope();
        envelope.Advance(float.NaN, 0.1, acceleration: 16f, deceleration: 24f);
        Assert.Equal(0f, envelope.CurrentMetersPerSecond);

        envelope.Reset(float.PositiveInfinity);
        Assert.Equal(0f, envelope.CurrentMetersPerSecond);

        envelope.Advance(-5f, 0.1, acceleration: 16f, deceleration: 24f);
        Assert.Equal(0f, envelope.CurrentMetersPerSecond);
    }

    [Fact]
    public void MoveTowardsClampsOntoTargetAndHandlesNonFinite()
    {
        Assert.Equal(1f, MovementSpeedEnvelope.MoveTowards(0f, 1f, 5f), precision: 5);
        Assert.Equal(0f, MovementSpeedEnvelope.MoveTowards(1f, 0f, 5f), precision: 5);
        Assert.Equal(0.5f, MovementSpeedEnvelope.MoveTowards(0f, 1f, 0.5f), precision: 5);
        Assert.Equal(0.5f, MovementSpeedEnvelope.MoveTowards(float.NaN, 1f, 0.5f), precision: 5);
        Assert.Equal(0.5f, MovementSpeedEnvelope.MoveTowards(0.5f, 1f, float.NaN), precision: 5);
    }

    // --- authored speed derivation --------------------------------------

    private static AnimationClip TravelClip(string name, float duration, float endZ) => new(
        name, duration, 30f, [
            new AnimationChannel(0, [0f, duration],
                [Vector3.Zero, new Vector3(0, 0, endZ)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    [Fact]
    public void WalkSpeedDerivesFromSyntheticClip()
    {
        var clip = TravelClip("walk", 1.0f, 100f);
        var speed = RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(clip, 0, worldScale: 1f);
        Assert.Equal(100f, speed, precision: 4);
    }

    [Fact]
    public void RunSpeedDerivesFromSyntheticClip()
    {
        var clip = TravelClip("run", 0.5f, 100f);
        var speed = RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(clip, 0, worldScale: 1f);
        Assert.Equal(200f, speed, precision: 4);
    }

    [Fact]
    public void AuthoredSpeedScalesWithWorldScale()
    {
        var clip = TravelClip("walk", 1.0f, 100f);
        var speed = RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(clip, 0, ErikaFigure.Scale);
        Assert.Equal(100f * ErikaFigure.Scale, speed, precision: 4);
    }

    [Fact]
    public void ZeroDisplacementReturnsZero()
    {
        var clip = TravelClip("stationary", 1.0f, 0f);
        Assert.Equal(0f, RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(clip, 0, 1f));
    }

    [Fact]
    public void MissingRootTrackReturnsZero()
    {
        var clip = new AnimationClip("noroot", 1f, 30f, [
            new AnimationChannel(1, [0f, 1f], null,
                [Quaternion.Identity, Quaternion.Identity]),
        ]);
        Assert.Equal(0f, RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(clip, 0, 1f));
    }

    [Fact]
    public void NonPositiveOrNonFiniteDurationReturnsZero()
    {
        var start = Vector3.Zero;
        var end = new Vector3(0, 0, 5);
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(start, end, 0.0, 1f));
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(start, end, -1.0, 1f));
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(start, end, double.NaN, 1f));
    }

    [Fact]
    public void NonFiniteScaleOrSamplesReturnZero()
    {
        var start = Vector3.Zero;
        var end = new Vector3(0, 0, 5);
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(start, end, 1.0, float.NaN));
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(start, end, 1.0, 0f));
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(
            new Vector3(float.NaN, 0, 0), end, 1.0, 1f));
        Assert.Equal(0f, RootMotionEvaluator.HorizontalSpeedMetersPerSecond(
            start, new Vector3(float.PositiveInfinity, 0, 0), 1.0, 1f));
    }
}
