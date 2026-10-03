using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2I acceleration/deceleration movement response through GameSession.
/// Verifies the speed envelope scales authored root motion (single authority),
/// start/stop ramps, walk/run continuity, gain behavior, residual stopping
/// travel, direction policy during deceleration, and frame-rate independence.
/// </summary>
public sealed class MovementResponseTests
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

    private static AnimationClip Walk() => new(
        "walk", 1.0f, 30f, [
            new AnimationChannel(0, [0f, 1f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Run() => new(
        "run", 0.5f, 30f, [
            new AnimationChannel(0, [0f, 0.5f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static GameSession SessionWithClips()
    {
        var session = new GameSession();
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

    private static InputState Look(Vector2 mouseDelta) => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: mouseDelta);

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

    // --- acceleration ----------------------------------------------------

    [Fact]
    public void StartsFromRestAndRampsTowardWalk()
    {
        var session = SessionWithClips();
        double time = 0;
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);

        Step(session, ref time, 0.01, Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > 0f);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond < session.WalkAuthoredSpeedMetersPerSecond);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
        // The switch frame itself applies no historical root delta.
        Assert.Equal(ErikaFigure.GroundPosition, session.ErikaPosition);
    }

    [Fact]
    public void AccelerationProducesLessDisplacementThanSteadyWalkOverShortInterval()
    {
        var accelerating = SessionWithClips();
        double accelTime = 0;
        Step(accelerating, ref accelTime, 0.005, Move(forward: true));
        var accelOrigin = accelerating.ErikaPosition;
        for (var i = 0; i < 10; i++)
        {
            Step(accelerating, ref accelTime, 0.005, Move(forward: true));
        }

        var steady = SessionWithClips();
        double steadyTime = 0;
        Settle(steady, ref steadyTime, Move(forward: true));
        var steadyOrigin = steady.ErikaPosition;
        for (var i = 0; i < 10; i++)
        {
            Step(steady, ref steadyTime, 0.005, Move(forward: true));
        }

        var accelDistance = (accelerating.ErikaPosition - accelOrigin).Length();
        var steadyDistance = (steady.ErikaPosition - steadyOrigin).Length();
        Assert.True(accelDistance > 0f);
        Assert.True(accelDistance < steadyDistance);
    }

    [Fact]
    public void LargeDtAccelerationStillRampsFromRest()
    {
        var session = SessionWithClips();
        double time = 0;
        Step(session, ref time, 1.0, Move(forward: true));
        // Speed clamps safely to the authored walk target; no overshoot/NaN.
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);
    }

    // --- deceleration ----------------------------------------------------

    [Fact]
    public void ReleaseLowersTargetAndKeepsLocomotionUntilStopped()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var movingSpeed = session.CurrentMoveSpeedMetersPerSecond;
        Assert.True(movingSpeed > 0f);

        Step(session, ref time, 0.01, NoInput());
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > 0f);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond < movingSpeed);

        var guard = 0;
        while (session.ActiveClipName != ErikaFigure.IdleClipName && guard++ < 1000)
        {
            Step(session, ref time, 0.005, NoInput());
        }

        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
    }

    [Fact]
    public void DecelerationProducesForwardResidualDisplacement()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        var origin = session.ErikaPosition;

        Step(session, ref time, 0.016, NoInput());
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        var firstStep = session.ErikaPosition - origin;
        Assert.True(firstStep.Z < 0f); // forward is -Z at the default spawn heading
        Assert.True(firstStep.Length() > 0f);

        var guard = 0;
        while (session.ActiveClipName != ErikaFigure.IdleClipName && guard++ < 1000)
        {
            Step(session, ref time, 0.005, NoInput());
        }

        var total = (session.ErikaPosition - origin).Length();
        Assert.True(total > 0f);
        // Residual travel is authored root motion, bounded by the run stopping
        // distance; it is never a separate inertial translation system.
        Assert.True(total < 0.5f);
    }

    // --- walk / run switching -------------------------------------------

    [Fact]
    public void WalkToRunSpeedIsContinuousAndTargetChangesImmediately()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var before = session.CurrentMoveSpeedMetersPerSecond;

        Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        var after = session.CurrentMoveSpeedMetersPerSecond;

        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
        Assert.True(after > before);
        Assert.True(after - before <= 16f * 0.005f + 1e-3f);
    }

    [Fact]
    public void RunToWalkSpeedIsContinuousAndGainMayExceedOne()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        var before = session.CurrentMoveSpeedMetersPerSecond;
        var expectedGain = before / session.WalkAuthoredSpeedMetersPerSecond;

        // Zero-dt switch isolates the gain without any speed change.
        Step(session, ref time, 0.0, Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(before, session.CurrentMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(expectedGain, session.RootMotionGain, precision: 3);
        Assert.True(session.RootMotionGain > 1f);

        // Decelerates smoothly and the gain converges back toward 1.
        Settle(session, ref time, Move(forward: true), steps: 80);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(1f, session.RootMotionGain, precision: 3);
    }

    [Fact]
    public void RunToWalkSwitchFrameAppliesZeroRootDelta()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        var before = session.ErikaPosition;

        Step(session, ref time, 0.016, Move(forward: true));
        Assert.Equal(before.X, session.ErikaPosition.X, precision: 5);
        Assert.Equal(before.Z, session.ErikaPosition.Z, precision: 5);
    }

    // --- steady-state root motion ---------------------------------------

    [Fact]
    public void SteadyWalkReproducesAuthoredDisplacement()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);

        var origin = session.ErikaPosition;
        for (var i = 0; i < 200; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(100f * ErikaFigure.Scale, traveled, precision: 3);
        Assert.Equal(1f, session.RootMotionGain, precision: 4);
    }

    [Fact]
    public void SteadyRunReproducesAuthoredDisplacement()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);

        var origin = session.ErikaPosition;
        for (var i = 0; i < 200; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        }

        // Run is 100 native units per 0.5 s loop: 1.0 s is two loops.
        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(200f * ErikaFigure.Scale, traveled, precision: 3);
    }

    [Fact]
    public void ManyLoopSteadyWalkRemainsExact()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var origin = session.ErikaPosition;
        for (var i = 0; i < 600; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        Assert.Equal(300f * ErikaFigure.Scale, (session.ErikaPosition - origin).Length(), precision: 2);
    }

    [Fact]
    public void LoopSeamsRemainStableUnderNonUnitGain()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));

        // Switch to walk (gain > 1) and run well past a full walk loop while the
        // speed envelope decelerates. A broken wrap would produce a large reverse
        // step at the seam.
        var previous = session.ErikaPosition;
        var maxStep = 0f;
        for (var i = 0; i < 300; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            maxStep = MathF.Max(maxStep, (session.ErikaPosition - previous).Length());
            previous = session.ErikaPosition;
        }

        Assert.True(maxStep < 0.2f);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    // --- start / stop interruptions -------------------------------------

    [Fact]
    public void RestartDuringDecelerationPreservesCurrentSpeed()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));

        Step(session, ref time, 0.05, NoInput());
        var decelerated = session.CurrentMoveSpeedMetersPerSecond;
        Assert.True(decelerated > 0f);
        Assert.True(decelerated < session.RunAuthoredSpeedMetersPerSecond);

        Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond >= decelerated);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond - decelerated <= 16f * 0.005f + 1e-3f);
    }

    [Fact]
    public void RepeatedStartStopRemainsBounded()
    {
        var session = SessionWithClips();
        double time = 0;
        var origin = session.ErikaPosition;
        for (var i = 0; i < 50; i++)
        {
            // Two movement frames so the second (post-switch) frame actually
            // consumes root motion, then two release frames to decay.
            Step(session, ref time, 0.005, Move(forward: true));
            Step(session, ref time, 0.005, Move(forward: true));
            Step(session, ref time, 0.005, NoInput());
            Step(session, ref time, 0.005, NoInput());
            Assert.True(session.CurrentMoveSpeedMetersPerSecond >= 0f);
            Assert.True(float.IsFinite(session.ErikaPosition.X + session.ErikaPosition.Z));
        }

        var total = (session.ErikaPosition - origin).Length();
        Assert.True(total > 0f);
        Assert.True(total < 2f);

        Step(session, ref time, 1.0, NoInput());
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    // --- direction policy ------------------------------------------------

    [Fact]
    public void ReleasePreservesYawDuringDeceleration()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        var heading = session.ErikaYawRadians;

        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.01, NoInput());
        }

        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void CameraOrbitDuringDecelerationDoesNotSteerErika()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        Step(session, ref time, 0.016, NoInput());
        var heading = session.ErikaYawRadians;

        var look = Look(new Vector2(200f, 0f));
        for (var i = 0; i < 10; i++)
        {
            Step(session, ref time, 0.01, look);
        }

        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void NewInputDuringDecelerationResumesYawTargeting()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        Step(session, ref time, 0.02, NoInput());
        var heading = session.ErikaYawRadians;

        // S requests +Z (target yaw 0) while the stop is still in progress.
        Step(session, ref time, 0.02, Move(back: true));
        Assert.True(session.ErikaYawRadians < heading);
        Assert.True(float.IsFinite(session.ErikaYawRadians));
    }

    // --- frame-rate independence ----------------------------------------

    [Fact]
    public void EquivalentElapsedTimeGivesEquivalentSpeedAcrossFrameRates()
    {
        var thirty = SessionWithClips();
        var sixty = SessionWithClips();
        var oneFortyFour = SessionWithClips();
        double t30 = 0, t60 = 0, t144 = 0;

        Step(thirty, ref t30, 0.05, Move(forward: true));

        Step(sixty, ref t60, 0.025, Move(forward: true));
        Step(sixty, ref t60, 0.025, Move(forward: true));

        for (var i = 0; i < 10; i++)
        {
            Step(oneFortyFour, ref t144, 0.005, Move(forward: true));
        }

        Assert.Equal(thirty.CurrentMoveSpeedMetersPerSecond, sixty.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(thirty.CurrentMoveSpeedMetersPerSecond, oneFortyFour.CurrentMoveSpeedMetersPerSecond, precision: 4);
    }
}
