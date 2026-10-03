using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2K locomotion playback-rate synchronization through GameSession.
/// Verifies the visual pose clock is driven by the Phase 2I speed envelope,
/// that steady locomotion renders at exactly 1x, that the run-to-walk raw rate
/// is capped, that crossfade pose phases stay finite and bounded, and — most
/// importantly — that enabling visual synchronization never changes world-space
/// gameplay motion.
/// </summary>
public sealed class LocomotionPlaybackSyncTests
{
    // --- fixtures --------------------------------------------------------

    private static Skeleton TestSkeleton() => new([
        new SkeletonBone("mixamorig:Hips", Skeleton.NoParent, new Vector3(0, 100, 0), Quaternion.Identity),
        new SkeletonBone("mixamorig:Spine", 0, new Vector3(0, 10, 0), Quaternion.Identity),
    ]);

    private static AnimationClip Idle() => new(
        "idle_looking_around", 4.0f, 30f, [
            new AnimationChannel(0, [0f, 4f],
                [Vector3.Zero, Vector3.Zero],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    // Walk: 100 native units / 1.0 s loop. Run: 100 units / 0.25 s loop, i.e.
    // 4x the walk authored speed, so run-to-walk raw rate peaks at ~4 and the
    // centralized 2x cap is exercised.
    private static AnimationClip Walk() => new(
        "walk", 1.0f, 30f, [
            new AnimationChannel(0, [0f, 1f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Run() => new(
        "run", 0.25f, 30f, [
            new AnimationChannel(0, [0f, 0.25f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static GameSession SessionWithClips(bool visualSync = true)
    {
        var session = new GameSession { VisualPlaybackSynchronizationEnabled = visualSync };
        session.SetAnimationData(TestSkeleton(), Idle(), Walk(), Run());
        return session;
    }

    private static InputState Move(
        bool forward = false,
        bool back = false,
        bool left = false,
        bool right = false,
        bool sprint = false) => new(
        MoveForward: forward, MoveBackward: back, StrafeLeft: left, StrafeRight: right,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default, Sprint: sprint);

    private static InputState NoInput() => Move();

    private static void Step(GameSession session, ref double time, double dt, InputState input)
    {
        session.Update(new FrameTime(time, dt), input);
        time += dt;
    }

    private static void Settle(GameSession session, ref double time, InputState input, int steps = 80)
    {
        for (var i = 0; i < steps; i++)
        {
            Step(session, ref time, 0.005, input);
        }
    }

    // --- playback-rate derivation through the session --------------------

    [Fact]
    public void IdlePlaysAtExactlyOneX()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, NoInput(), steps: 10);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(1f, session.VisualPlaybackRate);
        Assert.Equal(1f, session.RawVisualPlaybackRate);
    }

    [Fact]
    public void SteadyWalkPlaysAtExactlyOneX()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(1f, session.VisualPlaybackRate, precision: 5);
        Assert.Equal(1f, session.RawVisualPlaybackRate, precision: 5);
    }

    [Fact]
    public void SteadyRunPlaysAtExactlyOneX()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(1f, session.VisualPlaybackRate, precision: 5);
        Assert.Equal(1f, session.RawVisualPlaybackRate, precision: 5);
    }

    [Fact]
    public void IdleToWalkRateRisesFromZeroAndIsUncapped()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.01, Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate > 0f);
        Assert.True(session.VisualPlaybackRate < 1f);
        Assert.Equal(session.RawVisualPlaybackRate, session.VisualPlaybackRate, precision: 5);
    }

    [Fact]
    public void IdleToRunRateRisesFromZero()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.01, Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate > 0f);
        Assert.True(session.VisualPlaybackRate < 1f);
    }

    [Fact]
    public void VisualPoseAdvancesLessThanNominalDuringAcceleration()
    {
        var session = SessionWithClips();
        double time = 0;
        var dt = 0.01;
        Step(session, ref time, dt, Move(forward: true));
        Assert.True(session.VisualPoseElapsedSeconds > 0.0);
        Assert.True(session.VisualPoseElapsedSeconds < dt);
    }

    [Fact]
    public void SteadyWalkPoseAdvancesAtNominalRate()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var before = session.VisualPoseElapsedSeconds;
        var dt = 0.02;
        Step(session, ref time, dt, Move(forward: true));
        var advanced = session.VisualPoseElapsedSeconds - before;
        Assert.Equal(dt, advanced, precision: 4);
    }

    [Fact]
    public void WalkToRunDoesNotJumpRunRateToOne()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var before = session.CurrentMoveSpeedMetersPerSecond;

        Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate < 1f);
        Assert.True(session.VisualPlaybackRate > 0f);
        // World speed stays continuous across the switch.
        Assert.True(session.CurrentMoveSpeedMetersPerSecond >= before);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond - before <= 16f * 0.005f + 1e-3f);

