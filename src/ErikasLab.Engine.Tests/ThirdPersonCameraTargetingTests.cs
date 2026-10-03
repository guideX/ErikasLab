using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2H camera targeting + spawn-facing consistency. Covers the split
/// between the rendered look-at basis (<c>camera.Forward = normalize(target -
/// camera.Position)</c>) and the orbit/control basis used by WASD, degenerate
/// look-direction fallback, spawn alignment with initial forward movement, and
/// the guarantee that camera targeting never alters root motion.
/// </summary>
public sealed class ThirdPersonCameraTargetingTests
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

    private static InputState MoveLook(bool forward, Vector2 mouseDelta, bool sprint = false) => new(
        MoveForward: forward, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: mouseDelta, Sprint: sprint);

    private static InputState NoInput() => Move();

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Angular error (radians) between camera forward and the target direction.
    /// Uses atan2(|a x b|, a . b), which stays accurate for tiny angles (unlike
    /// acos near 1).
    /// </summary>
    private static float LookAtError(CameraState camera, Vector3 targetPoint)
    {
        var toTarget = targetPoint - camera.Position;
        if (!IsFinite(toTarget) || toTarget.LengthSquared() < 1e-12f)
        {
            return 0f;
        }

        var look = Vector3.Normalize(toTarget);
        var cross = Vector3.Cross(camera.Forward, look).Length();
        var dot = Vector3.Dot(camera.Forward, look);
        return MathF.Atan2(cross, dot);
    }

    // --- rendered view targeting ----------------------------------------

    [Fact]
    public void RenderedForwardEqualsNormalizedTargetMinusPosition()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(distanceMeters: 5f, targetHeightOffsetMeters: 0f, orbitPitchRadians: 0f);
        rig.SnapToTarget(camera, Vector3.Zero);

        var target = new Vector3(5f, 0f, 0f);
        rig.Follow(camera, target, new FrameTime(1.0, 0.05));

        var expected = Vector3.Normalize(rig.TargetPoint(target) - camera.Position);
        Assert.Equal(expected.X, camera.Forward.X, precision: 4);
        Assert.Equal(expected.Y, camera.Forward.Y, precision: 4);
        Assert.Equal(expected.Z, camera.Forward.Z, precision: 4);
    }

    [Fact]
    public void MovingTargetUpdatesRenderedViewDirection()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(distanceMeters: 5f, targetHeightOffsetMeters: 0f, orbitPitchRadians: 0f);
        var target = Vector3.Zero;
        rig.SnapToTarget(camera, target);

        var time = 0.0;
        for (var i = 0; i < 60; i++)
        {
            target += new Vector3(0f, 0f, -0.05f);
            time += 0.016;
            rig.Follow(camera, target, new FrameTime(time, 0.016));
            Assert.True(LookAtError(camera, rig.TargetPoint(target)) < 1e-5f);
        }
    }

    [Fact]
    public void FollowLagDoesNotLeaveStaleOrbitForwardAsRenderedForward()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(distanceMeters: 5f, targetHeightOffsetMeters: 0f, orbitPitchRadians: 0f);
        rig.SnapToTarget(camera, Vector3.Zero);
        var orbitForward = ThirdPersonCamera.ForwardFromOrbit(rig.OrbitYawRadians, rig.OrbitPitchRadians);

        // A lateral target jump with a small dt leaves the position trailing.
        var target = new Vector3(5f, 0f, 0f);
        rig.Follow(camera, target, new FrameTime(1.0, 0.05));

        // The rendered view points at the target, not along the stale orbit basis.
        Assert.True(Vector3.Dot(camera.Forward, orbitForward) < 0.99f);
        Assert.True(LookAtError(camera, rig.TargetPoint(target)) < 1e-5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1.2f)]
    [InlineData(0.5f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(-1.5707964f)]
    [InlineData(0.7f)]
    public void RenderedViewStaysFiniteAndRollFreeForExtremeOrbit(float yaw)
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera();
        rig.SetOrbit(yaw, -0.9f);
        rig.SnapToTarget(camera, new Vector3(0, 0, -1));

        Assert.True(IsFinite(camera.Position));
        Assert.True(IsFinite(camera.Forward));
        Assert.True(IsFinite(camera.Right));
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
        Assert.Equal(1f, camera.Right.Length(), precision: 5);
        Assert.Equal(0f, camera.Right.Y, precision: 4);
    }

    [Fact]
    public void DegenerateCameraAtTargetFallsBackToOrbitBasis()
    {
        var camera = new CameraState(Vector3.Zero);
        var rig = new ThirdPersonCamera(distanceMeters: 0f, targetHeightOffsetMeters: 0f);
        rig.SetOrbit(0.7f, -0.2f);
        rig.SnapToTarget(camera, new Vector3(3f, 0f, -2f));

        var expected = ThirdPersonCamera.ForwardFromOrbit(rig.OrbitYawRadians, rig.OrbitPitchRadians);
        Assert.True(IsFinite(camera.Forward));
        Assert.Equal(expected.X, camera.Forward.X, precision: 5);
        Assert.Equal(expected.Y, camera.Forward.Y, precision: 5);
        Assert.Equal(expected.Z, camera.Forward.Z, precision: 5);
    }

    [Fact]
    public void CenteringHoldsWhileWalkingRunningAndOrbiting()
    {
        var session = SessionWithClips();
        var time = 10.0;
        var maxError = 0f;
        for (var i = 0; i < 180; i++)
        {
            var sprint = i >= 90;
            session.Update(
                new FrameTime(time, 0.016),
                MoveLook(forward: true, mouseDelta: new Vector2(sprint ? 20f : 8f, 3f), sprint: sprint));

            maxError = MathF.Max(maxError, LookAtError(session.Camera, session.CameraRig.TargetPoint(session.ErikaPosition)));
            time += 0.016;
        }

        Assert.True(maxError < 1e-5f, $"max look-at error {maxError} rad");
    }

    [Theory]
    [InlineData(1.0 / 30.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 144.0)]
    public void CenteringHoldsAcrossFrameRates(double dt)
    {
        var session = SessionWithClips();
        var time = 0.0;
        var maxError = 0f;
        for (var i = 0; i < 200; i++)
        {
            time += dt;
            session.Update(new FrameTime(time, dt), MoveLook(forward: true, mouseDelta: new Vector2(12f, 4f)));
            maxError = MathF.Max(maxError, LookAtError(session.Camera, session.CameraRig.TargetPoint(session.ErikaPosition)));
        }

        Assert.True(maxError < 1e-5f, $"max look-at error {maxError} rad at dt={dt}");
    }

    // --- control-basis independence -------------------------------------

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1.5707964f, 1f, 0f)]
    [InlineData(3.1415927f, 0f, 1f)]
    [InlineData(-1.5707964f, -1f, 0f)]
    [InlineData(0.7f, 0.64421767f, -0.7648422f)]
    public void ControlForwardIsDeterminedByOrbitYaw(float yaw, float expectedX, float expectedZ)
    {
        var rig = new ThirdPersonCamera();
        rig.SetOrbit(yaw, 0f);

        var forward = rig.ControlForward;
        Assert.Equal(expectedX, forward.X, precision: 4);
        Assert.Equal(0f, forward.Y, precision: 6);
        Assert.Equal(expectedZ, forward.Z, precision: 4);
        Assert.Equal(1f, forward.Length(), precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    [InlineData(3.1415927f)]
    [InlineData(-1.5707964f)]
    [InlineData(0.7f)]
    public void ControlRightIsOrthogonalAndNormalized(float yaw)
    {
        var rig = new ThirdPersonCamera();
        rig.SetOrbit(yaw, -0.4f);

        var forward = rig.ControlForward;
        var right = rig.ControlRight;
        Assert.Equal(1f, right.Length(), precision: 5);
        Assert.Equal(0f, right.Y, precision: 6);
        Assert.Equal(0f, Vector3.Dot(forward, right), precision: 5);
    }

    [Fact]
    public void FollowLagDoesNotChangeWASDIntent()
    {
        var session = SessionWithClips();
        session.CameraRig.SetOrbit(0.6f, 0f);
        session.CameraRig.SnapToTarget(session.Camera, session.ErikaPosition);
        var settled = session.ComputeMovementIntent(Move(forward: true, right: true));

        // Drive the camera into lateral follow lag (Erika moves sideways).
        session.Update(new FrameTime(1.0, 0.016), Move(right: true));
        var duringLag = session.ComputeMovementIntent(Move(forward: true, right: true));

        Assert.Equal(settled.X, duringLag.X, precision: 6);
        Assert.Equal(settled.Z, duringLag.Z, precision: 6);
    }

    [Theory]
    [InlineData(-1.2f)]
    [InlineData(-0.28f)]
    [InlineData(0.5f)]
    public void PitchDoesNotChangeHorizontalIntentOrAddVerticalMovement(float pitch)
    {
        var session = SessionWithClips();
        session.CameraRig.SetOrbit(0.9f, pitch);

        var intent = session.ComputeMovementIntent(Move(forward: true));
        var controlForward = session.CameraRig.ControlForward;

        Assert.Equal(controlForward.X, intent.X, precision: 5);
        Assert.Equal(controlForward.Z, intent.Z, precision: 5);
        Assert.Equal(0f, intent.Y, precision: 6);
    }

    // --- spawn consistency ----------------------------------------------

    [Fact]
    public void InitialErikaFacingMatchesInitialControlForward()
    {
        var session = SessionWithClips();

        var controlForward = session.CameraRig.ControlForward;
        var expectedYaw = MathF.Atan2(controlForward.X, controlForward.Z);
        Assert.Equal(expectedYaw, session.ErikaYawRadians, precision: 5);

        var facing = new Vector3(MathF.Sin(session.ErikaYawRadians), 0f, MathF.Cos(session.ErikaYawRadians));
        Assert.True(Vector3.Dot(facing, controlForward) > 0.9999f);
    }

    [Fact]
    public void InitialCameraIsBehindErikaAlongControlForward()
    {
        var session = SessionWithClips();

        var facing = new Vector3(MathF.Sin(session.ErikaYawRadians), 0f, MathF.Cos(session.ErikaYawRadians));
        var cameraOffset = session.Camera.Position - session.ErikaPosition;
        cameraOffset.Y = 0f;

        // Camera sits on the opposite side of Erika from the direction she faces.
        Assert.True(Vector3.Dot(Vector3.Normalize(cameraOffset), facing) < 0f);

        // And the rendered view looks along her facing (she is seen from behind).
        var horizontalForward = session.Camera.Forward with { Y = 0f };
        Assert.True(Vector3.Dot(Vector3.Normalize(horizontalForward), facing) > 0.999f);
    }

    [Fact]
    public void InitialWDoesNotRequireATurn()
    {
        var session = SessionWithClips();
        var spawnYaw = session.ErikaYawRadians;

        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));

        Assert.Equal(spawnYaw, session.ErikaYawRadians, precision: 6);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    [Fact]
    public void InitialWTravelsForwardAlongErikaFacing()
    {
        var session = SessionWithClips();
        var origin = session.ErikaPosition;
        var yaw = session.ErikaYawRadians;

        for (var i = 1; i <= 30; i++)
        {
            session.Update(new FrameTime(i * 0.016, 0.016), Move(forward: true));
        }

        var step = session.ErikaPosition - origin;
        var direction = Vector3.Normalize(new Vector3(step.X, 0f, step.Z));
        Assert.Equal(MathF.Sin(yaw), direction.X, precision: 3);
        Assert.Equal(MathF.Cos(yaw), direction.Z, precision: 3);
    }

    [Fact]
    public void StationaryStartupDoesNotMoveErika()
    {
        var session = SessionWithClips();
        var origin = session.ErikaPosition;
        var yaw = session.ErikaYawRadians;

        for (var i = 1; i <= 30; i++)
        {
            session.Update(new FrameTime(i * 0.1, 0.1), NoInput());
        }

        Assert.Equal(origin.X, session.ErikaPosition.X, precision: 6);
        Assert.Equal(origin.Z, session.ErikaPosition.Z, precision: 6);
        Assert.Equal(yaw, session.ErikaYawRadians, precision: 6);
    }

    // --- orbit / facing independence ------------------------------------

    [Fact]
    public void OrbitingWhileStationaryLeavesFacingAndPositionUnchanged()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), NoInput());
        var yaw = session.ErikaYawRadians;
        var position = session.ErikaPosition;

        for (var i = 0; i < 40; i++)
        {
            session.Update(new FrameTime(1.1 + i * 0.016, 0.016), Look(new Vector2(35f, 0f)));
        }

        Assert.Equal(yaw, session.ErikaYawRadians, precision: 6);
        Assert.Equal(position, session.ErikaPosition);
        // Camera kept looking at Erika throughout the orbit.
        Assert.True(LookAtError(session.Camera, session.CameraRig.TargetPoint(session.ErikaPosition)) < 1e-5f);
    }

    [Fact]
    public void OrbitNinetyThenWUsesNewControlDirectionAndTurnsSmoothly()
    {
        var session = SessionWithClips();
        session.CameraRig.SetOrbit(MathF.PI / 2f, 0f);
        session.CameraRig.SnapToTarget(session.Camera, session.ErikaPosition);
        var spawnYaw = session.ErikaYawRadians;

        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var yaw = session.ErikaYawRadians;

        // New control forward is +X (pi/2); Erika turns from -Z (pi) toward it.
        Assert.True(yaw < spawnYaw);
        Assert.True(yaw > MathF.PI / 2f);
        Assert.True(LookAtError(session.Camera, session.CameraRig.TargetPoint(session.ErikaPosition)) < 1e-5f);
    }

    // --- root-motion / crossfade regressions ----------------------------

    [Fact]
    public void CameraTargetingDoesNotAlterErikaPositionOrFacing()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var position = session.ErikaPosition;
        var yaw = session.ErikaYawRadians;

        for (var i = 0; i < 60; i++)
        {
            session.CameraRig.Follow(
                session.Camera,
                session.ErikaPosition + new Vector3(0.5f, 0f, 0.5f),
                new FrameTime(1.1 + i * 0.016, 0.016));
        }

        Assert.Equal(position, session.ErikaPosition);
        Assert.Equal(yaw, session.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void WalkAndRunDisplacementAreUnaffectedByTargeting()
    {
        static Vector3 Travel(bool sprint)
        {
            var session = SessionWithClips();
            var origin = session.ErikaPosition;
            session.Update(new FrameTime(0.0, 0.016), Move(forward: true, sprint: sprint));
            session.Update(new FrameTime(1.0, 1.0), Move(forward: true, sprint: sprint));
            return session.ErikaPosition - origin;
        }

        Assert.Equal(100f * ErikaFigure.Scale, Travel(sprint: false).Length(), precision: 3);
        Assert.Equal(200f * ErikaFigure.Scale, Travel(sprint: true).Length(), precision: 3);
    }

    [Fact]
    public void TargetTrackingDoesNotRestartOrAccumulateTransitions()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var clipStart = session.ClipStartSeconds;
        var active = session.ActiveClipName;

        for (var i = 0; i < 60; i++)
        {
            session.Update(
                new FrameTime(1.1 + i * 0.016, 0.016),
                MoveLook(forward: true, mouseDelta: new Vector2(25f, 5f)));
        }

        Assert.Equal(active, session.ActiveClipName);
        Assert.Equal(clipStart, session.ClipStartSeconds);
    }
}
