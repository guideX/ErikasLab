using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2G third-person follow/orbit camera. Covers portable orbit math,
/// elapsed-time follow smoothing (including frame-split equivalence and snap
/// policy), camera-relative WASD, player/camera facing independence, and
/// root-motion/transition regressions.
/// </summary>
public sealed class ThirdPersonCameraTests
{
    // --- shared fixtures -------------------------------------------------

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
        ExitRequested: false, MouseDelta: default,
        Sprint: sprint);

    private static InputState Look(Vector2 mouseDelta) => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: mouseDelta);

    private static InputState MoveLook(bool forward, Vector2 mouseDelta) => new(
        MoveForward: forward, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: mouseDelta);

    private static InputState NoInput() => Move();

    /// <summary>Set the orbit and push it into the session camera immediately.</summary>
    private static void SetOrbit(GameSession session, float yaw, float pitch)
    {
        session.CameraRig.SetOrbit(yaw, pitch);
        session.CameraRig.Follow(session.Camera, session.ErikaPosition, default);
    }

    private static Vector3 HorizontalForward(CameraState camera)
    {
        var forward = camera.Forward with { Y = 0f };
        return Vector3.Normalize(forward);
    }

    private static Vector3 HorizontalRight(CameraState camera)
    {
        var right = camera.Right with { Y = 0f };
        return Vector3.Normalize(right);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // --- orbit math ------------------------------------------------------

    [Fact]
    public void DefaultFramingProducesFiniteNormalizedVectors()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SnapToTarget(camera, new Vector3(0, 0, -1));

        Assert.True(IsFinite(camera.Position));
        Assert.True(IsFinite(camera.Forward));
        Assert.True(IsFinite(camera.Right));
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
        Assert.Equal(1f, camera.Right.Length(), precision: 5);
    }

    [Fact]
    public void YawRotatesCameraPositionAroundTarget()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(distanceMeters: 5f, targetHeightOffsetMeters: 1f);
        var target = new Vector3(0, 0, -1);

        rig.SetOrbit(0f, 0f);
        rig.SnapToTarget(camera, target);
        Assert.Equal(target.X, camera.Position.X, precision: 4);
        Assert.Equal(target.Y + 1f, camera.Position.Y, precision: 4);
        Assert.Equal(target.Z + 5f, camera.Position.Z, precision: 4);

        rig.SetOrbit(MathF.PI / 2f, 0f);
        rig.SnapToTarget(camera, target);
        Assert.Equal(target.X - 5f, camera.Position.X, precision: 4);
        Assert.Equal(target.Z, camera.Position.Z, precision: 4);
    }

    [Fact]
    public void YawWrapsCleanly()
    {
        var rig = new ThirdPersonCamera();

        rig.SetOrbit(MathF.PI + 0.25f, 0f);
        Assert.True(rig.OrbitYawRadians > -MathF.PI && rig.OrbitYawRadians <= MathF.PI);
        Assert.Equal(-(MathF.PI - 0.25f), rig.OrbitYawRadians, precision: 4);

        rig.SetOrbit(6f * MathF.PI, 0f);
        Assert.Equal(0f, rig.OrbitYawRadians, precision: 4);

        rig.SetOrbit(float.NaN, 0f);
        Assert.Equal(0f, rig.OrbitYawRadians, precision: 4);
    }

    [Fact]
    public void YawWrapBoundaryDoesNotJump()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        var target = Vector3.Zero;

        rig.SetOrbit(MathF.PI - 0.01f, 0f);
        rig.SnapToTarget(camera, target);
        var before = camera.Forward;

        rig.SetOrbit(-MathF.PI + 0.01f, 0f);
        rig.SnapToTarget(camera, target);
        var after = camera.Forward;

        Assert.True(Vector3.Dot(before, after) > 0.999f);
    }

    [Fact]
    public void PitchClampsAtBothLimits()
    {
        var rig = new ThirdPersonCamera();

        rig.SetOrbit(0f, -100f);
        Assert.Equal(ThirdPersonCamera.MinPitchRadians, rig.OrbitPitchRadians, precision: 5);

        rig.SetOrbit(0f, 100f);
        Assert.Equal(ThirdPersonCamera.MaxPitchRadians, rig.OrbitPitchRadians, precision: 5);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, -1.2f)]
    [InlineData(0f, 0.5f)]
    [InlineData(1.5707964f, -0.7f)]
    [InlineData(3.1415927f, 0.25f)]
    [InlineData(-1.5707964f, -0.2f)]
    [InlineData(0.7f, 0.1f)]
    public void ExtremeValidOrbitRemainsFiniteAndNormalized(float yaw, float pitch)
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SetOrbit(yaw, pitch);
        rig.SnapToTarget(camera, new Vector3(0, 0, -1));

        Assert.True(IsFinite(camera.Position));
        Assert.True(IsFinite(camera.Forward));
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
    }

    // --- follow smoothing ------------------------------------------------

    [Fact]
    public void StationaryTargetConvergesTowardDesiredPosition()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SnapToTarget(camera, Vector3.Zero);

        var moved = new Vector3(10, 0, 0);
        var desired = rig.DesiredPosition(moved);
        for (var i = 1; i <= 200; i++)
        {
            rig.Follow(camera, moved, new FrameTime(i * 0.016, 0.016));
        }

        Assert.True(Vector3.Distance(camera.Position, desired) < 1e-3f);
    }

    [Fact]
    public void MovingTargetIsFollowedSmoothlyAndBounded()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        var target = Vector3.Zero;
        rig.SnapToTarget(camera, target);

        var previous = camera.Position;
        var maxError = 0f;
        for (var i = 1; i <= 120; i++)
        {
            target += new Vector3(0.05f, 0f, 0f);
            rig.Follow(camera, target, new FrameTime(i * 0.016, 0.016));

            maxError = MathF.Max(maxError, Vector3.Distance(camera.Position, rig.DesiredPosition(target)));
            Assert.True(Vector3.Distance(camera.Position, previous) < 1f);
            previous = camera.Position;
        }

        Assert.True(maxError < 1f);
        Assert.True(IsFinite(camera.Position));
    }

    [Fact]
    public void EquivalentElapsedTimeWithDifferentFrameSplitsMatches()
    {
        var single = new ThirdPersonCamera();
        var singleCamera = new CameraState(Vector3.Zero);
        single.SnapToTarget(singleCamera, Vector3.Zero);

        var split = new ThirdPersonCamera();
        var splitCamera = new CameraState(Vector3.Zero);
        split.SnapToTarget(splitCamera, Vector3.Zero);

        var destination = new Vector3(10, 0, 0);
        single.Follow(singleCamera, destination, new FrameTime(1.0, 0.1));
        split.Follow(splitCamera, destination, new FrameTime(1.0, 0.05));
        split.Follow(splitCamera, destination, new FrameTime(1.05, 0.05));

        Assert.Equal(singleCamera.Position.X, splitCamera.Position.X, precision: 4);
        Assert.Equal(singleCamera.Position.Y, splitCamera.Position.Y, precision: 4);
        Assert.Equal(singleCamera.Position.Z, splitCamera.Position.Z, precision: 4);
    }

    [Fact]
    public void ZeroElapsedTimeProducesNoMovement()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SnapToTarget(camera, Vector3.Zero);
        var position = camera.Position;

        rig.Follow(camera, new Vector3(5, 0, 0), new FrameTime(1.0, 0.0));

        Assert.Equal(position.X, camera.Position.X, precision: 5);
        Assert.Equal(position.Y, camera.Position.Y, precision: 5);
        Assert.Equal(position.Z, camera.Position.Z, precision: 5);
    }

    [Fact]
    public void ResetSnapsToTargetFraming()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SnapToTarget(camera, Vector3.Zero);
        rig.Follow(camera, new Vector3(5, 0, 0), new FrameTime(1.0, 0.016));

        var target = new Vector3(5, 0, 0);
        rig.Reset();
        rig.Follow(camera, target, new FrameTime(1.1, 0.016));

        var desired = rig.DesiredPosition(target);
        Assert.Equal(desired.X, camera.Position.X, precision: 5);
        Assert.Equal(desired.Y, camera.Position.Y, precision: 5);
        Assert.Equal(desired.Z, camera.Position.Z, precision: 5);
    }

    [Fact]
    public void LargeElapsedTimeStaysBoundedAndFinite()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SnapToTarget(camera, Vector3.Zero);

        var target = new Vector3(5, 0, 0);
        rig.Follow(camera, target, new FrameTime(1.0, 1000.0));

        Assert.True(IsFinite(camera.Position));
        Assert.True(Vector3.Distance(camera.Position, rig.DesiredPosition(target)) < 0.01f);
    }

    [Fact]
    public void LargeTargetDiscontinuitySnapsInsteadOfFlying()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(snapDistanceMeters: 25f);
        rig.SnapToTarget(camera, Vector3.Zero);

        var target = new Vector3(1000, 0, 0);
        rig.Follow(camera, target, new FrameTime(1.0, 0.016));

        var desired = rig.DesiredPosition(target);
        Assert.Equal(desired.X, camera.Position.X, precision: 3);
        Assert.Equal(desired.Z, camera.Position.Z, precision: 3);
    }

    [Fact]
    public void GameSessionStartsWithSettledThirdPersonCamera()
    {
        var session = new GameSession();

        Assert.True(session.CameraRig.IsInitialized);
        var desired = session.CameraRig.DesiredPosition(session.ErikaPosition);
        Assert.Equal(desired.X, session.Camera.Position.X, precision: 4);
        Assert.Equal(desired.Y, session.Camera.Position.Y, precision: 4);
        Assert.Equal(desired.Z, session.Camera.Position.Z, precision: 4);
        Assert.True(IsFinite(session.Camera.Forward));
        Assert.Equal(1f, session.Camera.Forward.Length(), precision: 5);
    }

    // --- camera-relative input ------------------------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(-1.5707964f)]
    [InlineData(0.7f)]
    public void ForwardIntentMatchesHorizontalCameraForward(float yaw)
    {
        var session = SessionWithClips();
        SetOrbit(session, yaw, 0f);

        var forward = HorizontalForward(session.Camera);
        var intent = session.ComputeMovementIntent(Move(forward: true));

        Assert.Equal(forward.X, intent.X, precision: 5);
        Assert.Equal(forward.Z, intent.Z, precision: 5);
        Assert.Equal(0f, intent.Y, precision: 6);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(-1.5707964f)]
    [InlineData(0.7f)]
    public void BackwardIntentMatchesNegativeCameraForward(float yaw)
    {
        var session = SessionWithClips();
        SetOrbit(session, yaw, 0f);

        var forward = HorizontalForward(session.Camera);
        var intent = session.ComputeMovementIntent(Move(back: true));

        Assert.Equal(-forward.X, intent.X, precision: 5);
        Assert.Equal(-forward.Z, intent.Z, precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(-1.5707964f)]
    [InlineData(0.7f)]
    public void StrafeIntentMatchesCameraRightBasis(float yaw)
    {
        var session = SessionWithClips();
        SetOrbit(session, yaw, 0f);

        var right = HorizontalRight(session.Camera);
        var strafeRight = session.ComputeMovementIntent(Move(right: true));
        var strafeLeft = session.ComputeMovementIntent(Move(left: true));

        Assert.Equal(right.X, strafeRight.X, precision: 5);
        Assert.Equal(right.Z, strafeRight.Z, precision: 5);
        Assert.Equal(-right.X, strafeLeft.X, precision: 5);
        Assert.Equal(-right.Z, strafeLeft.Z, precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(0.7f)]
    public void DiagonalIntentIsNormalizedForOrbitYaws(float yaw)
    {
        var session = SessionWithClips();
        SetOrbit(session, yaw, 0f);

        var diagonal = session.ComputeMovementIntent(Move(forward: true, right: true));
        var cardinal = session.ComputeMovementIntent(Move(forward: true));

        Assert.Equal(1f, diagonal.Length(), precision: 5);
        Assert.Equal(1f, cardinal.Length(), precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1.2f)]
    [InlineData(0.5f)]
    public void PitchDoesNotAddVerticalPlayerMovement(float pitch)
    {
        var session = SessionWithClips();
        SetOrbit(session, 0.9f, pitch);

        var intent = session.ComputeMovementIntent(Move(forward: true));

        Assert.Equal(0f, intent.Y, precision: 6);
        Assert.Equal(1f, intent.Length(), precision: 5);
    }

    [Fact]
    public void OppositeMovementInputsCancel()
    {
        var session = SessionWithClips();
        SetOrbit(session, 0.6f, -0.3f);

        Assert.Equal(Vector3.Zero, session.ComputeMovementIntent(Move(forward: true, back: true)));
        Assert.Equal(Vector3.Zero, session.ComputeMovementIntent(Move(left: true, right: true)));
    }

    // --- player/camera facing independence -------------------------------

    [Fact]
    public void OrbitingWhileStationaryDoesNotChangeErikaYaw()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), NoInput());
        var heading = session.ErikaYawRadians;
        var orbitBefore = session.CameraRig.OrbitYawRadians;

        for (var i = 0; i < 20; i++)
        {
            session.Update(new FrameTime(1.1 + i * 0.016, 0.016), Look(new Vector2(40, 0)));
        }

        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);
        Assert.NotEqual(orbitBefore, session.CameraRig.OrbitYawRadians);
    }

    [Fact]
    public void AfterOrbitingMovementUsesNewCameraDirection()
    {
        var session = SessionWithClips();
        SetOrbit(session, MathF.PI / 2f, 0f);
        var origin = session.ErikaPosition;

        for (var i = 1; i <= 40; i++)
        {
            session.Update(new FrameTime(1.0 + i * 0.016, 0.016), Move(forward: true));
        }

        Assert.True(session.ErikaPosition.X > origin.X);
    }

    [Fact]
    public void ErikaTurnsTowardCameraIntentWithSmoothYaw()
    {
        var session = SessionWithClips();
        SetOrbit(session, MathF.PI / 2f, 0f);

        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var yaw = session.ErikaYawRadians;

        Assert.True(yaw > 0f);
        Assert.True(yaw < MathF.PI / 2f);
    }

    [Fact]
    public void CameraOrbitNeverOverwritesErikaFacingDirectly()
    {
        var session = SessionWithClips();
        SetOrbit(session, 2.1f, -0.4f);
        var heading = session.ErikaYawRadians;

        session.Update(new FrameTime(1.0, 0.016), NoInput());
        session.Update(new FrameTime(1.016, 0.016), NoInput());

        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);
    }

    // --- regressions -----------------------------------------------------

    [Fact]
    public void RootMotionDistanceIsUnaffectedByCameraFollowState()
    {
        var smooth = SessionWithClips();
        var snapped = SessionWithClips();
        smooth.Update(new FrameTime(1.0, 0.016), NoInput());
        snapped.Update(new FrameTime(1.0, 0.016), NoInput());

        for (var i = 1; i <= 60; i++)
        {
            var time = 1.0 + i * 0.016;
            smooth.Update(new FrameTime(time, 0.016), Move(forward: true));
            // Force a hard snap every frame: only the follow state differs.
            snapped.CameraRig.Reset();
            snapped.Update(new FrameTime(time, 0.016), Move(forward: true));
        }

        Assert.Equal(smooth.ErikaPosition.X, snapped.ErikaPosition.X, precision: 4);
        Assert.Equal(smooth.ErikaPosition.Z, snapped.ErikaPosition.Z, precision: 4);
    }

    [Fact]
    public void AuthoritativeWalkDisplacementIsUnchanged()
    {
        var session = SessionWithClips();
        var origin = session.ErikaPosition;

        session.Update(new FrameTime(0.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(1.0, 1.0), Move(forward: true));

        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(100f * ErikaFigure.Scale, traveled, precision: 3);
    }

    [Fact]
    public void CameraFollowDoesNotRestartClips()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var start = session.ClipStartSeconds;

        for (var i = 0; i < 30; i++)
        {
            session.Update(new FrameTime(1.1 + i * 0.016, 0.016), MoveLook(forward: true, mouseDelta: new Vector2(30, 10)));
        }

        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(start, session.ClipStartSeconds);
    }

    [Fact]
    public void OrbitingWhileWalkingStaysBoundedAndSettles()
    {
        var session = SessionWithClips();
        var time = 20.0;
        for (var i = 0; i < 80; i++)
        {
            session.Update(new FrameTime(time, 0.016), MoveLook(forward: true, mouseDelta: new Vector2(25, 0)));
            time += 0.016;
            Assert.True(float.IsFinite(session.ErikaPosition.X + session.ErikaPosition.Z));
            Assert.True(float.IsFinite(session.ErikaYawRadians));
        }

        // Holding walk with a settled camera must retire any crossfade.
        time += 0.5;
        session.Update(new FrameTime(time, 0.5), Move(forward: true));
        Assert.Null(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    [Fact]
    public void LoopSeamRemainsStableWithCameraFollowing()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(10.0, 0.016), Move(forward: true));
        var origin = session.ErikaPosition;

        for (var i = 1; i <= 12; i++)
        {
            session.Update(new FrameTime(10.0 + i * 0.25, 0.25), Move(forward: true));
        }

        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(300f * ErikaFigure.Scale, traveled, precision: 2);
    }
}
