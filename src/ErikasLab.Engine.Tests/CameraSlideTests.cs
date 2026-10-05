using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2Q camera-body sphere sweep/slide. Covers the portable sweep math
/// (miss/head-on/diagonal/rotated/corner/high-speed/boundary/zero/ordering/
/// finiteness/iteration bound/starting overlap), scripted longhouse corner
/// orbits (exterior corners, interior long-wall corners, doorway jamb,
/// wall-to-roof, tree trunk) with the mandatory no-penestration + clear-sight-
/// line assertions, orbit continuity (step bound vs the Phase 2M legacy rig,
/// no oscillation), distance retention, control-basis independence, gameplay
/// A/B with the body-slide seam, blocked-movement regression while orbiting,
/// and 30/60/144 Hz validation.
/// </summary>
public sealed class CameraSlideTests
{
    private const float Radius = CameraCollisionPolicy.CollisionRadiusMeters;
    private const float Skin = CameraCollisionPolicy.CameraSlideSkinMeters;

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

    private static bool HasTwoPositionOscillation(System.Collections.Generic.List<Vector3> positions)
    {
        for (var i = 0; i + 3 < positions.Count; i++)
        {
            var returnsToFirst = Vector3.Distance(positions[i], positions[i + 2]) < 1e-4f;
            var returnsToSecond = Vector3.Distance(positions[i + 1], positions[i + 3]) < 1e-4f;
            var distinctPair = Vector3.Distance(positions[i], positions[i + 1]) > 1e-3f;
            if (returnsToFirst && returnsToSecond && distinctPair)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record OrbitSummary(
        float MaxStep,
        Vector3 FinalPosition,
        float MinDistance,
        float MaxDistance,
        System.Collections.Generic.List<Vector3> Positions);

    private static OrbitSummary RunOrbit(
        CameraObstructionSet set,
        Vector3 targetWorld,
        int steps,
        double dt,
        bool slideEnabled,
        float pitch = -0.28f)
    {
        var rig = new ThirdPersonCamera { Obstructions = set, CameraBodySlideEnabled = slideEnabled };
        var camera = new CameraState(Vector3.Zero);
        rig.SetOrbit(0f, pitch);
        rig.SnapToTarget(camera, targetWorld);

        var positions = new System.Collections.Generic.List<Vector3> { camera.Position };
        var previous = camera.Position;
        var maxStep = 0f;
        var minDistance = float.MaxValue;
        var maxDistance = 0f;
        for (var i = 1; i <= steps; i++)
        {
            rig.SetOrbit(i * MathF.Tau / steps, pitch);
            rig.Follow(camera, targetWorld, new FrameTime(i * dt, dt));
            maxStep = MathF.Max(maxStep, Vector3.Distance(camera.Position, previous));
            minDistance = MathF.Min(minDistance, rig.ActualTargetDistance);
            maxDistance = MathF.Max(maxDistance, rig.ActualTargetDistance);
            previous = camera.Position;
            positions.Add(camera.Position);
        }

        return new OrbitSummary(maxStep, camera.Position, minDistance, maxDistance, positions);
    }

    private static void AssertCornerOrbitContract(
        CameraObstructionSet set,
        Vector3 targetWorld,
        int steps,
        float maxStepBound)
    {
        var rig = new ThirdPersonCamera { Obstructions = set };
        var camera = new CameraState(Vector3.Zero);
        var lookTarget = rig.TargetPoint(targetWorld);
        rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
        rig.SnapToTarget(camera, targetWorld);

        var previous = camera.Position;
        var maxStep = 0f;
        for (var i = 1; i <= steps; i++)
        {
            rig.SetOrbit(i * MathF.Tau / steps, ThirdPersonCamera.DefaultOrbitPitchRadians);
            rig.Follow(camera, targetWorld, new FrameTime(i * (1.0 / 60.0), 1.0 / 60.0));

            Assert.True(IsFinite(camera.Position), $"frame {i}: non-finite camera position");
            Assert.False(IsInsideAnyCollider(set, camera.Position), $"frame {i}: camera penetrated an obstruction");
            Assert.False(set.CastSegment(lookTarget, camera.Position, out _), $"frame {i}: target sight line obstructed after final clamp");
            Assert.True(camera.Position.Y >= CameraCollisionPolicy.CameraFloorHeightMeters - 1e-4f, $"frame {i}: camera below floor");

            maxStep = MathF.Max(maxStep, Vector3.Distance(camera.Position, previous));
            previous = camera.Position;
        }

        Assert.True(maxStep < maxStepBound, $"corner transition step {maxStep:F3} m exceeded bound {maxStepBound:F3} m");
    }

    // --- sweep/slide math -------------------------------------------------

    [Fact]
    public void SweepMissesObb()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        Assert.False(set.Sweep(new Vector3(0, 0, 5), new Vector3(0, 0, 1), Radius, out _), "sweeping away from the box");
        Assert.False(set.Sweep(new Vector3(5, 5, 5), new Vector3(1, 1, 1), Radius, out _), "sweeping parallel outside the box");
        Assert.False(set.Sweep(new Vector3(5, 5, 5), Vector3.Zero, Radius, out _), "zero displacement from free space");
        // Zero displacement from inside the expanded box reports the starting overlap.
        Assert.True(set.Sweep(Vector3.Zero, Vector3.Zero, Radius, out var overlap));
        Assert.Equal(0f, overlap.Fraction, precision: 5);
    }

    [Fact]
    public void SweepHeadOnHitReportsFractionAndNormal()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var hit = set.Sweep(new Vector3(0, 0, 5), new Vector3(0, 0, -10), Radius, out var result);

        Assert.True(hit);
        Assert.Equal("B", result.Box.Name);
        // Expanded half depth 1.25: enters at z = 1.25, t = 3.75 / 10.
        Assert.Equal(0.375f, result.Fraction, precision: 5);
        Assert.Equal(3.75f, result.Distance, precision: 4);
        Assert.True(Vector3.DistanceSquared(result.Normal, Vector3.UnitZ) < 1e-10f, $"normal {result.Normal} is not +Z");
    }

