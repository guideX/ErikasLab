using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>Phase 2E locomotion + transition coverage through GameSession.</summary>
public sealed class LocomotionTests
{
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
        var skeleton = TestSkeleton();
        session.SetAnimationData(skeleton, Idle(), Walk(), Run());
        return session;
    }

    private static InputState Move(
        bool forward = false,
        bool back = false,
        bool left = false,
        bool right = false,
        bool sprint = false,
        bool idle = false,
        bool walk = false,
        bool run = false) => new(
        MoveForward: forward, MoveBackward: back, StrafeLeft: left, StrafeRight: right,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        SelectIdle: idle, SelectWalk: walk, SelectRun: run, Sprint: sprint);

    private static InputState NoInput() => Move();

    [Fact]
    public void DefaultStationarySelectsIdle()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), NoInput());
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void MovementSelectsWalk()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    [Fact]
    public void SprintPlusMovementSelectsRun()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
    }

    [Fact]
    public void SprintWithoutMovementRemainsIdle()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(sprint: true));
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void ReleasingMovementReturnsToIdle()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        session.Update(new FrameTime(1.5, 0.016), NoInput());
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void HoldingMovementDoesNotRestartClip()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(10.0, 0.016), Move(forward: true));
        var start = session.ClipStartSeconds;
        session.Update(new FrameTime(10.5, 0.016), Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(start, session.ClipStartSeconds);
    }

    [Fact]
    public void DiagonalIntentIsNormalized()
    {
        var session = SessionWithClips();
        var diagonal = session.ComputeMovementIntent(Move(forward: true, right: true));
        Assert.Equal(1f, diagonal.Length(), precision: 5);
        var cardinal = session.ComputeMovementIntent(Move(forward: true));
        Assert.Equal(1f, cardinal.Length(), precision: 5);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    public void CardinalAndDiagonalHeadingsAreValid(
        bool forward, bool back, bool left, bool right)
    {
        var session = SessionWithClips();
        var input = Move(forward, back, left, right);

        // Compute the entry-decision heading error exactly as the session does
        // (pre-smoothing intent vs. spawn yaw), so the assertion matches the
        // turn-in-place entry branch bit-for-bit.
        var intent = session.ComputeMovementIntent(input);
        var entryError = MathF.Abs(YawSmoothing.WrapToPi(
            MathF.Atan2(intent.X, intent.Z) - session.ErikaYawRadians));

        session.Update(new FrameTime(1.0, 0.016), input);
        Assert.True(float.IsFinite(session.ErikaYawRadians));
        var rotation = Quaternion.CreateFromYawPitchRoll(session.ErikaYawRadians, 0f, 0f);
        Assert.Equal(1f, rotation.Length(), precision: 5);

        // Phase 2J: a large stationary heading error enters turn-in-place (idle
        // held, zero target); a small error starts ordinary walk immediately.
        if (entryError >= GameSession.TurnInPlaceEnterAngleRadians)
        {
            Assert.True(session.IsTurningInPlace);
            Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
            Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
        }
        else
        {
            Assert.False(session.IsTurningInPlace);
            Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
            Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
        }
    }

    [Fact]
    public void OppositeKeysCancelToIdle()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true, back: true));
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        session.Update(new FrameTime(1.0, 0.016), Move(left: true, right: true));
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void IdleProducesNoWorldLocomotion()
    {
        var session = SessionWithClips();
        var start = session.ErikaPosition;
        for (var i = 1; i <= 10; i++)
        {
            session.Update(new FrameTime(i * 0.5, 0.5), NoInput());
        }

        Assert.Equal(start.X, session.ErikaPosition.X, precision: 5);
        Assert.Equal(start.Z, session.ErikaPosition.Z, precision: 5);
    }

    [Fact]
    public void WalkMovesContinuouslyAcrossLoopBoundary()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(100.0, 0.016), Move(forward: true));
        var origin = session.ErikaPosition;
        // Walk 2.5 loops (2.5s at 100u/s native * scale) in 0.25s steps.
        for (var i = 1; i <= 10; i++)
        {
            session.Update(new FrameTime(100.0 + i * 0.25, 0.25), Move(forward: true));
        }

        var traveled = session.ErikaPosition - origin;
        // 2.5 loops * 100 native units * scale meters, heading W (-Z).
        var expectedMeters = 250f * ErikaFigure.Scale;
        Assert.True(traveled.Length() > 0.5f * expectedMeters);
        Assert.True(session.ErikaPosition.Z < origin.Z);
        // No snap-back: distance grows monotonically; a wrap would collapse it.
        Assert.Equal(expectedMeters, traveled.Length(), precision: 2);
    }

    [Fact]
    public void RunIsFasterThanWalk()
    {
        var walker = SessionWithClips();
        walker.Update(new FrameTime(50.0, 0.016), Move(forward: true));
        var walkOrigin = walker.ErikaPosition;
        walker.Update(new FrameTime(51.0, 1.0), Move(forward: true));

        var runner = SessionWithClips();
        runner.Update(new FrameTime(50.0, 0.016), Move(forward: true, sprint: true));
        var runOrigin = runner.ErikaPosition;
        runner.Update(new FrameTime(51.0, 1.0), Move(forward: true, sprint: true));

        var walkDist = (walker.ErikaPosition - walkOrigin).Length();
        var runDist = (runner.ErikaPosition - runOrigin).Length();
        // Walk 1s = 1 loop (100u); run 1s = 2 loops (200u).
        Assert.True(runDist > walkDist);
        Assert.Equal(2f * walkDist, runDist, precision: 3);
    }

    [Fact]
    public void IdleToWalkDoesNotTeleport()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(5.0, 0.016), NoInput());
        var before = session.ErikaPosition;
        session.Update(new FrameTime(5.016, 0.016), Move(forward: true));
        Assert.Equal(before, session.ErikaPosition);
    }

    [Fact]
    public void WalkToRunDoesNotImportStaleDelta()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(20.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(20.5, 0.5), Move(forward: true));
        var atSwitch = session.ErikaPosition;
        session.Update(new FrameTime(20.516, 0.016), Move(forward: true, sprint: true));
        // The switch frame itself applies no delta.
        Assert.Equal(atSwitch.X, session.ErikaPosition.X, precision: 5);
        Assert.Equal(atSwitch.Z, session.ErikaPosition.Z, precision: 5);
    }

    [Fact]
    public void RunReleaseDeceleratesThroughRootMotionInsteadOfTeleporting()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(30.0, 0.016), Move(forward: true, sprint: true));
        session.Update(new FrameTime(30.5, 0.5), Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);

        // Phase 2I: releasing keeps the run clip as the root-motion authority and
        // coasts a little. The step is finite and far smaller than a teleport.
        var before = session.ErikaPosition;
        session.Update(new FrameTime(30.516, 0.016), NoInput());
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > 0f);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond < session.RunAuthoredSpeedMetersPerSecond);
        var step = (session.ErikaPosition - before).Length();
        Assert.True(step > 0f);
        Assert.True(step < 0.2f);

        // Continued release decelerates exactly to zero and hands off to idle.
        session.Update(new FrameTime(31.516, 1.0), NoInput());
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
    }

    [Fact]
    public void DirectionChangesStayFiniteAndBounded()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(40.0, 0.016), Move(forward: true));
        var headings = new (bool F, bool B, bool L, bool R)[]
        {
            (true, false, false, false),
            (false, true, false, false),
            (false, false, true, false),
            (false, false, false, true),
            (true, false, false, true),
        };
        var time = 40.016;
        foreach (var (f, b, l, r) in headings)
        {
            var before = session.ErikaPosition;
            session.Update(new FrameTime(time, 0.016), Move(f, b, l, r));
            var step = session.ErikaPosition - before;
            Assert.True(float.IsFinite(session.ErikaYawRadians));
            Assert.True(float.IsFinite(step.X + step.Z));
            Assert.True(step.Length() < 1.0f);
            time += 0.016;
        }
    }

    [Fact]
    public void RepeatedStartStopRemainsBounded()
    {
        var session = SessionWithClips();
        var origin = session.ErikaPosition;
        var time = 60.0;
        for (var i = 0; i < 20; i++)
        {
            session.Update(new FrameTime(time, 0.016), Move(forward: true));
            time += 0.1;
            session.Update(new FrameTime(time, 0.1), Move(forward: true));
            time += 0.1;
            session.Update(new FrameTime(time, 0.1), NoInput());
            time += 0.1;
        }

        var total = (session.ErikaPosition - origin).Length();
        // 20 bursts of 0.1s walk at ~0.94 m/s ≈ 1.9 m; must stay small, never explode.
        Assert.True(total < 10f);
        Assert.True(total > 0.5f);
        Assert.True(float.IsFinite(total));
    }

    [Fact]
    public void MovementTakesPrecedenceOverDiagnostic()
    {
        var session = SessionWithClips();
        // Diagnostic run requested but WASD says walk: walk wins.
        session.Update(new FrameTime(70.0, 0.016), Move(forward: true, run: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }
}
