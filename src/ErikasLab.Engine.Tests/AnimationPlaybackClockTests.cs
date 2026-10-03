using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2K portable visual-pose clock and centralized locomotion playback-rate
/// math, tested in isolation from any gameplay state.
/// </summary>
public sealed class AnimationPlaybackClockTests
{
    // --- playback-rate derivation ---------------------------------------

    [Fact]
    public void SteadyWalkAndRunDeriveOne()
    {
        Assert.Equal(1f, LocomotionPlaybackRates.RawRate(1.6853f, 1.6853f), precision: 5);
        Assert.Equal(1f, LocomotionPlaybackRates.AppliedRate(5.5859f, 5.5859f), precision: 5);
    }

    [Fact]
    public void HalfSpeedDerivesHalfRate()
    {
        Assert.Equal(0.5f, LocomotionPlaybackRates.RawRate(0.5f, 1f), precision: 5);
        Assert.Equal(0.5f, LocomotionPlaybackRates.RawRate(2.79295f, 5.5859f), precision: 4);
    }

    [Fact]
    public void ZeroSpeedDerivesZeroRate()
    {
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(0f, 1.6853f));
        Assert.Equal(0f, LocomotionPlaybackRates.AppliedRate(0f, 1.6853f));
    }

    [Fact]
    public void RunToWalkRawRateExceedsOne()
    {
        // Run speed expressed against the walk clip's authored speed.
        var raw = LocomotionPlaybackRates.RawRate(5.5859f, 1.6853f);
        Assert.True(raw > 3f);
    }

    [Fact]
    public void AppliedRateRespectsMaximum()
    {
        var raw = LocomotionPlaybackRates.RawRate(10f, 1f);
        Assert.True(raw > LocomotionPlaybackRates.MaximumRate);
        Assert.Equal(LocomotionPlaybackRates.MaximumRate, LocomotionPlaybackRates.AppliedRate(10f, 1f));
    }

    [Fact]
    public void BoundsAreFiniteAndSane()
    {
        Assert.Equal(0f, LocomotionPlaybackRates.MinimumRate);
        Assert.True(LocomotionPlaybackRates.MaximumRate > 1f);
        Assert.True(LocomotionPlaybackRates.MaximumRate <= 2f);
    }

    [Fact]
    public void NonFiniteAndZeroAuthoredInputsAreContained()
    {
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(float.NaN, 1f));
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(float.PositiveInfinity, 1f));
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(1f, float.NaN));
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(1f, 0f));
        Assert.Equal(0f, LocomotionPlaybackRates.RawRate(1f, -1f));
        Assert.True(float.IsFinite(LocomotionPlaybackRates.AppliedRate(float.NaN, float.NaN)));
    }

    // --- clock integration ----------------------------------------------

    [Fact]
    public void StartsAtZeroAtOneX()
    {
        var clock = new AnimationPlaybackClock();
        Assert.Equal(0.0, clock.ElapsedSeconds);
        Assert.Equal(1f, clock.PlaybackRate);
    }

    [Fact]
    public void ZeroRateDoesNotAdvance()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 0f);
        clock.Advance(1.0, 0f, 10.0);
        Assert.Equal(0.0, clock.ElapsedSeconds);
    }

    [Fact]
    public void OneXAdvancesNormally()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(0.5, 1f, 10.0);
        Assert.Equal(0.5, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void HalfRateAdvancesHalfTime()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 0.5f);
        clock.Advance(1.0, 0.5f, 10.0);
        Assert.Equal(0.5, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void AboveOneAdvancesProportionally()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 2f);
        clock.Advance(1.0, 2f, 100.0);
        Assert.Equal(2.0, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void TrapezoidalStepRampsFromZeroRate()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 0f);
        // Average of 0 and 1 over 1 s is 0.5 s of phase, not 1 s.
        clock.Advance(1.0, 1f, 100.0);
        Assert.Equal(0.5, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void ExactDurationWrapsToZero()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(1.0, 1f, 1.0);
        Assert.Equal(0.0, clock.ElapsedSeconds);
    }

    [Fact]
    public void JustBelowDurationIsNotFoldedToZero()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(1.0 - 1e-6, 1f, 1.0);
        Assert.True(clock.ElapsedSeconds > 0.999);
        Assert.True(clock.ElapsedSeconds < 1.0);
    }

    [Fact]
    public void JustAboveDurationWrapsToRemainder()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(1.0 + 1e-6, 1f, 1.0);
        Assert.Equal(1e-6, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void MultipleLoopsAreDeterministic()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(250.0, 1f, 100.0);
        Assert.Equal(50.0, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void HighRateStaysFiniteAndWrapped()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, LocomotionPlaybackRates.MaximumRate);
        for (var i = 0; i < 100; i++)
        {
            clock.Advance(1.0, LocomotionPlaybackRates.MaximumRate, 0.633);
            Assert.True(double.IsFinite(clock.ElapsedSeconds));
            Assert.True(clock.ElapsedSeconds >= 0.0 && clock.ElapsedSeconds < 0.633);
        }
    }

    [Fact]
    public void ZeroOrInvalidDurationStaysAtZero()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(1.0, 1f, 0.0);
        Assert.Equal(0.0, clock.ElapsedSeconds);

        clock.Advance(1.0, 1f, double.NaN);
        Assert.Equal(0.0, clock.ElapsedSeconds);
    }

    [Fact]
    public void NonFiniteTimeAndRateAreContained()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(0.0, 1f);
        clock.Advance(double.NaN, 1f, 10.0);
        Assert.Equal(0.0, clock.ElapsedSeconds);

        clock.Advance(-1.0, 1f, 10.0);
        Assert.Equal(0.0, clock.ElapsedSeconds);

        // A non-finite/negative rate is ignored; the previous rate is reused.
        clock.Reset(0.5, 1f);
        clock.Advance(0.25, float.NaN, 10.0);
        Assert.Equal(0.75, clock.ElapsedSeconds, precision: 9);
        clock.Advance(0.25, -3f, 10.0);
        Assert.Equal(1.0, clock.ElapsedSeconds, precision: 9);
    }

    [Fact]
    public void ResetAndSetPhaseHandleInvalidInput()
    {
        var clock = new AnimationPlaybackClock();
        clock.Reset(double.NaN, float.NaN);
        Assert.Equal(0.0, clock.ElapsedSeconds);
        Assert.Equal(0f, clock.PlaybackRate);

        clock.SetPhase(3.5, 2.0, 1.25f);
        Assert.Equal(1.5, clock.ElapsedSeconds, precision: 9);
        Assert.Equal(1.25f, clock.PlaybackRate);

        clock.SetPhase(double.NaN, 2.0, 0f);
        Assert.Equal(0.0, clock.ElapsedSeconds);
    }

    [Fact]
    public void WrapHandlesNegativesAndExactMultiples()
    {
        Assert.Equal(0.0, AnimationPlaybackClock.Wrap(4.0, 2.0));
        Assert.Equal(0.0, AnimationPlaybackClock.Wrap(-2.0, 2.0));
        Assert.Equal(0.5, AnimationPlaybackClock.Wrap(2.5, 1.0), precision: 9);
        Assert.Equal(0.0, AnimationPlaybackClock.Wrap(0.0, 1.0));
        Assert.Equal(0.0, AnimationPlaybackClock.Wrap(1.0, 0.0));
    }
}
