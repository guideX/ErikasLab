using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2M static third-person camera obstruction. Covers the portable
/// segment-vs-expanded-OBB math (miss/hit/rotated/parallel/boundary/inside/
/// ordering/finiteness), correspondence between the obstruction set and the real
/// Phase 2L longhouse geometry (walls, doorway, roof, trunks), camera behavior
/// (clear-space equivalence, immediate pull-in, smooth outward recovery,
/// doorway sight lines, corner orbit stability, control-basis independence,
/// target centering, starting-inside policy), gameplay A/B regression with
/// obstruction enabled vs disabled, and 30/60/144 Hz validation.
/// </summary>
public sealed class CameraObstructionTests
{
    private const float Radius = CameraCollisionPolicy.CollisionRadiusMeters;

    // --- helpers ---------------------------------------------------------

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static CameraObstructionBox Box(string name, Vector3 center, Vector3 halfExtents, Quaternion orientation) =>
        new(name, center, halfExtents, orientation);

    private static CameraObstructionSet Boxes(params CameraObstructionBox[] boxes) => new(boxes);

    private static CameraObstructionSet LonghouseObstructions() => EnvironmentFactory.CreateCameraObstructions();

    private static bool IsInsideAnyCollider(CameraObstructionSet set, Vector3 point)
    {
        foreach (var box in set.Boxes)
        {
            if (box.ContainsPoint(point, Radius))
            {
                return true;
            }
        }

        return false;
    }

    private static float LookAtError(CameraState camera, Vector3 target)
    {
        var toTarget = target - camera.Position;
        if (toTarget.LengthSquared() < 1e-12f)
        {
            return 0f;
        }

        var expected = Vector3.Normalize(toTarget);
        return MathF.Acos(Math.Clamp(Vector3.Dot(camera.Forward, expected), -1f, 1f));
    }

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

    private static InputState MoveLook(bool forward, Vector2 mouseDelta) => new(
        MoveForward: forward, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: mouseDelta,
        Sprint: false);

    private static InputState NoInput() => Move();

    // --- intersection math -----------------------------------------------