    [Fact]
    public void ResolverHeadOnHitStopsAtSurfaceWithSkin()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(0, 0, 5), new Vector3(0, 0, -10), Radius);

        Assert.True(motion.Constrained);
        Assert.Equal(1, motion.HitCount);
        Assert.Equal("B", motion.LastHitName);
        // Stops at the expanded face (z = 1.25) plus the 0.01 m skin.
        Assert.Equal(1.25f + Skin, motion.Position.Z, precision: 4);
        Assert.Equal(0f, motion.Position.X, precision: 5);
        Assert.True(IsFinite(motion.Delta));
    }

    [Fact]
    public void ResolverDiagonalHitPreservesTangent()
    {
        // A wall thin in X; a diagonal sweep must keep the tangential (Y) motion
        // and lose only the inward (X) component.
        var set = Boxes(Box("W", Vector3.Zero, new Vector3(1, 5, 5), Quaternion.Identity));

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(5, -5, 0), new Vector3(-10, 10, 0), Radius);

        Assert.True(motion.Constrained);
        Assert.Equal(1, motion.HitCount);
        // Slid along the wall: X stopped at the expanded face + skin, Y kept its motion.
        Assert.Equal(1.25f + Skin, motion.Position.X, precision: 4);
        Assert.Equal(5f, motion.Position.Y, precision: 3);
        Assert.False(IsInsideAnyCollider(set, motion.Position));
    }

    [Fact]
    public void ResolverRotatedObbSlide()
    {
        // A plate thin in Z, rotated 45 degrees about Y: the contact normal must
        // be the rotated face normal, and a diagonal sweep must slide along it.
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        var set = Boxes(Box("R", Vector3.Zero, new Vector3(2, 2, 0.5f), rotation));

        var hit = set.Sweep(new Vector3(0, 0, 5), new Vector3(0, 0, -10), Radius, out var result);

        Assert.True(hit);
        Assert.Equal(MathF.Sqrt(0.5f), result.Normal.X, precision: 4);
        Assert.Equal(0f, result.Normal.Y, precision: 5);
        Assert.Equal(MathF.Sqrt(0.5f), result.Normal.Z, precision: 4);

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(3, 0, 5), new Vector3(-6, 0, -10), Radius);

        Assert.True(motion.Constrained);
        Assert.True(motion.HitCount >= 1);
        Assert.True(IsFinite(motion.Position));
        Assert.False(IsInsideAnyCollider(set, motion.Position));
        // The camera stayed on the +normal side of the tilted plate surface.
        var distanceToPlane = Vector3.Dot(motion.Position, result.Normal);
        Assert.True(distanceToPlane > 0.7f, $"camera crossed the plate plane, distance {distanceToPlane:F3}");
        // Tangential progress preserved: the camera made progress toward -X.
        Assert.True(motion.Position.X < 3f, $"expected tangential slide, X={motion.Position.X:F3}");
    }

    [Fact]
    public void ResolverTwoSurfaceCorner()
    {
        // Convex outside corner: two thin walls meeting at (-1, 1), like the
        // longhouse exterior corner. A diagonal sweep into the corner must
        // slide along one face and then the other, progressing toward the
        // corner instead of stopping dead.
        var set = Boxes(
            Box("A", new Vector3(-1, 0, 0), new Vector3(0.125f, 5, 5), Quaternion.Identity),
            Box("B", new Vector3(0, 0, 1), new Vector3(5, 5, 0.125f), Quaternion.Identity));

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(-5, 0, 5), new Vector3(10, 0, -10), Radius);

        Assert.True(motion.Constrained);
        Assert.True(motion.HitCount >= 2, $"expected two surface contacts, got {motion.HitCount}");
        Assert.True(IsFinite(motion.Position));
        Assert.False(IsInsideAnyCollider(set, motion.Position));
        // Rests at the corner (skin offset outside both expanded walls).
        Assert.Equal(-1.375f - Skin, motion.Position.X, precision: 3);
        Assert.Equal(1.375f + Skin, motion.Position.Z, precision: 3);
    }

    [Fact]
    public void ResolverConcaveCornerCollapses()
    {
        // Concave inside corner at (0, 0): the remaining displacement naturally
        // collapses to zero at the corner. Bounded and outside both boxes.
        var set = Boxes(
            Box("A", new Vector3(1, 0, 0), new Vector3(1, 5, 1), Quaternion.Identity),
            Box("B", new Vector3(0, 0, 1), new Vector3(5, 1, 1), Quaternion.Identity));

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(-5, 0, -5), new Vector3(10, 0, 10), Radius);

        Assert.True(motion.Constrained);
        Assert.True(IsFinite(motion.Position));
        Assert.False(IsInsideAnyCollider(set, motion.Position));
        // Both inward components removed: the camera rests at the expanded
        // corner faces (skin offset), displacement collapsed to zero.
        Assert.Equal(-0.25f - Skin, motion.Position.X, precision: 3);
        Assert.Equal(-0.25f - Skin, motion.Position.Z, precision: 3);
    }

    [Fact]
    public void ResolverHighSpeedSweepDoesNotTunnel()
    {
        // 40 m in one step through a thin wall: the slab test must still catch it.
        var set = Boxes(Box("W", Vector3.Zero, new Vector3(5, 5, 0.1f), Quaternion.Identity));

        var motion = CameraCollisionResolver.Resolve(set, new Vector3(0, 0, 20), new Vector3(0, 0, -40), Radius);

        Assert.True(motion.Constrained);
        Assert.Equal(1, motion.HitCount);
        // Expanded face at z = 0.35, plus skin: no tunnel to the far side.
        Assert.Equal(0.35f + Skin, motion.Position.Z, precision: 4);
    }

    [Fact]
    public void SweepExactBoundaryIsHit()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        // Ends exactly on the expanded face z = 1.25: contact is a hit at t = 1.
        var contact = set.Sweep(new Vector3(0, 0, 5), new Vector3(0, 0, -3.75f), Radius, out var contactHit);
        Assert.True(contact);
        Assert.Equal(1f, contactHit.Fraction, precision: 5);

        // One millimeter further out: a miss.
        Assert.False(set.Sweep(new Vector3(0, 0, 5), new Vector3(0, 0, -3.74f), Radius, out _));
    }

    [Fact]
    public void ResolverZeroDisplacementIsNoOp()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));
        var start = new Vector3(5, 5, 5);

        var motion = CameraCollisionResolver.Resolve(set, start, Vector3.Zero, Radius);

        Assert.Equal(start, motion.Position);
        Assert.Equal(Vector3.Zero, motion.Delta);
        Assert.False(motion.Constrained);
        Assert.Equal(0, motion.HitCount);
        Assert.Null(motion.LastHitName);
    }

    [Fact]
    public void SweepBlockerOrderingIsNearestAndOrderIndependent()
    {
        var forward = Boxes(
            Box("A", Vector3.Zero, Vector3.One, Quaternion.Identity),
            Box("B", new Vector3(0, 0, 3), Vector3.One, Quaternion.Identity));
        var reversed = Boxes(
            Box("B", new Vector3(0, 0, 3), Vector3.One, Quaternion.Identity),
            Box("A", Vector3.Zero, Vector3.One, Quaternion.Identity));

        var start = new Vector3(0, 0, 6);
        var delta = new Vector3(0, 0, -12);
        var first = forward.Sweep(start, delta, Radius, out var firstHit);
        var second = reversed.Sweep(start, delta, Radius, out var secondHit);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal("B", firstHit.Box.Name);
        Assert.Equal(firstHit.Fraction, secondHit.Fraction, precision: 6);
        Assert.Equal(firstHit.Normal, secondHit.Normal);
    }

    [Fact]
    public void ResolverOutputsFinite()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f);
        var set = Boxes(Box("B", new Vector3(1, 2, 3), new Vector3(0.5f, 1, 2), rotation));

        var starts = new[]
        {
            Vector3.Zero,
            new Vector3(1, 2, 3),
            new Vector3(-4, 6, 2),
            new Vector3(float.NaN, 0, 0),
        };
        var deltas = new[]
        {
            new Vector3(4, -2, 5),
            new Vector3(1, 2, 3),
            new Vector3(0, 0, 0),
            new Vector3(2, 2, 2),
        };

        for (var i = 0; i < starts.Length; i++)
        {
            var motion = CameraCollisionResolver.Resolve(set, starts[i], deltas[i], Radius);
            Assert.True(IsFinite(motion.Delta), $"delta non-finite for case {i}");
            if (IsFinite(starts[i]) && IsFinite(deltas[i]))
            {
                Assert.True(IsFinite(motion.Position), $"position non-finite for case {i}");
            }
            else
            {
                // Non-finite input: no invented motion, no constraint reported.
                Assert.Equal(Vector3.Zero, motion.Delta);
                Assert.False(motion.Constrained);
            }
        }
    }

    [Fact]
    public void ResolverHitCountBoundedByIterationLimit()
    {
        // Two-surface corner needs two iterations; a limit of one must cap it.
        var set = Boxes(
            Box("A", new Vector3(-1, 0, 0), new Vector3(1, 5, 1), Quaternion.Identity),
            Box("B", new Vector3(0, 0, 1), new Vector3(5, 1, 1), Quaternion.Identity));
        var start = new Vector3(-5, 0, 5);
        var delta = new Vector3(10, 0, -10);

        var capped = CameraCollisionResolver.Resolve(set, start, delta, Radius, maxSlideIterations: 1);
        Assert.True(capped.Constrained);
        Assert.True(capped.HitCount <= 1, $"hit count {capped.HitCount} exceeded the iteration limit");
        Assert.True(IsFinite(capped.Position));

        var zero = CameraCollisionResolver.Resolve(set, start, delta, Radius, maxSlideIterations: 0);
        Assert.Equal(0, zero.HitCount);
        Assert.True(IsFinite(zero.Position));
    }

    [Fact]
    public void ResolverStartingOverlapDepenetratesBounded()
    {
        var set = Boxes(Box("B", Vector3.Zero, Vector3.One, Quaternion.Identity));

        // Center inside the expanded box: pushed out along the least-penetration
        // axis to the expanded face (no skin on depenetration). The sphere
        // surface then touches the box surface exactly: not penetrating.
        var motion = CameraCollisionResolver.Resolve(set, new Vector3(0.5f, 0, 0), Vector3.Zero, Radius);
        Assert.True(motion.Constrained);
        Assert.Equal(1.25f, motion.Position.X, precision: 4);
        Assert.True(motion.Position.X >= 1.25f - 1e-4f, "camera must reach the expanded face");

        // Deep overlap inside a huge box: the correction is capped at 1.0 m.
        var huge = Boxes(Box("H", Vector3.Zero, new Vector3(10, 10, 10), Quaternion.Identity));
        var deep = CameraCollisionResolver.Resolve(huge, new Vector3(5, 0, 0), Vector3.Zero, Radius);
        Assert.True(deep.Constrained);
        Assert.Equal(6f, deep.Position.X, precision: 4);
    }

    // --- longhouse corners ------------------------------------------------

    [Fact]
    public void ExteriorFrontLeftCornerOrbit()
    {
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(-4.6f, 0, 9.6f), 72, maxStepBound: 1.0f);
    }

    [Fact]
    public void ExteriorFrontRightCornerOrbit()
    {
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(4.6f, 0, 9.6f), 72, maxStepBound: 1.0f);
    }

    [Fact]
    public void ExteriorRearCornerOrbit()
    {
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(4.6f, 0, -9.6f), 72, maxStepBound: 1.0f);
    }

    [Fact]
    public void InteriorLongWallCornerOrbit()
    {
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(2.5f, 0, 7.5f), 72, maxStepBound: 1.0f);
        AssertCornerOrbitContract(set, new Vector3(-2.2f, 0, 6.5f), 72, maxStepBound: 1.0f);
    }

    [Fact]
    public void DoorwayJambTransitionOrbit()
    {
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(0.9f, 0, 6.8f), 72, maxStepBound: 1.0f);
    }

    [Fact]
    public void WallToRoofTransitionOrbit()
    {
        // High camera near the right long wall: the constraint transitions from
        // the wall to the roof slab as the orbit carries the camera upward.
        var set = LonghouseObstructions();
        AssertCornerOrbitContract(set, new Vector3(2f, 0, 0f), 72, maxStepBound: 1.5f);
    }

    [Fact]
    public void TreeTrunkOrbit()
    {
        var set = LonghouseObstructions();
        // Tree 0 trunk sits near (27.6, 0, 0); orbit a target 3 m from it.
        AssertCornerOrbitContract(set, new Vector3(24.5f, 0, 0f), 72, maxStepBound: 1.5f);
    }

    // --- orbit continuity -------------------------------------------------

    [Fact]
    public void OutsideCornerOrbitContinuity()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(4.6f, 0, 9.6f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        Assert.True(slide.MaxStep < legacy.MaxStep,
            $"slide max step {slide.MaxStep:F3} not below legacy {legacy.MaxStep:F3}");
        Assert.False(HasTwoPositionOscillation(slide.Positions), "two-position oscillation detected");
        Assert.All(slide.Positions, position => Assert.True(IsFinite(position)));
    }

    [Fact]
    public void InsideCornerOrbitContinuity()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(2.5f, 0, 7.5f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        Assert.True(slide.MaxStep < legacy.MaxStep,
            $"slide max step {slide.MaxStep:F3} not below legacy {legacy.MaxStep:F3}");
        Assert.False(HasTwoPositionOscillation(slide.Positions), "two-position oscillation detected");
        Assert.All(slide.Positions, position => Assert.True(IsFinite(position)));
    }

    [Fact]
    public void DoorwayOrbitContinuity()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(0.9f, 0, 6.8f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        Assert.True(slide.MaxStep < legacy.MaxStep,
            $"slide max step {slide.MaxStep:F3} not below legacy {legacy.MaxStep:F3}");
        Assert.False(HasTwoPositionOscillation(slide.Positions), "two-position oscillation detected");
    }

    [Fact]
    public void TreeTrunkOrbitContinuity()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(24.5f, 0, 0f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        Assert.False(HasTwoPositionOscillation(slide.Positions), "two-position oscillation detected");
        Assert.All(slide.Positions, position => Assert.True(IsFinite(position)));
        Assert.True(slide.MaxStep < 1.5f, $"tree orbit step {slide.MaxStep:F3} exceeded bound");
    }

    [Fact]
    public void CornerOrbitsAreDeterministic()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(2.5f, 0, 7.5f);

        var first = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);
        var second = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        Assert.Equal(first.MaxStep, second.MaxStep, precision: 6);
        for (var i = 0; i < first.Positions.Count; i++)
        {
            Assert.Equal(first.Positions[i], second.Positions[i]);
        }
    }

    // --- step reduction ---------------------------------------------------

    [Fact]
    public void InteriorCornerOrbitStepMateriallyReducedVsLegacy()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(2.5f, 0, 7.5f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        // Phase 2M baseline: the hard clamp pops when the nearest line-of-sight
        // blocker changes at the corner. Phase 2Q slides around it.
        Assert.True(legacy.MaxStep > 1.5f, $"test premise: legacy snap {legacy.MaxStep:F3} m should be large");
        Assert.True(slide.MaxStep < 1.0f, $"Phase 2Q step {slide.MaxStep:F3} m exceeded the 1.0 m goal");
        Assert.True(slide.MaxStep < 0.5f * legacy.MaxStep,
            $"step reduction insufficient: legacy {legacy.MaxStep:F3} m vs slide {slide.MaxStep:F3} m");
    }

    // --- distance behavior ------------------------------------------------

    [Fact]
    public void SlidingRetainsUsefulDistanceVsLegacy()
    {
        var set = LonghouseObstructions();
        var target = new Vector3(2.5f, 0, 7.5f);

        var legacy = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: false);
        var slide = RunOrbit(set, target, 72, 1.0 / 60.0, slideEnabled: true);

        // The legacy hard clamp collapsed the camera toward Erika (min 0.08 m);
        // sliding keeps the camera at a useful distance.
        Assert.True(legacy.MinDistance < 0.5f, $"test premise: legacy collapsed to {legacy.MinDistance:F3} m");
        Assert.True(slide.MinDistance > legacy.MinDistance,
            $"slide min distance {slide.MinDistance:F3} m did not improve on legacy {legacy.MinDistance:F3} m");
        Assert.True(slide.MinDistance > 0.2f, $"slide collapsed to {slide.MinDistance:F3} m");
    }

    // --- control basis ----------------------------------------------------

    [Fact]
    public void BodySlideDoesNotChangeControlBasis()
    {
        var obstructed = new ThirdPersonCamera { Obstructions = LonghouseObstructions() };
        var legacy = new ThirdPersonCamera { Obstructions = LonghouseObstructions(), CameraBodySlideEnabled = false };
        var camera = new CameraState(Vector3.Zero);

        obstructed.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        legacy.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        obstructed.SnapToTarget(camera, Vector3.Zero);
        legacy.SnapToTarget(camera, Vector3.Zero);

        Assert.Equal(legacy.ControlForward, obstructed.ControlForward);
        Assert.Equal(legacy.ControlRight, obstructed.ControlRight);

        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.CameraRig.Obstructions = null;
        disabled.CameraRig.CameraBodySlideEnabled = false;
        enabled.CameraRig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);
        disabled.CameraRig.SetOrbit(-MathF.PI / 2, ThirdPersonCamera.DefaultOrbitPitchRadians);

        Assert.Equal(disabled.ComputeMovementIntent(Move(forward: true)), enabled.ComputeMovementIntent(Move(forward: true)));
        Assert.Equal(disabled.ComputeMovementIntent(Move(forward: true, right: true)), enabled.ComputeMovementIntent(Move(forward: true, right: true)));
        Assert.Equal(disabled.ComputeMovementIntent(Move(back: true, left: true)), enabled.ComputeMovementIntent(Move(back: true, left: true)));
    }

    // --- gameplay A/B -----------------------------------------------------

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

    private static (GameSession Enabled, GameSession Disabled) SessionPair()
    {
        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.CameraRig.Obstructions = null;
        disabled.CameraRig.CameraBodySlideEnabled = false;
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
        Assert.Equal(disabled.IsMovementBlocked, enabled.IsMovementBlocked);
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
    public void GameplayIdenticalWithAndWithoutBodySlide()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true));
        RunIdentical(enabled, disabled, 60, _ => Move(forward: true, sprint: true));
        RunIdentical(enabled, disabled, 30, _ => NoInput());
    }

    [Fact]
    public void OrbitWhileMovingIsIdenticalWithAndWithoutBodySlide()
    {
        var (enabled, disabled) = SessionPair();

        // Walk straight through the doorway to the interior first.
        RunIdentical(enabled, disabled, 300, _ => Move(forward: true));
        Assert.True(enabled.ErikaPosition.Z < LonghouseLayout.FrontZ, "test premise: Erika must reach the interior");

        // Then orbit the camera while continuing to walk: gameplay must stay
        // identical and the enabled rig must actually constrain the camera.
        var triggered = false;
        for (var i = 301; i <= 400; i++)
        {
            var time = new FrameTime(i * 0.016, 0.016);
            var state = MoveLook(forward: true, new Vector2(4, 0));
            enabled.Update(time, state);
            disabled.Update(time, state);
            AssertGameplayIdentical(enabled, disabled);
            triggered |= enabled.CameraRig.WasCameraMotionConstrained || enabled.CameraRig.CameraVisibilityConstrained;
        }

        Assert.True(triggered, "test premise: orbiting while walking inside the longhouse must constrain the enabled camera");
    }

    // --- blocked-movement regression --------------------------------------

    [Fact]
    public void BlockedMovementRecoversWhileOrbiting()
    {
        // Erika walks from spawn into the hearth and idles there (Phase 2P
        // latch), then the camera orbits while blocked, then the orbit turns
        // her movement direction clear and she must recover.
        var session = SessionWithClips();
        var dt = 1.0 / 60.0;
        var t = 0.0;

        // Walk forward from spawn (through the doorway) until the hearth blocks
        // the path and the blocked latch engages.
        for (var i = 0; i < 900 && !session.IsMovementBlocked; i++)
        {
            t += dt;
            session.Update(new FrameTime(t, dt), Move(forward: true));
        }

        Assert.True(session.IsMovementBlocked, "test premise: Erika should be blocked against the hearth");
        var blockedPosition = session.ErikaPosition;

        // Orbit a full turn while holding forward: the camera body slides along
        // the obstructions, and the orbit rotates the movement control basis, so
        // Erika turns with it. The camera sliding must not interfere with the
        // Phase 2P probe/latch: once her facing clears the hearth the latch
        // releases and she recovers.
        var triggered = false;
        var released = false;
        for (var i = 1; i <= 240; i++)
        {
            t += dt;
            session.Update(new FrameTime(t, dt), MoveLook(forward: true, new Vector2(6, 0)));
            Assert.True(IsFinite(session.Camera.Position));
            Assert.False(IsInsideAnyCollider(session.CameraObstructions, session.Camera.Position));
            triggered |= session.CameraRig.WasCameraMotionConstrained || session.CameraRig.CameraVisibilityConstrained;
            released |= !session.IsMovementBlocked;
        }

        Assert.True(triggered, "test premise: orbiting near the hearth must constrain the camera");
        Assert.True(released, "blocked latch should release as the orbit turns the movement direction clear");
        Assert.True(Vector3.Distance(session.ErikaPosition, blockedPosition) > 0.05f,
            "Erika should have moved after recovery");
    }

    // --- frame-rate validation --------------------------------------------

    [Fact]
    public void CornerOrbitFrameRateValidation()
    {
        var set = LonghouseObstructions();
        var scenarios = new (string Name, Vector3 Target)[]
        {
            ("Exterior", new Vector3(4.6f, 0, 9.6f)),
            ("Interior", new Vector3(2.5f, 0, 7.5f)),
        };

        foreach (var (name, target) in scenarios)
        {
            var results = new (int Fps, float MaxStep, Vector3 Final, float Distance)[3];
            var r = 0;
            foreach (var fps in new[] { 30, 60, 144 })
            {
                var dt = 1.0 / fps;
                var rig = new ThirdPersonCamera { Obstructions = set };
                var camera = new CameraState(Vector3.Zero);
                var lookTarget = rig.TargetPoint(target);
                rig.SetOrbit(0f, ThirdPersonCamera.DefaultOrbitPitchRadians);
                rig.SnapToTarget(camera, target);

                var previous = camera.Position;
                var maxStep = 0f;
                for (var i = 1; i <= fps; i++)
                {
                    rig.SetOrbit(i * MathF.Tau / fps, ThirdPersonCamera.DefaultOrbitPitchRadians);
                    rig.Follow(camera, target, new FrameTime(i * dt, dt));
                    Assert.False(IsInsideAnyCollider(set, camera.Position), $"{name} fps={fps} frame {i}: penetration");
                    Assert.False(set.CastSegment(lookTarget, camera.Position, out _), $"{name} fps={fps} frame {i}: sight line obstructed");
                    maxStep = MathF.Max(maxStep, Vector3.Distance(camera.Position, previous));
                    previous = camera.Position;
                }

                results[r++] = (fps, maxStep, camera.Position, rig.ActualTargetDistance);
            }

            for (var i = 1; i < results.Length; i++)
            {
                Assert.True(Math.Abs(results[0].Final.X - results[i].Final.X) < 0.5f,
                    $"{name}: X divergence {Math.Abs(results[0].Final.X - results[i].Final.X):F3} m across frame rates");
                Assert.True(Math.Abs(results[0].Final.Z - results[i].Final.Z) < 0.5f,
                    $"{name}: Z divergence {Math.Abs(results[0].Final.Z - results[i].Final.Z):F3} m across frame rates");
                Assert.True(Math.Abs(results[0].Distance - results[i].Distance) < 0.2f,
                    $"{name}: distance divergence {Math.Abs(results[0].Distance - results[i].Distance):F3} m");
            }

            Assert.All(results, result =>
            {
                Assert.True(result.MaxStep < 1.5f, $"{name} fps={result.Fps}: step {result.MaxStep:F3} m exceeded bound");
                Assert.True(result.Distance > 0.2f, $"{name} fps={result.Fps}: camera collapsed");
            });
        }
    }

    [Fact]
    public void ObstructionRecoveryIsFrameRateIndependentWithSlide()
    {
        var set = LonghouseObstructions();
        var results = new (int Fps, float Distance, double SecondsToNearNominal)[3];
        var r = 0;
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1.0 / fps;
            var rig = new ThirdPersonCamera { Obstructions = set };
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

    // --- shared gameplay fixtures ------------------------------------------

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
}