        Settle(session, ref time, Move(forward: true, sprint: true), steps: 120);
        Assert.Equal(1f, session.VisualPlaybackRate, precision: 5);
    }

    [Fact]
    public void RunToWalkRawRateExceedsMaximumAndAppliedRateIsCapped()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        var runSpeed = session.CurrentMoveSpeedMetersPerSecond;

        // Zero-dt switch isolates the rate without any speed change.
        Step(session, ref time, 0.0, Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(runSpeed, session.CurrentMoveSpeedMetersPerSecond, precision: 5);
        Assert.True(session.RawVisualPlaybackRate > LocomotionPlaybackRates.MaximumRate);
        Assert.Equal(LocomotionPlaybackRates.MaximumRate, session.VisualPlaybackRate, precision: 5);

        // Decelerates and the applied rate converges back to exactly 1x.
        Settle(session, ref time, Move(forward: true), steps: 120);
        Assert.Equal(1f, session.VisualPlaybackRate, precision: 5);
        Assert.Equal(1f, session.RawVisualPlaybackRate, precision: 5);
    }

    // --- stopping --------------------------------------------------------

    [Fact]
    public void WalkStopRateDecreasesTowardZero()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var movingRate = session.VisualPlaybackRate;
        Assert.Equal(1f, movingRate, precision: 5);

        Step(session, ref time, 0.01, NoInput());
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate < movingRate);
        Assert.True(session.VisualPlaybackRate >= 0f);

        var guard = 0;
        var previousRate = session.VisualPlaybackRate;
        while (session.ActiveClipName != ErikaFigure.IdleClipName && guard++ < 1000)
        {
            Step(session, ref time, 0.005, NoInput());
            if (session.ActiveClipName == ErikaFigure.IdleClipName)
            {
                // Stop hand-off frame: idle playback resumes at 1x, so the
                // monotonic-decrease check only applies to the walk coast.
                break;
            }

            Assert.True(session.VisualPlaybackRate <= previousRate + 1e-5f);
            previousRate = session.VisualPlaybackRate;
        }

        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void RunStopRateDecreasesTowardZero()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        Assert.Equal(1f, session.VisualPlaybackRate, precision: 5);

        Step(session, ref time, 0.01, NoInput());
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate < 1f);
    }

    [Fact]
    public void PoseClockSlowsDuringCoast()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));

        var steadyBefore = session.VisualPoseElapsedSeconds;
        Step(session, ref time, 0.02, Move(forward: true));
        var steadyAdvance = session.VisualPoseElapsedSeconds - steadyBefore;

        // Coast (no input) at the same dt: rate < 1 so pose advances less.
        Step(session, ref time, 0.02, NoInput());
        var coastBefore = session.VisualPoseElapsedSeconds;
        Step(session, ref time, 0.02, NoInput());
        var coastAdvance = session.VisualPoseElapsedSeconds - coastBefore;

        Assert.True(coastAdvance < steadyAdvance);
    }

    [Fact]
    public void IdleSwitchDependsOnSpeedStateNotPoseClockState()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));

        var guard = 0;
        var previousSpeed = session.CurrentMoveSpeedMetersPerSecond;
        var previousClip = session.ActiveClipName;
        while (session.ActiveClipName != ErikaFigure.IdleClipName && guard++ < 1000)
        {
            previousSpeed = session.CurrentMoveSpeedMetersPerSecond;
            previousClip = session.ActiveClipName;
            Step(session, ref time, 0.005, NoInput());
        }

        // The last locomotion frame still had positive speed; the switch is
        // driven by the Phase 2I threshold, not by any pose-clock phase.
        Assert.Equal(ErikaFigure.WalkClipName, previousClip);
        Assert.True(previousSpeed > MovementSpeedEnvelope.ZeroSpeedThresholdMetersPerSecond);
    }

    // --- crossfades ------------------------------------------------------

    [Fact]
    public void CrossfadePosePhasesRemainFiniteAndInRange()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.005, Move(forward: true));
        Assert.NotNull(session.Transition);

        for (var i = 0; i < 10; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            Assert.True(double.IsFinite(session.TransitionSourcePoseElapsedSeconds));
            Assert.True(double.IsFinite(session.TransitionDestinationPoseElapsedSeconds));
            Assert.True(session.TransitionSourcePoseElapsedSeconds >= 0.0);
            Assert.True(session.TransitionSourcePoseElapsedSeconds <= 4.0);
            Assert.True(session.TransitionDestinationPoseElapsedSeconds >= 0.0);
            Assert.True(session.TransitionDestinationPoseElapsedSeconds <= 1.0);
        }
    }

    [Fact]
    public void RapidReversalsRemainBounded()
    {
        var session = SessionWithClips();
        double time = 0;
        var origin = session.ErikaPosition;
        for (var i = 0; i < 120; i++)
        {
            Step(session, ref time, 0.005, i % 2 == 0 ? Move(forward: true) : NoInput());
            Assert.True(float.IsFinite(session.VisualPlaybackRate));
            Assert.True(session.VisualPlaybackRate >= 0f);
            Assert.True(double.IsFinite(session.VisualPoseElapsedSeconds));
            Assert.True(double.IsFinite(session.ErikaPosition.X + session.ErikaPosition.Z));
            Assert.True(float.IsFinite(session.ErikaYawRadians));
        }

        Assert.True((session.ErikaPosition - origin).Length() < 2f);
    }

    [Fact]
    public void ChainedTransitionContinuesDestinationPoseAsSource()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.005, Move(forward: true)); // idle -> walk
        Assert.NotNull(session.Transition);
        var destinationPose = session.TransitionDestinationPoseElapsedSeconds;

        // Sprint mid-blend chains from the current destination (walk) to run.
        Step(session, ref time, 0.0, Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.Transition!.Value.SourceClipName);
        Assert.Equal(destinationPose, session.TransitionSourcePoseElapsedSeconds, precision: 9);
    }

    [Fact]
    public void ReversalResumesReturningClipPosePhase()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true)); // steady walk

        Step(session, ref time, 0.005, Move(forward: true, sprint: true)); // walk -> run
        Assert.NotNull(session.Transition);
        var sourcePose = session.TransitionSourcePoseElapsedSeconds; // walk phase

        // Releasing sprint returns to the transition's source (walk): reversal.
        Step(session, ref time, 0.0, Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.Transition!.Value.DestinationClipName);
        // The resumed destination continues the old source phase (no pose snap).
        Assert.Equal(sourcePose, session.TransitionDestinationPoseElapsedSeconds, precision: 9);
    }

    // --- root-motion independence (critical) -----------------------------

    [Fact]
    public void VisualSynchronizationDoesNotChangeWorldMotion()
    {
        var synced = SessionWithClips(visualSync: true);
        var nominal = SessionWithClips(visualSync: false);
        double t1 = 0, t2 = 0;

        // Identical scripted run: idle -> walk -> run -> walk -> stop -> back.
        var script = new List<InputState>();
        for (var i = 0; i < 40; i++) script.Add(Move(forward: true));
        for (var i = 0; i < 40; i++) script.Add(Move(forward: true, sprint: true));
        for (var i = 0; i < 40; i++) script.Add(Move(forward: true));
        for (var i = 0; i < 40; i++) script.Add(NoInput());
        for (var i = 0; i < 40; i++) script.Add(Move(back: true, sprint: true));

        foreach (var input in script)
        {
            Step(synced, ref t1, 0.005, input);
            Step(nominal, ref t2, 0.005, input);

            Assert.Equal(nominal.ErikaPosition, synced.ErikaPosition);
            Assert.Equal(nominal.ErikaYawRadians, synced.ErikaYawRadians);
            Assert.Equal(nominal.CurrentMoveSpeedMetersPerSecond, synced.CurrentMoveSpeedMetersPerSecond);
            Assert.Equal(nominal.RootMotionGain, synced.RootMotionGain);
        }

        // The visual clocks are intentionally allowed to differ.
        Assert.Equal(1f, nominal.VisualPlaybackRate);
        Assert.True((synced.ErikaPosition - nominal.ErikaPosition).Length() == 0f);
    }

    [Fact]
    public void DisabledSynchronizationForcesNominalVisualRate()
    {
        var session = SessionWithClips(visualSync: false);
        double time = 0;
        Step(session, ref time, 0.01, Move(forward: true));
        Assert.Equal(1f, session.VisualPlaybackRate);

        Settle(session, ref time, Move(forward: true, sprint: true));
        Assert.Equal(1f, session.VisualPlaybackRate);
    }

    // --- turn-in-place ---------------------------------------------------

    [Fact]
    public void TurnInPlaceKeepsIdleVisualRateAndZeroTranslation()
    {
        var session = SessionWithClips();
        double time = 0;
        var origin = session.ErikaPosition;

        // S requests the opposite heading (180 deg) at rest: enters a turn.
        Step(session, ref time, 0.005, Move(back: true));
        Assert.True(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(1f, session.VisualPlaybackRate);
        Assert.Equal(1f, session.RawVisualPlaybackRate);
        Assert.Equal(origin, session.ErikaPosition);

        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.005, Move(back: true));
            Assert.Equal(origin, session.ErikaPosition);
            Assert.Equal(1f, session.VisualPlaybackRate);
        }
    }

    [Fact]
    public void PostTurnWalkBeginsBelowNominalRate()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.005, Move(back: true));
        Assert.True(session.IsTurningInPlace);

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 500)
        {
            Step(session, ref time, 0.005, Move(back: true));
        }

        Assert.False(session.IsTurningInPlace);
        // The release frame begins the walk/run envelope from rest, so the
        // visible cadence is still below nominal.
        Assert.True(session.VisualPlaybackRate <= 1f);
        Assert.True(float.IsFinite(session.VisualPlaybackRate));
    }

    [Fact]
    public void PostTurnShiftSelectsRunWithSlowedInitialRate()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 0.005, Move(back: true, sprint: true));
        Assert.True(session.IsTurningInPlace);

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 500)
        {
            Step(session, ref time, 0.005, Move(back: true, sprint: true));
        }

        Assert.False(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.VisualPlaybackRate < 1f);
    }
}
