using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2J stationary turn-in-place through GameSession. Covers entry policy
/// (stationary threshold, enter angle, hysteresis), shortest-path facing
/// (including the ±π wrap and the deterministic 180° convention), translational
/// gating (zero target/position while gated, envelope ramp after release),
/// Shift/run interaction, input interruption, deceleration interaction,
/// camera/control-basis independence, root-motion and crossfade regressions,
/// and 30/60/144 Hz frame-rate equivalence.
///
/// Path B (procedural fallback): no authored turn clips exist, so idle stays
/// the visual clip while the authoritative yaw rotates through the existing
/// Phase 2F smoother.
/// </summary>
public sealed class TurnInPlaceTests
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

    /// <summary>
    /// Orbit the camera so a W press requests a heading <paramref name="errorRadians"/>
    /// away from the spawn facing (orbit yaw equals the heading error).
    /// </summary>
    private static void OrbitForError(GameSession session, float errorRadians)
    {
        session.CameraRig.SetOrbit(errorRadians, ThirdPersonCamera.DefaultOrbitPitchRadians);
        session.CameraRig.SnapToTarget(session.Camera, session.ErikaPosition);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // --- entry policy ----------------------------------------------------

    [Fact]
    public void StationarySmallErrorDoesNotEnterTurnInPlace()
    {
        var session = SessionWithClips();
        OrbitForError(session, 0.35f); // ~20 degrees
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        Assert.False(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);

        // Ordinary Phase 2F behavior: she turns slightly while travel begins.
        var origin = session.ErikaPosition;
        Settle(session, ref time, Move(forward: true), steps: 40);
        Assert.True((session.ErikaPosition - origin).Length() > 0f);
    }

    [Fact]
    public void StationaryLargeErrorEntersTurnInPlace()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f); // 90 degrees
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        Assert.True(session.IsTurningInPlace);
        Assert.True(session.TurnGatingActive);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
    }

    [Fact]
    public void MovingAboveThresholdDoesNotEnterTurnInPlace()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > GameSession.StationaryTurnSpeedThresholdMetersPerSecond);

        OrbitForError(session, MathF.PI / 2f);
        Step(session, ref time, 0.016, Move(forward: true));

        Assert.False(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
    }

    [Fact]
    public void DeceleratingAboveThresholdDoesNotEnterTurnInPlace()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        Step(session, ref time, 0.01, NoInput());

        // S requests a 180 degree reversal while the run coast is still fast.
        Step(session, ref time, 0.01, Move(back: true));
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > GameSession.StationaryTurnSpeedThresholdMetersPerSecond);
        Assert.False(session.IsTurningInPlace);

        // The coast continues; turn-in-place must not engage above the threshold.
        for (var i = 0; i < 10; i++)
        {
            Step(session, ref time, 0.01, Move(back: true));
            if (session.CurrentMoveSpeedMetersPerSecond > GameSession.StationaryTurnSpeedThresholdMetersPerSecond)
            {
                Assert.False(session.IsTurningInPlace);
            }
        }
    }

    [Fact]
    public void ExactEnterThresholdIsDeterministic()
    {
        // Just below the enter angle: no turn state, ordinary walk.
        var below = SessionWithClips();
        OrbitForError(below, GameSession.TurnInPlaceEnterAngleRadians - 0.01f);
        double belowTime = 0;
        Step(below, ref belowTime, 0.016, Move(forward: true));
        Assert.False(below.IsTurningInPlace);
        Assert.Equal(ErikaFigure.WalkClipName, below.ActiveClipName);

        // Just above the enter angle: turn-in-place engages.
        var above = SessionWithClips();
        OrbitForError(above, GameSession.TurnInPlaceEnterAngleRadians + 0.01f);
        double aboveTime = 0;
        Step(above, ref aboveTime, 0.016, Move(forward: true));
        Assert.True(above.IsTurningInPlace);
        Assert.Equal(ErikaFigure.IdleClipName, above.ActiveClipName);

        // The exact-threshold scenario is deterministic: identical inputs give
        // identical state and yaw across repeated runs.
        var first = SessionWithClips();
        var second = SessionWithClips();
        OrbitForError(first, GameSession.TurnInPlaceEnterAngleRadians);
        OrbitForError(second, GameSession.TurnInPlaceEnterAngleRadians);
        double t1 = 0, t2 = 0;
        Step(first, ref t1, 0.016, Move(forward: true));
        Step(second, ref t2, 0.016, Move(forward: true));
        Assert.Equal(first.IsTurningInPlace, second.IsTurningInPlace);
        Assert.Equal(first.ErikaYawRadians, second.ErikaYawRadians, precision: 6);
    }

    // --- facing / shortest path ------------------------------------------

    [Fact]
    public void LeftTurnUsesShortestPath()
    {
        var session = SessionWithClips();
        OrbitForError(session, -MathF.PI / 2f); // request -pi/2 from spawn pi
        double time = 0;
        var yaw = session.ErikaYawRadians;
        var totalVariation = 0f;
        var guard = 0;
        while (guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            totalVariation += MathF.Abs(YawSmoothing.WrapToPi(session.ErikaYawRadians - yaw));
            yaw = session.ErikaYawRadians;
            if (!session.IsTurningInPlace)
            {
                break;
            }
        }

        // Shortest path: ~75-90 degrees of turning, never the ~270 degree way.
        Assert.True(totalVariation > MathF.PI / 4f);
        Assert.True(totalVariation < MathF.PI);

        // After release the yaw converges exactly onto the requested heading.
        Settle(session, ref time, Move(forward: true), steps: 20);
        Assert.Equal(-MathF.PI / 2f, session.ErikaYawRadians, precision: 3);
    }

    [Fact]
    public void RightTurnUsesShortestPath()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f); // request +pi/2 from spawn pi
        double time = 0;
        var yaw = session.ErikaYawRadians;
        var totalVariation = 0f;
        var guard = 0;
        while (guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            totalVariation += MathF.Abs(YawSmoothing.WrapToPi(session.ErikaYawRadians - yaw));
            yaw = session.ErikaYawRadians;
            if (!session.IsTurningInPlace)
            {
                break;
            }
        }

        Assert.True(totalVariation > MathF.PI / 4f);
        Assert.True(totalVariation < MathF.PI);

        Settle(session, ref time, Move(forward: true), steps: 20);
        Assert.Equal(MathF.PI / 2f, session.ErikaYawRadians, precision: 3);
    }

    [Fact]
    public void HalfTurnIsDeterministicAndTakesTheShortPath()
    {
        var first = SessionWithClips();
        var second = SessionWithClips();
        OrbitForError(first, MathF.PI); // request 0 from spawn pi
        OrbitForError(second, MathF.PI);

        double t1 = 0, t2 = 0;
        Step(first, ref t1, 0.016, Move(forward: true));
        Step(second, ref t2, 0.016, Move(forward: true));
        Assert.True(first.IsTurningInPlace);

        var yaw1 = first.ErikaYawRadians;
        var signedTravel1 = 0f;
        var guard = 0;
        while (guard++ < 1000)
        {
            Step(first, ref t1, 0.005, Move(forward: true));
            signedTravel1 += YawSmoothing.WrapToPi(first.ErikaYawRadians - yaw1);
            yaw1 = first.ErikaYawRadians;
            if (!first.IsTurningInPlace)
            {
                break;
            }
        }

        // Exact pi ambiguity resolves to the positive (left) direction every time.
        Assert.True(signedTravel1 > 0f);
        Assert.True(signedTravel1 < MathF.PI + 0.1f);

        Settle(first, ref t1, Move(forward: true), steps: 30);
        Assert.Equal(0f, first.ErikaYawRadians, precision: 3);

        // Deterministic across runs.
        Settle(second, ref t2, Move(forward: true), steps: 60);
        Assert.Equal(first.ErikaYawRadians, second.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void WrapStraddlingTurnUsesShortestPathAcrossPiBoundary()
    {
        // Settle Erika stationary facing ~175 degrees (5 degrees off spawn).
        var session = SessionWithClips();
        OrbitForError(session, 0.0873f);
        double time = 0;
        Settle(session, ref time, Move(forward: true), steps: 40);
        Step(session, ref time, 0.05, NoInput());
        Settle(session, ref time, NoInput(), steps: 60);
        var settledYaw = session.ErikaYawRadians;
        Assert.True(MathF.Abs(YawSmoothing.WrapToPi(settledYaw - (MathF.PI - 0.0873f))) < 0.05f);

        // Request -135 degrees: a 50 degree error whose shortest path crosses
        // the +/-pi wrap (175 -> 180/-180 -> -135).
        session.CameraRig.SetOrbit(MathF.PI + MathF.PI * 0.75f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        session.CameraRig.SnapToTarget(session.Camera, session.ErikaPosition);
        Step(session, ref time, 0.005, Move(forward: true));
        Assert.True(session.IsTurningInPlace);

        var yaw = session.ErikaYawRadians;
        var totalVariation = 0f;
        var crossedBoundary = false;
        var guard = 0;
        while (guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            totalVariation += MathF.Abs(YawSmoothing.WrapToPi(session.ErikaYawRadians - yaw));
            yaw = session.ErikaYawRadians;
            if (MathF.Abs(yaw) > MathF.PI - 0.07f)
            {
                crossedBoundary = true;
            }

            if (!session.IsTurningInPlace)
            {
                break;
            }
        }

        Assert.True(crossedBoundary);
        // ~35-50 degrees of turning, never the ~315 degree way.
        Assert.True(totalVariation < MathF.PI / 2f);

        Settle(session, ref time, Move(forward: true), steps: 20);
        Assert.Equal(-MathF.PI * 0.75f, session.ErikaYawRadians, precision: 3);
    }

    // --- translational gating --------------------------------------------

    [Fact]
    public void GatedTurnKeepsTargetSpeedZeroAndPositionFixed()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI); // 180 degree reversal from rest
        double time = 0;
        var origin = session.ErikaPosition;
        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.01, Move(forward: true));
            Assert.True(session.IsTurningInPlace);
            Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
            Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
            Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
            Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);
            Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
            Assert.True(float.IsFinite(session.ErikaYawRadians));
        }
    }

    [Fact]
    public void ReleaseAngleEnablesWalkTarget()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        Assert.False(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.IdleClipName, session.Transition!.Value.SourceClipName);
    }

    [Fact]
    public void ShiftDuringTurnSelectsRunOnlyAfterRelease()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true, sprint: true));
        Assert.True(session.IsTurningInPlace);

        // Shift must not create run displacement during the gated turn.
        for (var i = 0; i < 8; i++)
        {
            Step(session, ref time, 0.01, Move(forward: true, sprint: true));
            Assert.True(session.IsTurningInPlace);
            Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
            Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
        }

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        }

        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
    }

    [Fact]
    public void SpeedRampsThroughEnvelopeAfterRelease()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        // Run the turn; capture the speed on the release frame.
        var guard = 0;
        var speedAtRelease = 0f;
        while (guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            if (!session.IsTurningInPlace)
            {
                speedAtRelease = session.CurrentMoveSpeedMetersPerSecond;
                break;
            }
        }

        // Release frame: the envelope just started from zero (one 16 m/s^2 step).
        Assert.True(speedAtRelease > 0f);
        Assert.True(speedAtRelease <= MovementSpeedEnvelope.DefaultAccelerationMetersPerSecondSquared * 0.005f + 1e-3f);
        Assert.True(speedAtRelease < session.WalkAuthoredSpeedMetersPerSecond);

        // The envelope keeps ramping; no full-speed jump at release.
        var origin = session.ErikaPosition;
        Settle(session, ref time, Move(forward: true), steps: 50);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond <= session.WalkAuthoredSpeedMetersPerSecond);
        Assert.True((session.ErikaPosition - origin).Length() < 0.5f);
    }

    [Fact]
    public void FirstWalkFrameAfterTurnAppliesZeroHistoricalDelta()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        // Step until release, capturing the position just before the release frame.
        var guard = 0;
        var preReleasePosition = session.ErikaPosition;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            preReleasePosition = session.ErikaPosition;
            Step(session, ref time, 0.005, Move(forward: true));
        }

        // The release frame switched idle->walk with a seeded clip clock, so it
        // applied zero root delta.
        Assert.Equal(preReleasePosition.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(preReleasePosition.Z, session.ErikaPosition.Z, precision: 6);

        // The next frame consumes authored walk travel.
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True((session.ErikaPosition - preReleasePosition).Length() > 0f);
    }

    // --- hysteresis -------------------------------------------------------

    [Fact]
    public void ReleaseThresholdIsSmallerThanEnterThreshold()
    {
        Assert.True(GameSession.TurnInPlaceReleaseAngleRadians < GameSession.TurnInPlaceEnterAngleRadians);
    }

    [Fact]
    public void AlternatingOppositeInputsWhileStationaryRemainBounded()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        var entries = 0;
        var wasTurning = false;
        var origin = session.ErikaPosition;
        var minYaw = float.MaxValue;
        var maxYaw = float.MinValue;
        for (var i = 0; i < 100; i++)
        {
            // Alternate W (yaw pi/2) and S (yaw -pi/2) every frame: opposite
            // intents keep the heading error large, so the turn never releases.
            Step(session, ref time, 0.005, i % 2 == 0 ? Move(forward: true) : Move(back: true));
            if (session.IsTurningInPlace && !wasTurning)
            {
                entries++;
            }

            wasTurning = session.IsTurningInPlace;
            minYaw = MathF.Min(minYaw, session.ErikaYawRadians);
            maxYaw = MathF.Max(maxYaw, session.ErikaYawRadians);
            Assert.True(float.IsFinite(session.ErikaYawRadians));
        }

        // Enters once, never releases, never translates: no queued turns.
        Assert.Equal(1, entries);
        Assert.True(session.IsTurningInPlace);
        Assert.True(maxYaw - minYaw < 0.5f);
        Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);
    }

    [Fact]
    public void ReleasedTurnDoesNotReenterOnSmallHeadingWobble()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }
        Assert.False(session.IsTurningInPlace);

        // Wobble the requested heading +/-10 degrees around the turn target
        // (orbit pi/2 ∓ 0.17); the error never reaches the 45 degree enter
        // angle, so no turn restarts.
        for (var i = 0; i < 50; i++)
        {
            session.CameraRig.SetOrbit(
                MathF.PI / 2f + (i % 2 == 0 ? 0.17f : -0.17f),
                ThirdPersonCamera.DefaultOrbitPitchRadians);
            Step(session, ref time, 0.005, Move(forward: true));
            Assert.False(session.IsTurningInPlace);
        }
    }

    // --- input interruption ----------------------------------------------

    [Fact]
    public void ReleaseKeyMidTurnCancelsTurnAndRetainsFacing()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);
        var midYaw = session.ErikaYawRadians;

        Step(session, ref time, 0.016, NoInput());
        Assert.False(session.IsTurningInPlace);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);

        // Facing is retained: no further yaw integration without intent.
        Settle(session, ref time, NoInput(), steps: 20);
        Assert.Equal(midYaw, session.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void DirectionChangeMidTurnTracksNewestHeading()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f); // W requests +pi/2
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);
        var origin = session.ErikaPosition;

        // D at this orbit requests +Z (yaw 0); the turn retargets without queue.
        Step(session, ref time, 0.016, Move(right: true));
        Assert.True(session.IsTurningInPlace);
        Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(right: true));
        }

        Settle(session, ref time, Move(right: true), steps: 20);
        Assert.Equal(0f, session.ErikaYawRadians, precision: 3);
    }

    [Fact]
    public void ShiftTappingDuringTurnDoesNotCorruptState()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        for (var i = 0; i < 12; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: i % 2 == 0));
            Assert.True(session.IsTurningInPlace);
            Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
            Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
        }

        // Post-release target matches the final Shift state (walk here).
        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: false));
        }

        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
    }

    // --- deceleration interaction -----------------------------------------

    [Fact]
    public void NewDirectionDuringRunCoastDoesNotEnterTurnInPlace()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        Step(session, ref time, 0.01, NoInput());

        // S requests a reversal; the run coast is still fast.
        Step(session, ref time, 0.01, Move(back: true));
        var guard = 0;
        while (guard++ < 40)
        {
            Step(session, ref time, 0.005, Move(back: true));
            // Speed stays well above the stationary threshold throughout.
            Assert.True(session.CurrentMoveSpeedMetersPerSecond > GameSession.StationaryTurnSpeedThresholdMetersPerSecond);
            Assert.False(session.IsTurningInPlace);
        }

        // The existing coast-facing policy is preserved: she decelerates toward
        // the walk target and turns toward the new heading while doing so.
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
        Assert.True(session.ErikaYawRadians < MathF.PI);
    }

    [Fact]
    public void CoastToStopThenNewDirectionEntersTurnInPlace()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true));

        // Release and coast to a full stop (target 0, speed decays to zero).
        Step(session, ref time, 0.075, NoInput());
        Step(session, ref time, 0.005, NoInput());
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);

        // A new direction with a large error now enters a stationary turn.
        OrbitForError(session, MathF.PI / 2f);
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void OrbitDuringCoastDoesNotSteerAndTurnStartsAfterStop()
    {
        var session = SessionWithClips();
        double time = 0;
        Settle(session, ref time, Move(forward: true, sprint: true));
        var heading = session.ErikaYawRadians;
        Step(session, ref time, 0.01, NoInput());

        // Orbit during the coast with no movement input: the facing must not change.
        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.01, Look(new Vector2(40f, 0f)));
        }

        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);

        // After the coast stops, W requests a new heading: a stationary
        // turn-in-place begins.
        var guard = 0;
        while (session.CurrentMoveSpeedMetersPerSecond > 0f && guard++ < 1000)
        {
            Step(session, ref time, 0.005, NoInput());
        }

        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond);
    }

    // --- camera / control basis ------------------------------------------

    [Fact]
    public void OrbitWithoutMovementDoesNotRotateErika()
    {
        var session = SessionWithClips();
        var spawnYaw = session.ErikaYawRadians;
        var origin = session.ErikaPosition;
        double time = 0;
        for (var i = 0; i < 30; i++)
        {
            Step(session, ref time, 0.016, Look(new Vector2(25f, 0f)));
        }

        Assert.Equal(spawnYaw, session.ErikaYawRadians, precision: 6);
        Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);
        Assert.False(session.IsTurningInPlace);
    }

    [Fact]
    public void OrbitThenWTurnsTowardControlForward()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        // She ends facing the control forward (+X at this orbit), and the camera
        // kept looking at her throughout.
        Settle(session, ref time, Move(forward: true), steps: 20);
        Assert.Equal(MathF.PI / 2f, session.ErikaYawRadians, precision: 3);
        var toTarget = session.CameraRig.TargetPoint(session.ErikaPosition) - session.Camera.Position;
        var look = Vector3.Normalize(toTarget);
        Assert.True(Vector3.Dot(session.Camera.Forward, look) > 0.9999f);
    }

    [Fact]
    public void SmallCameraCorrectionDoesNotTriggerTurnThreshold()
    {
        var session = SessionWithClips();
        OrbitForError(session, 0.17f); // ~10 degrees
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        // The threshold is evaluated against the control basis heading error;
        // a small correction never enters a turn regardless of the rendered view.
        Assert.False(session.IsTurningInPlace);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    // --- root-motion regressions -----------------------------------------

    [Fact]
    public void NoTranslationDuringStationaryProceduralTurn()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI); // 180 degree reversal from rest
        double time = 0;
        var origin = session.ErikaPosition;
        Step(session, ref time, 0.016, Move(forward: true));
        Assert.True(session.IsTurningInPlace);

        // Run well past the full 180 degree turn (0.23 s) while gated.
        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.01, Move(forward: true));
        }

        Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond);

        // After release she travels along the reversed heading.
        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        Settle(session, ref time, Move(forward: true), steps: 40);
        Assert.Equal(0f, session.ErikaYawRadians, precision: 2);
        Assert.True(session.ErikaPosition.Z > origin.Z); // reversed heading is +Z
    }

    [Fact]
    public void TurnThenWalkSeveralLoopsIsExact()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        // Settle to steady walk, then measure exactly one loop of travel.
        Settle(session, ref time, Move(forward: true), steps: 60);
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
    public void TurnThenRunSeveralLoopsIsExact()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true, sprint: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        }

        Settle(session, ref time, Move(forward: true, sprint: true), steps: 80);
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.CurrentMoveSpeedMetersPerSecond, precision: 4);
        var origin = session.ErikaPosition;
        for (var i = 0; i < 200; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        }

        // Run is two loops per second: 1 s measures 200 native units.
        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(200f * ErikaFigure.Scale, traveled, precision: 3);
    }

    [Fact]
    public void NonUnitRootGainRemainsStableAfterTurn()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        // Switch walk->run (non-unit gain) and run well past several loops; a
        // broken wrap would produce a large reverse step at a seam.
        Step(session, ref time, 0.005, Move(forward: true, sprint: true));
        var previous = session.ErikaPosition;
        var maxStep = 0f;
        for (var i = 0; i < 300; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true, sprint: true));
            maxStep = MathF.Max(maxStep, (session.ErikaPosition - previous).Length());
            previous = session.ErikaPosition;
        }

        Assert.True(maxStep < 0.2f);
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
    }

    // --- animation regressions -------------------------------------------

    [Fact]
    public void TurnHoldsIdleWithoutRestartingClip()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.005, Move(forward: true));
        var clipStart = session.ClipStartSeconds;

        for (var i = 0; i < 20; i++)
        {
            Step(session, ref time, 0.005, Move(forward: true));
            Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
            Assert.Equal(clipStart, session.ClipStartSeconds);
            Assert.Null(session.Transition);
        }
    }

    [Fact]
    public void TurnReleaseStartsSingleBoundedCrossfade()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        while (session.IsTurningInPlace && guard++ < 1000)
        {
            Step(session, ref time, 0.005, Move(forward: true));
        }

        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.IdleClipName, session.Transition!.Value.SourceClipName);
        Assert.Equal(ErikaFigure.WalkClipName, session.Transition.Value.DestinationClipName);

        // The single crossfade retires; no accumulation.
        Settle(session, ref time, Move(forward: true), steps: 60);
        Assert.Null(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    [Fact]
    public void AllStateRemainsFiniteDuringInterruptedTurn()
    {
        var session = SessionWithClips();
        double time = 0;
        var inputs = new[]
        {
            Move(forward: true),
            Move(forward: true),
            Move(right: true),
            NoInput(),
            Move(back: true, sprint: true),
            Move(left: true),
            NoInput(),
        };

        foreach (var input in inputs)
        {
            for (var i = 0; i < 10; i++)
            {
                Step(session, ref time, 0.005, input);
                Assert.True(float.IsFinite(session.ErikaYawRadians));
                Assert.True(IsFinite(session.ErikaPosition));
                Assert.True(float.IsFinite(session.CurrentMoveSpeedMetersPerSecond));
                Assert.True(session.CurrentMoveSpeedMetersPerSecond >= 0f);
            }
        }
    }

    // --- quantitative turn timing ----------------------------------------

    [Fact]
    public void NinetyDegreeTurnTimingMatchesTurnSpeed()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI / 2f);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        var releaseTime = 0f;
        while (guard++ < 1000)
        {
            var frameStart = time;
            Step(session, ref time, 0.005, Move(forward: true));
            if (!session.IsTurningInPlace)
            {
                releaseTime = (float)frameStart;
                break;
            }
        }

        // (90 - 15) degrees at 4pi rad/s, within one frame step of slack.
        var expected = (MathF.PI / 2f - GameSession.TurnInPlaceReleaseAngleRadians)
            / GameSession.TurnSpeedRadiansPerSecond;
        Assert.True(MathF.Abs(releaseTime - expected) < 0.01f);
    }

    [Fact]
    public void ReversalTurnTimingMatchesTurnSpeed()
    {
        var session = SessionWithClips();
        OrbitForError(session, MathF.PI);
        double time = 0;
        Step(session, ref time, 0.016, Move(forward: true));

        var guard = 0;
        var releaseTime = 0f;
        while (guard++ < 1000)
        {
            var frameStart = time;
            Step(session, ref time, 0.005, Move(forward: true));
            if (!session.IsTurningInPlace)
            {
                releaseTime = (float)frameStart;
                break;
            }
        }

        // (180 - 15) degrees at 4pi rad/s, within one frame step of slack.
        var expected = (MathF.PI - GameSession.TurnInPlaceReleaseAngleRadians)
            / GameSession.TurnSpeedRadiansPerSecond;
        Assert.True(MathF.Abs(releaseTime - expected) < 0.01f);
    }

    // --- frame-rate equivalence ------------------------------------------

    [Fact]
    public void YawAfterFixedElapsedTimeMatchesAcrossFrameRates()
    {
        var thirty = SessionWithClips();
        var sixty = SessionWithClips();
        var oneFortyFour = SessionWithClips();
        OrbitForError(thirty, MathF.PI / 2f);
        OrbitForError(sixty, MathF.PI / 2f);
        OrbitForError(oneFortyFour, MathF.PI / 2f);

        double t30 = 0, t60 = 0, t144 = 0;
        Step(thirty, ref t30, 1.0 / 30.0, Move(forward: true));
        Step(sixty, ref t60, 1.0 / 60.0, Move(forward: true));
        Step(oneFortyFour, ref t144, 1.0 / 144.0, Move(forward: true));

        // Mid-turn: yaw is elapsed-time integrated, so rates agree within one
        // coarse frame step.
        var maxDelta = MathF.Max(
            MathF.Abs(YawSmoothing.WrapToPi(thirty.ErikaYawRadians - sixty.ErikaYawRadians)),
            MathF.Abs(YawSmoothing.WrapToPi(thirty.ErikaYawRadians - oneFortyFour.ErikaYawRadians)));
        Assert.True(maxDelta <= GameSession.TurnSpeedRadiansPerSecond * (1.0f / 30.0f) + 1e-3f);

        // After enough elapsed time the 90 degree turn is complete at every rate.
        Settle(thirty, ref t30, Move(forward: true), steps: 29);
        Settle(sixty, ref t60, Move(forward: true), steps: 58);
        Settle(oneFortyFour, ref t144, Move(forward: true), steps: 143);
        Assert.Equal(MathF.PI / 2f, thirty.ErikaYawRadians, precision: 3);
        Assert.Equal(thirty.ErikaYawRadians, sixty.ErikaYawRadians, precision: 3);
        Assert.Equal(thirty.ErikaYawRadians, oneFortyFour.ErikaYawRadians, precision: 3);
    }

    [Fact]
    public void ReleaseTimingDiffersByAtMostOneCoarseFrameStep()
    {
        var thirty = SessionWithClips();
        var sixty = SessionWithClips();
        var oneFortyFour = SessionWithClips();
        OrbitForError(thirty, MathF.PI / 2f);
        OrbitForError(sixty, MathF.PI / 2f);
        OrbitForError(oneFortyFour, MathF.PI / 2f);

        double t30 = 0, t60 = 0, t144 = 0;
        Step(thirty, ref t30, 1.0 / 30.0, Move(forward: true));
        Step(sixty, ref t60, 1.0 / 60.0, Move(forward: true));
        Step(oneFortyFour, ref t144, 1.0 / 144.0, Move(forward: true));

        float ReleaseTime(GameSession session, ref double time, double dt)
        {
            var guard = 0;
            while (guard++ < 1000)
            {
                var frameStart = time;
                Step(session, ref time, dt, Move(forward: true));
                if (!session.IsTurningInPlace)
                {
                    return (float)frameStart;
                }
            }

            return (float)time;
        }

        var r30 = ReleaseTime(thirty, ref t30, 1.0 / 30.0);
        var r60 = ReleaseTime(sixty, ref t60, 1.0 / 60.0);
        var r144 = ReleaseTime(oneFortyFour, ref t144, 1.0 / 144.0);

        // Threshold-crossing times may differ by at most a small bounded
        // timestep amount (one 30 Hz frame here).
        var spread = MathF.Max(r30, MathF.Max(r60, r144)) - MathF.Min(r30, MathF.Min(r60, r144));
        Assert.True(spread <= 1.0 / 30.0 + 0.001f);

        // Speed at release is a single frame of envelope ramp from zero at
        // every rate (never a full-speed jump).
        Assert.True(thirty.CurrentMoveSpeedMetersPerSecond < thirty.WalkAuthoredSpeedMetersPerSecond);
        Assert.True(sixty.CurrentMoveSpeedMetersPerSecond < sixty.WalkAuthoredSpeedMetersPerSecond);
        Assert.True(oneFortyFour.CurrentMoveSpeedMetersPerSecond < oneFortyFour.WalkAuthoredSpeedMetersPerSecond);
    }

    [Fact]
    public void SpeedAfterFixedPostReleaseTimeMatchesAcrossFrameRates()
    {
        var thirty = SessionWithClips();
        var sixty = SessionWithClips();
        var oneFortyFour = SessionWithClips();
        OrbitForError(thirty, MathF.PI / 2f);
        OrbitForError(sixty, MathF.PI / 2f);
        OrbitForError(oneFortyFour, MathF.PI / 2f);

        double t30 = 0, t60 = 0, t144 = 0;
        Step(thirty, ref t30, 1.0 / 30.0, Move(forward: true));
        Step(sixty, ref t60, 1.0 / 60.0, Move(forward: true));
        Step(oneFortyFour, ref t144, 1.0 / 144.0, Move(forward: true));

        var guard = 0;
        while (thirty.IsTurningInPlace && guard++ < 1000)
        {
            Step(thirty, ref t30, 1.0 / 30.0, Move(forward: true));
        }

        guard = 0;
        while (sixty.IsTurningInPlace && guard++ < 1000)
        {
            Step(sixty, ref t60, 1.0 / 60.0, Move(forward: true));
        }

        guard = 0;
        while (oneFortyFour.IsTurningInPlace && guard++ < 1000)
        {
            Step(oneFortyFour, ref t144, 1.0 / 144.0, Move(forward: true));
        }

        // 0.2 s of post-release envelope at each rate: the envelope is
        // elapsed-time based, so all rates converge to the same steady speed.
        Settle(thirty, ref t30, Move(forward: true), steps: 40);
        Settle(sixty, ref t60, Move(forward: true), steps: 40);
        Settle(oneFortyFour, ref t144, Move(forward: true), steps: 40);
        Assert.Equal(thirty.CurrentMoveSpeedMetersPerSecond, sixty.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(thirty.CurrentMoveSpeedMetersPerSecond, oneFortyFour.CurrentMoveSpeedMetersPerSecond, precision: 4);
    }
}