    [Fact]
    public void SegmentMissesBox()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, 2), out var result);

        Assert.False(hit);
        Assert.Equal(default, result);
    }

    [Fact]
    public void SegmentHitsBox()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -5), out var result);

        Assert.True(hit);
        Assert.Equal("B", result.Box.Name);
        // Expanded half depth 1 + 0.25 radius: enters at z = 1.25, t = 3.75/10.
        Assert.Equal(0.375f, result.Fraction, precision: 5);
        Assert.Equal(3.75f, result.Distance, precision: 4);
    }

    [Fact]
    public void NearestFaceHit()
    {
        var set = Boxes(Box("B", Vector3.Zero, new Vector3(1, 2, 3), Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(5, 5, 5), new Vector3(-5, -5, -5), out var result);

        Assert.True(hit);
        // Expanded halves (1.25, 2.25, 3.25): per-axis entries t_x = 0.375,
        // t_y = 0.275, t_z = 0.175; the segment enters the box at the LAST slab
        // crossed, so the nearest-face fraction is the max: 0.375.
        Assert.Equal(0.375f, result.Fraction, precision: 4);
    }

    [Fact]
    public void RotatedBoxHit()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        var set = Boxes(Box("R", Vector3.Zero, Vector3.One, rotation));

        var hit = set.CastSegment(new Vector3(5, 0, 0), new Vector3(-5, 0, 0), out var result);

        Assert.True(hit);
        Assert.InRange(result.Fraction, 0.30f, 0.35f);
        Assert.True(float.IsFinite(result.Distance));
    }

    [Fact]
    public void PitchedRoofLikeObbHit()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - 0.6f);
        var set = Boxes(Box("Roof", new Vector3(1.75f, 3.8f, 0), new Vector3(2.165f, 0.09f, 8.5f), rotation));

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(1, 6, 0), out var result);

        Assert.True(hit);
        Assert.Equal("Roof", result.Box.Name);
        Assert.InRange(result.Fraction, 0f, 1f);
    }

    [Fact]
    public void PitchedRoofLikeObbMiss()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - 0.6f);
        var set = Boxes(Box("Roof", new Vector3(1.75f, 3.8f, 0), new Vector3(2.165f, 0.09f, 8.5f), rotation));

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(0, 2, 0), out _);

        Assert.False(hit);
    }

    [Fact]
    public void ParallelSlabMiss()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(5, 5, 0), new Vector3(-5, 5, 0), out _);

        Assert.False(hit);
    }

    [Fact]
    public void ParallelSlabHit()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(0.5f, 0.5f, 5), new Vector3(0.5f, 0.5f, -5), out var result);

        Assert.True(hit);
        Assert.Equal(0.375f, result.Fraction, precision: 5);
    }

    [Fact]
    public void ZeroLengthSegmentIsSafe()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        Assert.False(set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, 5), out _));
        Assert.False(set.CastSegment(Vector3.Zero, Vector3.Zero, out _));
    }

    [Fact]
    public void ExactBoundaryContact()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        // Segment ends exactly on the expanded face z = 1.25: contact is a hit.
        var contact = set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, 1.25f), out var contactHit);
        Assert.True(contact);
        Assert.Equal(1f, contactHit.Fraction, precision: 5);

        // Target exactly on the expanded boundary counts as inside: box skipped.
        var boundaryTarget = set.CastSegment(new Vector3(0, 0, 1.25f), new Vector3(0, 0, 5), out _);
        Assert.False(boundaryTarget);
    }

    [Fact]
    public void CameraRadiusExpansion()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        // 1.3 is outside the radius-expanded face (1.25): miss.
        Assert.False(set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, 1.3f), out _));
        // 1.2 is inside the radius-expanded face: hit.
        Assert.True(set.CastSegment(new Vector3(0, 0, 5), new Vector3(0, 0, 1.2f), out _));
    }

    [Fact]
    public void CandidateInsideBlocker()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(0, 0, 5), Vector3.Zero, out var result);

        Assert.True(hit);
        // Segment length 5; enters the expanded box at z = 1.25: t = 3.75 / 5.
        Assert.Equal(0.75f, result.Fraction, precision: 5);
    }

    [Fact]
    public void TargetInsideBlockerPolicy()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        // Target at the box center: the box is ignored for this query.
        var hit = set.CastSegment(Vector3.Zero, new Vector3(0, 0, 5), out _);

        Assert.False(hit);
    }

    [Fact]
    public void MultipleBlockersChooseNearest()
    {
        var set = Boxes(
            Box("A", Vector3.Zero, Vector3.One, Quaternion.Identity),
            Box("B", new Vector3(0, 0, 3), Vector3.One, Quaternion.Identity));

        var hit = set.CastSegment(new Vector3(0, 0, 6), new Vector3(0, 0, -6), out var result);

        Assert.True(hit);
        Assert.Equal("B", result.Box.Name);
        // B's expanded near face is z = 4.25: t = (6 - 4.25) / 12.
        Assert.Equal(0.1458333f, result.Fraction, precision: 4);
    }

    [Fact]
    public void ColliderOrderDoesNotChangeResult()
    {
        var forward = Boxes(
            Box("A", Vector3.Zero, Vector3.One, Quaternion.Identity),
            Box("B", new Vector3(0, 0, 3), Vector3.One, Quaternion.Identity));
        var reversed = Boxes(
            Box("B", new Vector3(0, 0, 3), Vector3.One, Quaternion.Identity),
            Box("A", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var target = new Vector3(0, 0, 6);
        var candidate = new Vector3(0, 0, -6);
        Assert.True(forward.CastSegment(target, candidate, out var first));
        Assert.True(reversed.CastSegment(target, candidate, out var second));

        Assert.Equal(first.Fraction, second.Fraction, precision: 6);
        Assert.Equal(first.Box.Name, second.Box.Name);
    }

    [Fact]
    public void AllOutputsFinite()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f);
        var set = Boxes(Box("B", new Vector3(1, 2, 3), new Vector3(0.5f, 1, 2), rotation));

        var targets = new[]
        {
            Vector3.Zero,
            new Vector3(1, 2, 3),
            new Vector3(-4, 6, 2),
            new Vector3(float.NaN, 0, 0),
        };
        var candidates = new[]
        {
            new Vector3(4, -2, 5),
            new Vector3(1, 2, 3),
            new Vector3(0, 0, 0),
            new Vector3(2, 2, 2),
        };

        for (var i = 0; i < targets.Length; i++)
        {
            var hit = set.CastSegment(targets[i], candidates[i], out var result);
            Assert.True(!hit || float.IsFinite(result.Fraction));
            Assert.True(!hit || float.IsFinite(result.Distance));
            Assert.True(!hit || result.Fraction is >= 0f and <= 1f);
        }
    }

    // --- longhouse geometry ----------------------------------------------

    [Fact]
    public void ColliderCountIsExpected()
    {
        var set = LonghouseObstructions();

        // 6 wall boxes + 2 roof slabs + 24 tree trunks.
        Assert.Equal(32, set.Count);
    }

    [Fact]
    public void WallAndRoofCollidersMatchSceneGeometry()
    {
        var scene = EnvironmentFactory.Create();
        var set = LonghouseObstructions();
        var names = new[]
        {
            "Wall.Left", "Wall.Right", "Wall.Rear",
            "Wall.Front.Left", "Wall.Front.Right", "Wall.Front.Lintel",
            "Roof.Left", "Roof.Right",
        };

        foreach (var name in names)
        {
            var sceneObject = scene.Objects.Single(o => o.Name == name);
            var box = set.Boxes.Single(b => b.Name == name);

            Assert.Equal(sceneObject.Transform.Position, box.Center);
            Assert.Equal(sceneObject.Transform.Scale / 2f, box.HalfExtents);
            Assert.Equal(sceneObject.Transform.Rotation, box.Orientation);
        }
    }

    [Fact]
    public void TreeTrunkCollidersMatchSceneTrunks()
    {
        var scene = EnvironmentFactory.Create();
        var set = LonghouseObstructions();

        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var name = $"Tree.{i:00}.Trunk";
            var sceneObject = scene.Objects.Single(o => o.Name == name);
            var box = set.Boxes.Single(b => b.Name == name);

            Assert.Equal(sceneObject.Transform.Position, box.Center);
            Assert.Equal(sceneObject.Transform.Scale / 2f, box.HalfExtents);
            Assert.Equal(sceneObject.Transform.Rotation, box.Orientation);
        }
    }

    [Fact]
    public void ColliderTransformsAreFinite()
    {
        var set = LonghouseObstructions();

        foreach (var box in set.Boxes)
        {
            Assert.True(IsFinite(box.Center), $"{box.Name} center non-finite");
            Assert.True(IsFinite(box.HalfExtents), $"{box.Name} half extents non-finite");
            Assert.True(box.HalfExtents.X > 0 && box.HalfExtents.Y > 0 && box.HalfExtents.Z > 0, $"{box.Name} non-positive half extents");
            Assert.True(float.IsFinite(box.Orientation.X + box.Orientation.Y + box.Orientation.Z + box.Orientation.W), $"{box.Name} orientation non-finite");
            Assert.Equal(1f, box.Orientation.Length(), precision: 4);
        }
    }

    [Fact]
    public void ObstructionSetIsDeterministic()
    {
        var first = LonghouseObstructions();
        var second = LonghouseObstructions();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first.Boxes[i], second.Boxes[i]);
        }
    }

    [Fact]
    public void DoorwayRemainsOpen()
    {
        var set = LonghouseObstructions();

        // Through the door center, low enough for the camera sphere to fit
        // under the radius-expanded lintel: unobstructed.
        Assert.False(set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(0, 1.25f, 12), out _));
        // The 0.25 m camera radius means the camera center must stay below
        // y = 1.75 to pass the 2.0 m doorway: y = 1.9 hits the expanded lintel.
        Assert.True(set.CastSegment(new Vector3(0, 1.9f, 0), new Vector3(0, 1.9f, 12), out var lintel));
        Assert.Equal("Wall.Front.Lintel", lintel.Box.Name);
        // Well above the door the lintel blocks too.
        Assert.True(set.CastSegment(new Vector3(0, 2.45f, 0), new Vector3(0, 2.45f, 12), out _));
    }

    [Fact]
    public void CastThroughFlankingWallIsBlocked()
    {
        var set = LonghouseObstructions();

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(1.8f, 1.25f, 12), out var result);

        Assert.True(hit);
        Assert.Equal("Wall.Front.Right", result.Box.Name);
    }

    [Fact]
    public void RoofSlabCastIsBlocked()
    {
        var set = LonghouseObstructions();

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(1, 6, 0), out var result);

        Assert.True(hit);
        Assert.Equal("Roof.Right", result.Box.Name);
    }

    [Fact]
    public void CentralInteriorCameraRayBeneathRoofIsValid()
    {
        var set = LonghouseObstructions();

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(2, 2, -3), out _);

        Assert.False(hit);
    }

    [Fact]
    public void ExteriorWallCastIsBlocked()
    {
        var set = LonghouseObstructions();

        var hit = set.CastSegment(new Vector3(0, 1.25f, 0), new Vector3(8, 2.5f, 0), out var result);

        Assert.True(hit);
        Assert.Equal("Wall.Right", result.Box.Name);
        // Expanded near face x = 3 - 0.125 - 0.25 = 2.625: t = 2.625 / 8.
        Assert.Equal(0.328125f, result.Fraction, precision: 4);
    }

    // --- camera behavior -------------------------------------------------

    [Fact]
    public void ClearSpaceCameraMatchesUnobstructedRig()
    {
        var disabled = new ThirdPersonCamera();
        var enabled = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var disabledCamera = new CameraState(Vector3.Zero);
        var enabledCamera = new CameraState(Vector3.Zero);
        disabled.SnapToTarget(disabledCamera, LonghouseLayout.SpawnPosition);
        enabled.SnapToTarget(enabledCamera, LonghouseLayout.SpawnPosition);

        var target = new Vector3(5, 0, 12);
        for (var i = 1; i <= 60; i++)
        {
            var time = new FrameTime(i * 0.016, 0.016);
            disabled.Follow(disabledCamera, target, time);
            enabled.Follow(enabledCamera, target, time);

            if (i % 10 == 0)
            {
                Assert.Equal(disabledCamera.Position.X, enabledCamera.Position.X, precision: 5);
                Assert.Equal(disabledCamera.Position.Y, enabledCamera.Position.Y, precision: 5);
                Assert.Equal(disabledCamera.Position.Z, enabledCamera.Position.Z, precision: 5);
            }

            Assert.False(enabled.IsCameraObstructed);
        }

        Assert.Equal(4.5f, enabled.ActualTargetDistance, precision: 2);
        Assert.Equal(4.5f, disabled.ActualTargetDistance, precision: 2);
    }

    [Fact]
    public void EnteringObstructionPullsInwardImmediatelyAndNeverPenetrates()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        // Orbit yaw -pi/2 puts the desired camera outside the right long wall
        // while Erika stands at the interior center.
        rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);

        // The snap frame still pulls inward immediately (visibility clamp).
        Assert.True(rig.IsCameraObstructed);
        Assert.True(IsFinite(camera.Position));
        Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));
        Assert.True(camera.Position.X < 3.125f + Radius, "camera must stay inside the expanded wall");
        Assert.True(camera.Position.X > 0f, "camera must remain on Erika's visible side");

        // Phase 2Q: the camera body then slides along the wall instead of being
        // radially clamped every frame. The visibility clamp releases once the
        // slid position has a clear line of sight; the camera must never
        // penetrate and must keep a useful distance (no collapse toward Erika).
        for (var i = 1; i <= 60; i++)
        {
            rig.Follow(camera, target, new FrameTime(i * 0.016, 0.016));
            Assert.True(IsFinite(camera.Position));
            Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));
            Assert.True(camera.Position.X > 0f, "camera must remain on Erika's visible side");
            Assert.True(rig.ActualTargetDistance > 2.0f, $"camera collapsed to {rig.ActualTargetDistance:F3} m");
        }

        // The camera settles at the wall surface (skin offset), not at the
        // radial clamp distance: sliding retains more useful distance.
        Assert.True(camera.Position.X > 2.5f, $"camera slid to X={camera.Position.X:F3}, expected near the wall surface");
        Assert.False(rig.IsCameraObstructed);
    }

    [Fact]
    public void LeavingObstructionReturnsOutwardSmoothly()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);
        var clampedDistance = rig.ActualTargetDistance;
        Assert.True(rig.IsCameraObstructed);

        // Swing the orbit to a clear interior direction.
        rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.Follow(camera, target, new FrameTime(0.016, 0.016));

        // No instant pop to the nominal distance on the first frame.
        Assert.True(rig.ActualTargetDistance < ThirdPersonCamera.DefaultDistanceMeters);
        Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));

        for (var i = 2; i <= 60; i++)
        {
            rig.Follow(camera, target, new FrameTime(i * 0.016, 0.016));
            Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));
        }

        Assert.True(rig.ActualTargetDistance > 4.4f, $"expected near-nominal recovery, got {rig.ActualTargetDistance:F3}");
        Assert.True(rig.ActualTargetDistance < ThirdPersonCamera.DefaultDistanceMeters);
        Assert.False(rig.IsCameraObstructed);
        Assert.True(clampedDistance < 4.5f);
    }

    [Fact]
    public void DoorwayAllowsFartherCamera()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);

        // Erika just inside the doorway, camera directly behind her (outside,
        // looking in through the open door): full nominal distance, no obstruction.
        rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, new Vector3(0, 0, 7));

        Assert.False(rig.IsCameraObstructed);
        Assert.Equal(4.5f, rig.ActualTargetDistance, precision: 3);

        // Off-center: the same orbit crosses the flanking wall segment.
        var offCenter = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var offCamera = new CameraState(Vector3.Zero);
        offCenter.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        offCenter.SnapToTarget(offCamera, new Vector3(1.5f, 0, 7));

        Assert.True(offCenter.IsCameraObstructed);
        Assert.True(offCenter.ActualTargetDistance < 4.5f);
    }

    [Fact]
    public void OrbitAroundInteriorCornerRemainsFinite()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = new Vector3(2.5f, 0, 7.5f);

        rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);

        var previous = camera.Position;
        var maxStep = 0f;
        for (var i = 1; i <= 72; i++)
        {
            rig.SetOrbit(i * MathF.Tau / 72, ThirdPersonCamera.DefaultOrbitPitchRadians);
            rig.Follow(camera, target, new FrameTime(i * 0.016, 0.016));

            Assert.True(IsFinite(camera.Position));
            Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));
            maxStep = MathF.Max(maxStep, Vector3.Distance(camera.Position, previous));
            previous = camera.Position;
        }

        // Hard clamping can pop when the lagging camera's line of sight suddenly
        // crosses a wall; the pop is bounded by the nominal camera distance
        // (corner sliding is a later phase).
        Assert.True(maxStep < 3.0f, $"corner transition step {maxStep:F3} m exceeded bound");
    }

    [Fact]
    public void RapidYawChangesRemainFinite()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);

        for (var i = 1; i <= 100; i++)
        {
            rig.SetOrbit(i % 2 == 0 ? 0f : MathF.PI, ThirdPersonCamera.DefaultOrbitPitchRadians);
            rig.Follow(camera, target, new FrameTime(i * 0.016, 0.016));

            Assert.True(IsFinite(camera.Position));
            Assert.True(camera.Position.Y >= CameraCollisionPolicy.CameraFloorHeightMeters - 1e-4f);
        }
    }

    [Fact]
    public void PitchExtremesRemainFinite()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        rig.SetOrbit(0f, ThirdPersonCamera.MinPitchRadians);
        rig.SnapToTarget(camera, target);
        Assert.True(IsFinite(camera.Position));
        Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));

        rig.SetOrbit(0f, ThirdPersonCamera.MaxPitchRadians);
        rig.SnapToTarget(camera, target);
        Assert.True(IsFinite(camera.Position));
        Assert.True(camera.Position.Y >= CameraCollisionPolicy.CameraFloorHeightMeters - 1e-4f);
    }

    [Fact]
    public void HighOrbitInsideIsPulledBelowRoof()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        // Pitch -1.2 wants the camera ~4.2 m above the target: above the 5.0 m
        // ridge, so the roof slabs must pull it back inside.
        rig.SetOrbit(0f, ThirdPersonCamera.MinPitchRadians);
        rig.SnapToTarget(camera, target);

        Assert.True(rig.IsCameraObstructed);
        Assert.True(camera.Position.Y < 4.9f, $"camera Y {camera.Position.Y:F2} must stay below the ridge");
        Assert.False(IsInsideAnyCollider(rig.Obstructions!.Value, camera.Position));
    }

    [Fact]
    public void PositivePitchAppliesFloorConstraint()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);

        rig.SetOrbit(0f, ThirdPersonCamera.MaxPitchRadians);
        rig.SnapToTarget(camera, LonghouseLayout.SpawnPosition);

        var desiredY = rig.DesiredPosition(LonghouseLayout.SpawnPosition).Y;
        Assert.True(desiredY < 0f, "test premise: positive pitch wants a below-ground camera");
        Assert.True(camera.Position.Y >= CameraCollisionPolicy.CameraFloorHeightMeters - 1e-4f);
    }

    [Fact]
    public void CollisionDoesNotChangeControlBasis()
    {
        var obstructed = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var clear = new ThirdPersonCamera();
        var camera = new CameraState(Vector3.Zero);

        obstructed.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        obstructed.SnapToTarget(camera, Vector3.Zero);
        Assert.True(obstructed.IsCameraObstructed);

        clear.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);

        Assert.Equal(clear.ControlForward, obstructed.ControlForward);
        Assert.Equal(clear.ControlRight, obstructed.ControlRight);

        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.CameraRig.Obstructions = null;
        enabled.CameraRig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        disabled.CameraRig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);

        var enabledIntent = enabled.ComputeMovementIntent(Move(forward: true));
        var disabledIntent = disabled.ComputeMovementIntent(Move(forward: true));

        Assert.Equal(disabledIntent, enabledIntent);
    }

    [Fact]
    public void CameraLooksAtErikaAfterCollision()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = Vector3.Zero;

        rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);
        Assert.True(rig.IsCameraObstructed);

        var lookTarget = rig.TargetPoint(target);
        Assert.True(LookAtError(camera, lookTarget) < 1e-5f);
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
    }

    [Fact]
    public void TargetInsideObstructionKeepsCameraFinite()
    {
        // No player collision exists: Erika may stand inside a wall. The wall
        // containing the look target is ignored, so the camera must not
        // collapse to zero distance or emit NaNs.
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = new Vector3(3, 0, 0);

        rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);

        Assert.True(IsFinite(camera.Position));
        Assert.False(rig.IsCameraObstructed);
        Assert.Equal(4.5f, rig.ActualTargetDistance, precision: 3);
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
    }

    [Fact]
    public void ExtremelyCloseCameraUsesNumericalMinimum()
    {
        var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var camera = new CameraState(Vector3.Zero);
        var target = new Vector3(2.55f, 0, 0);

        rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, target);

        Assert.True(rig.IsCameraObstructed);
        Assert.True(IsFinite(camera.Position));
        Assert.Equal(CameraCollisionPolicy.MinCameraDistanceMeters, rig.ActualTargetDistance, precision: 3);
        Assert.Equal(1f, camera.Forward.Length(), precision: 5);
    }

    // --- gameplay A/B regression -----------------------------------------

    private static (GameSession Enabled, GameSession Disabled) SessionPair()
    {
        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.CameraRig.Obstructions = null;
        return (enabled, disabled);
    }

    private static void AssertGameplayIdentical(GameSession enabled, GameSession disabled)
    {
        Assert.Equal(disabled.ErikaPosition.X, enabled.ErikaPosition.X, precision: 4);
        Assert.Equal(disabled.ErikaPosition.Z, enabled.ErikaPosition.Z, precision: 4);
        Assert.Equal(disabled.ErikaYawRadians, enabled.ErikaYawRadians, precision: 5);
        Assert.Equal(disabled.CurrentMoveSpeedMetersPerSecond, enabled.CurrentMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(disabled.TargetMoveSpeedMetersPerSecond, enabled.TargetMoveSpeedMetersPerSecond, precision: 4);
        Assert.Equal(disabled.ActiveClipName, enabled.ActiveClipName);
        Assert.Equal(disabled.IsTurningInPlace, enabled.IsTurningInPlace);
    }

    private static void RunIdentical(
        GameSession enabled,
        GameSession disabled,
        int frames,
        Func<int, InputState> input)
    {
        for (var i = 1; i <= frames; i++)
        {
            var time = new FrameTime(i * 0.016, 0.016);
            var state = input(i);
            enabled.Update(time, state);
            disabled.Update(time, state);
            AssertGameplayIdentical(enabled, disabled);
        }
    }

    [Fact]
    public void WalkIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true));
    }

    [Fact]
    public void RunIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true, sprint: true));
    }

    [Fact]
    public void WalkToRunIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 30, _ => Move(forward: true));
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true, sprint: true));
    }

    [Fact]
    public void RunToWalkIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 30, _ => Move(forward: true, sprint: true));
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true));
    }

    [Fact]
    public void StopIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 30, _ => Move(forward: true));
        RunIdentical(enabled, disabled, 30, _ => NoInput());
    }

    [Fact]
    public void StationaryTurnIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 1, _ => NoInput());
        RunIdentical(enabled, disabled, 60, _ => Move(right: true));
    }

    [Fact]
    public void OrbitWhileMovingIsIdenticalWithAndWithoutObstruction()
    {
        var (enabled, disabled) = SessionPair();

        // Walk straight through the doorway to the interior first.
        RunIdentical(enabled, disabled, 300, _ => Move(forward: true));
        Assert.True(enabled.ErikaPosition.Z < LonghouseLayout.FrontZ, "test premise: Erika must reach the interior");

        // Then orbit the camera while continuing to walk: the enabled rig must
        // actually obstruct, and gameplay must stay identical.
        var triggered = false;
        for (var i = 301; i <= 400; i++)
        {
            var time = new FrameTime(i * 0.016, 0.016);
            var state = MoveLook(forward: true, new Vector2(4, 0));
            enabled.Update(time, state);
            disabled.Update(time, state);
            AssertGameplayIdentical(enabled, disabled);
            triggered |= enabled.CameraRig.IsCameraObstructed;
        }

        Assert.True(triggered, "test premise: orbiting while walking inside the longhouse must obstruct the enabled camera");
    }

    // --- frame-rate validation --------------------------------------------

    [Fact]
    public void ObstructionResponseIsFrameRateIndependent()
    {
        var results = new (int Fps, Vector3 Position, float Distance, bool Obstructed)[3];
        var r = 0;
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1.0 / fps;
            var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
            var camera = new CameraState(Vector3.Zero);
            rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
            rig.SnapToTarget(camera, Vector3.Zero);
            for (var i = 1; i <= fps; i++)
            {
                rig.Follow(camera, Vector3.Zero, new FrameTime(i * dt, dt));
            }

            results[r++] = (fps, camera.Position, rig.ActualTargetDistance, rig.IsCameraObstructed);
        }

        for (var i = 1; i < results.Length; i++)
        {
            Assert.Equal(results[0].Position.X, results[i].Position.X, precision: 5);
            Assert.Equal(results[0].Position.Y, results[i].Position.Y, precision: 5);
            Assert.Equal(results[0].Position.Z, results[i].Position.Z, precision: 5);
            Assert.Equal(results[0].Distance, results[i].Distance, precision: 4);
            Assert.Equal(results[0].Obstructed, results[i].Obstructed);
        }

        // Phase 2Q: the camera slides to the wall surface at every frame rate;
        // the visibility clamp releases once the slid line of sight is clear.
        Assert.All(results, result =>
        {
            Assert.False(result.Obstructed);
            Assert.True(result.Distance > 2.5f);
        });
    }

    [Fact]
    public void OutwardRecoveryIsFrameRateIndependent()
    {
        var results = new (int Fps, float Distance, double SecondsToNearNominal)[3];
        var r = 0;
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1.0 / fps;
            var rig = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
            var camera = new CameraState(Vector3.Zero);
            rig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
            rig.SnapToTarget(camera, Vector3.Zero);
            for (var i = 1; i <= 10; i++)
            {
                rig.Follow(camera, Vector3.Zero, new FrameTime(i * dt, dt));
            }

            rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
            var orbitChangeTime = 10 * dt;
            var secondsToNearNominal = 0.0;
            for (var i = 11; i <= fps + 10; i++)
            {
                rig.Follow(camera, Vector3.Zero, new FrameTime(i * dt, dt));
                if (secondsToNearNominal == 0.0 && rig.ActualTargetDistance >= 4.05f)
                {
                    secondsToNearNominal = i * dt - orbitChangeTime;
                }
            }

            results[r++] = (fps, rig.ActualTargetDistance, secondsToNearNominal);
        }

        for (var i = 1; i < results.Length; i++)
        {
            Assert.Equal(results[0].Distance, results[i].Distance, precision: 3);
            Assert.True(Math.Abs(results[0].SecondsToNearNominal - results[i].SecondsToNearNominal) < 0.05);
        }

        Assert.All(results, result =>
        {
            Assert.True(result.Distance > 4.4f, $"recovery distance {result.Distance:F3} too low");
            Assert.True(result.SecondsToNearNominal < 0.25, $"recovery took {result.SecondsToNearNominal:F3} s");
        });
    }

    [Fact]
    public void PlayerStateIsFrameRateIndependentWithObstruction()
    {
        var positions = new (int Fps, double X, double Z)[3];
        var r = 0;
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1.0 / fps;
            var session = SessionWithClips();
            for (var i = 1; i <= fps; i++)
            {
                session.Update(new FrameTime(i * dt, dt), Move(forward: true));
            }

            positions[r++] = (fps, session.ErikaPosition.X, session.ErikaPosition.Z);
        }

        for (var i = 1; i < positions.Length; i++)
        {
            Assert.True(Math.Abs(positions[0].X - positions[i].X) < 0.01, $"X drift {Math.Abs(positions[0].X - positions[i].X):F4} m");
            Assert.True(Math.Abs(positions[0].Z - positions[i].Z) < 0.01, $"Z drift {Math.Abs(positions[0].Z - positions[i].Z):F4} m");
        }
    }
}
