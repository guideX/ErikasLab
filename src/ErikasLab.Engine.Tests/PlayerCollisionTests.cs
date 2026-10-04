using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2N player collision and wall sliding. Covers the portable
/// circle-vs-expanded-OBB sweep math (miss/hit/normal/rotated/parallel/
/// boundary/inside/nearest/order/finite/skin), the bounded sweep-and-slide
/// resolver (head-on/diagonal/corner/multi-wall/starting-overlap), the real
/// Phase 2L longhouse blockers (walls, doorway, spawn, tree trunks), gameplay
/// integration through <see cref="GameSession"/> (walk/run into walls, doorway
/// passage, state finiteness, release, turn-in-place), the clear-space A/B
/// root-motion regression, frame-rate validation, and determinism.
/// </summary>
public sealed class PlayerCollisionTests
{
    private const float Radius = PlayerCollisionPolicy.PlayerCollisionRadiusMeters;
    private const float Skin = PlayerCollisionPolicy.PlayerCollisionSkinMeters;
    private const float PenetrationTolerance = 1e-3f;

    // --- helpers ---------------------------------------------------------

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static PlayerCollisionBox Box(string name, float centerX, float centerZ, float halfX, float halfZ, float yaw = 0f) =>
        new(name, new Vector2(centerX, centerZ), new Vector2(halfX, halfZ), yaw);

    private static PlayerCollisionSet Set(params PlayerCollisionBox[] boxes) => new(boxes);

    private static PlayerCollisionSet LonghouseCollisions() => EnvironmentFactory.CreatePlayerCollisionSet();

    private static PlayerCollisionResult Resolve(PlayerCollisionSet set, float startX, float startZ, float deltaX, float deltaZ) =>
        PlayerCollisionResolver.Resolve(set, new Vector2(startX, startZ), new Vector2(deltaX, deltaZ), Radius);

    private static bool IsPenetrating(PlayerCollisionSet set, Vector3 position) =>
        set.ContainsPoint(new Vector2(position.X, position.Z), Radius - PenetrationTolerance);

    private static bool IsPenetrating(PlayerCollisionSet set, Vector2 position) =>
        set.ContainsPoint(position, Radius - PenetrationTolerance);

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
                [Vector3.Zero, new Vector3(0, 0, 200)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Run() => new(
        "run", 0.5f, 30f, [
            new AnimationChannel(0, [0f, 0.5f],
                [Vector3.Zero, new Vector3(0, 0, 300)],
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

    private static InputState NoInput() => Move();

    /// <summary>Monotonic test clock so multiple Step calls form one continuous timeline.</summary>
    private sealed class Timeline
    {
        public double Time { get; private set; }

        public void Step(GameSession session, int frames, Func<int, InputState> input, double dt = 1.0 / 60.0)
        {
            for (var i = 1; i <= frames; i++)
            {
                Time += dt;
                session.Update(new FrameTime(Time, dt), input(i));
            }
        }
    }

    // --- intersection math -----------------------------------------------

    [Fact]
    public void SweepMissesBox()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.False(set.Sweep(new Vector2(0, 5), new Vector2(0, -3), Radius, out _));
    }

    [Fact]
    public void SweepHitsBox()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.True(set.Sweep(new Vector2(0, -5), new Vector2(0, 10), Radius, out var hit));
        Assert.Equal("B", hit.Box.Name);
        // Expanded half depth 1 + 0.30 radius: enters at z = -1.30, t = 3.70/10.
        Assert.Equal(0.37f, hit.Fraction, precision: 5);
    }

    [Fact]
    public void HeadOnNormalCorrect()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.True(set.Sweep(new Vector2(0, -5), new Vector2(0, 10), Radius, out var hit));
        Assert.Equal(0f, hit.Normal.X, precision: 5);
        Assert.Equal(-1f, hit.Normal.Y, precision: 5);
        Assert.Equal(1f, hit.Normal.Length(), precision: 5);
    }

    [Fact]
    public void ShallowAngleHit()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.True(set.Sweep(new Vector2(-5, 1.0f), new Vector2(10, 0.1f), Radius, out var hit));
        // Enters through the near X face, so the normal points -X.
        Assert.Equal(-1f, hit.Normal.X, precision: 4);
        Assert.Equal(0f, hit.Normal.Y, precision: 4);
        Assert.InRange(hit.Fraction, 0.3f, 0.4f);
    }

    [Fact]
    public void RotatedBlockerHit()
    {
        var yaw = MathF.PI / 4f;
        var set = Set(Box("R", 0, 0, 1, 1, yaw));

        Assert.True(set.Sweep(new Vector2(5, 0), new Vector2(-10, 0), Radius, out var hit));
        Assert.True(IsFinite(hit.Normal));
        Assert.Equal(1f, hit.Normal.Length(), precision: 4);
        Assert.InRange(hit.Fraction, 0f, 1f);
    }

    [Fact]
    public void ExactEdgeContactCountsAsHit()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        // End exactly on the expanded near face z = -1.30: contact is a hit at t = 1.
        Assert.True(set.Sweep(new Vector2(0, -5), new Vector2(0, 3.7f), Radius, out var contact));
        Assert.Equal(1f, contact.Fraction, precision: 5);

        // Just short of the expanded face: miss.
        Assert.False(set.Sweep(new Vector2(0, -5), new Vector2(0, 3.69f), Radius, out _));
    }

    [Fact]
    public void ParallelMovementMisses()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        // Parallel to X at z = 5, well outside the expanded Y slab.
        Assert.False(set.Sweep(new Vector2(-5, 5), new Vector2(10, 0), Radius, out _));
    }

    [Fact]
    public void ZeroMovementIsSafe()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.False(set.Sweep(new Vector2(0, 5), Vector2.Zero, Radius, out _));
    }

    [Fact]
    public void StartingInsideReportsImmediateContact()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.True(set.Sweep(Vector2.Zero, new Vector2(10, 0), Radius, out var hit));
        Assert.Equal(0f, hit.Fraction, precision: 6);
        // Least-penetration axis is X, sign from the center's local coordinate (0 -> +).
        Assert.Equal(1f, hit.Normal.X, precision: 5);
        Assert.Equal(0f, hit.Normal.Y, precision: 5);
    }

    [Fact]
    public void NonFiniteInputIsSafe()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        Assert.False(set.Sweep(new Vector2(float.NaN, 0), new Vector2(1, 0), Radius, out _));
        Assert.False(set.Sweep(Vector2.Zero, new Vector2(float.PositiveInfinity, 0), Radius, out _));

        var resolved = PlayerCollisionResolver.Resolve(
            set, new Vector2(float.NaN, 0), new Vector2(1, 1), Radius);
        Assert.Equal(new Vector2(float.NaN, 0), resolved.Position);
        Assert.Equal(Vector2.Zero, resolved.Delta);
        Assert.False(resolved.Constrained);
        Assert.False(IsFinite(resolved.Position));
    }

    [Fact]
    public void HighSpeedSegmentCrossesThinWallButStillHits()
    {
        // A thin wall the segment would jump entirely if only the endpoint were tested.
        var set = Set(Box("Thin", 0, 0, 0.125f, 8f));

        Assert.True(set.Sweep(new Vector2(0, -50), new Vector2(0, 100), Radius, out var hit));
        Assert.Equal("Thin", hit.Box.Name);
        Assert.InRange(hit.Fraction, 0.4f, 0.45f);
    }

    [Fact]
    public void NearestBlockerWins()
    {
        var set = Set(
            Box("Far", 0, -3, 1, 1),
            Box("Near", 0, 3, 1, 1));

        Assert.True(set.Sweep(new Vector2(0, 6), new Vector2(0, -12), Radius, out var hit));
        Assert.Equal("Near", hit.Box.Name);
    }

    [Fact]
    public void SweepColliderOrderDoesNotChangeResult()
    {
        var forward = Set(
            Box("Far", 0, -3, 1, 1),
            Box("Near", 0, 3, 1, 1));
        var reversed = Set(
            Box("Near", 0, 3, 1, 1),
            Box("Far", 0, -3, 1, 1));

        Assert.True(forward.Sweep(new Vector2(0, 6), new Vector2(0, -12), Radius, out var first));
        Assert.True(reversed.Sweep(new Vector2(0, 6), new Vector2(0, -12), Radius, out var second));

        Assert.Equal(first.Box.Name, second.Box.Name);
        Assert.Equal(first.Fraction, second.Fraction, precision: 6);
        Assert.Equal(first.Normal, second.Normal);
    }

    [Fact]
    public void ContactSkinIsRespected()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        var result = Resolve(set, 0, -5, 0, 10);

        Assert.True(result.Constrained);
        Assert.Equal(0f, result.Delta.X, precision: 6);
        // Contact at z = -1.30, then pushed out by the skin.
        Assert.Equal(-1.30f - Skin, result.Position.Y, precision: 4);
        Assert.False(IsPenetrating(set, result.Position));
    }

    // --- slide resolver --------------------------------------------------

    [Fact]
    public void HeadOnStopsAtWallWithoutSidewaysDisplacement()
    {
        var set = Set(Box("Wall", 0, 0, 5, 0.125f));

        var result = Resolve(set, -3, -5, 0, 10);

        Assert.True(result.Constrained);
        Assert.Equal(0f, result.Delta.X, precision: 6); // no invented sideways motion
        Assert.True(result.Delta.Y < 10f);
        Assert.True(result.Position.Y <= -0.425f - Skin + 1e-4f);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void DiagonalRemovesInwardComponentAndKeepsTangential()
    {
        var set = Set(Box("Wall", 0, 0, 5, 0.125f));

        var result = Resolve(set, -3, -5, 1, 10);

        Assert.True(result.Constrained);
        // The requested +X tangential component is fully preserved.
        Assert.Equal(1f, result.Delta.X, precision: 4);
        // The inward +Y component is removed (stopped at the wall plus skin).
        Assert.Equal(4.565f, result.Delta.Y, precision: 3);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void CornerIsBoundedAndDeterministic()
    {
        // Two perpendicular walls meeting at the origin.
        var set = Set(
            Box("WallX", 0, 0, 5, 0.125f),
            Box("WallY", 0, 0, 0.125f, 5));

        var first = Resolve(set, -5, -5, 10, 10);
        var second = Resolve(set, -5, -5, 10, 10);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Delta, second.Delta);
        Assert.True(first.Constrained);
        Assert.InRange(first.HitCount, 1, PlayerCollisionPolicy.MaxSlideIterations);
        Assert.False(IsPenetrating(set, first.Position));
        Assert.True(IsFinite(first.Position));
    }

    [Fact]
    public void MultipleWallsEarliestResolvedFirst()
    {
        var set = Set(
            Box("First", 0, 0, 5, 0.125f),
            Box("Second", 0, -3, 5, 0.125f));

        var result = Resolve(set, 0, 5, 0, -10);

        Assert.True(result.Constrained);
        Assert.Equal("First", result.LastHitName);
    }

    [Fact]
    public void StartingOverlapResolvesSafely()
    {
        // Player center already inside a realistic wall thickness.
        var set = Set(Box("Wall", 0, 0, 0.125f, 5f));

        var result = PlayerCollisionResolver.Resolve(set, Vector2.Zero, Vector2.Zero, Radius);

        Assert.True(result.Constrained);
        Assert.True(IsFinite(result.Position));
        Assert.True(result.Delta.Length() <= PlayerCollisionPolicy.MaxDepenetrationDistanceMeters + 1e-4f);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void DeepOverlapCorrectionIsBounded()
    {
        // A large blocker means full depenetration would exceed the cap; the
        // resolver must still bound the correction and never emit NaN.
        var set = Set(Box("Huge", 0, 0, 20, 20));

        var result = PlayerCollisionResolver.Resolve(set, Vector2.Zero, Vector2.Zero, Radius);

        Assert.True(IsFinite(result.Position));
        Assert.True(result.Delta.Length() <= PlayerCollisionPolicy.MaxDepenetrationDistanceMeters + 1e-4f);
    }

    [Fact]
    public void ZeroRadiusInputIsRejectedSafely()
    {
        var set = Set(Box("B", 0, 0, 1, 1));

        var result = PlayerCollisionResolver.Resolve(set, new Vector2(0, -5), new Vector2(0, 10), 0f);

        Assert.Equal(new Vector2(0, -5), result.Position);
        Assert.Equal(Vector2.Zero, result.Delta);
        Assert.False(result.Constrained);
    }

    // --- longhouse geometry ----------------------------------------------

    [Fact]
    public void PlayerColliderCountIsExpected()
    {
        var set = LonghouseCollisions();

        // 5 shell boxes (2 long walls + rear + 2 front flanking segments)
        // + 5 interior furnishings (hearth + 2 benches + 2 tables) + 24 trunks.
        Assert.Equal(34, set.Count);
    }

    [Fact]
    public void PlayerCollidersExcludeRoofLintelAndGables()
    {
        var set = LonghouseCollisions();

        Assert.DoesNotContain(set.Boxes, b => b.Name.Contains("Roof", StringComparison.Ordinal));
        Assert.DoesNotContain(set.Boxes, b => b.Name.Contains("Lintel", StringComparison.Ordinal));
        Assert.DoesNotContain(set.Boxes, b => b.Name.Contains("Gable", StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerCollidersMatchSceneGeometry()
    {
        var scene = EnvironmentFactory.Create();
        var set = LonghouseCollisions();
        var names = new[] { "Wall.Left", "Wall.Right", "Wall.Rear", "Wall.Front.Left", "Wall.Front.Right" };

        foreach (var name in names)
        {
            var sceneObject = scene.Objects.Single(o => o.Name == name);
            var box = set.Boxes.Single(b => b.Name == name);

            Assert.Equal(sceneObject.Transform.Position.X, box.CenterXZ.X, precision: 5);
            Assert.Equal(sceneObject.Transform.Position.Z, box.CenterXZ.Y, precision: 5);
            Assert.Equal(sceneObject.Transform.Scale.X / 2f, box.HalfExtentsXZ.X, precision: 5);
            Assert.Equal(sceneObject.Transform.Scale.Z / 2f, box.HalfExtentsXZ.Y, precision: 5);
            Assert.Equal(0f, box.YawRadians, precision: 6);
        }

        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var name = $"Tree.{i:00}.Trunk";
            var sceneObject = scene.Objects.Single(o => o.Name == name);
            var box = set.Boxes.Single(b => b.Name == name);

            Assert.Equal(sceneObject.Transform.Position.X, box.CenterXZ.X, precision: 5);
            Assert.Equal(sceneObject.Transform.Position.Z, box.CenterXZ.Y, precision: 5);
            Assert.Equal(LonghouseLayout.TreeYawRadians(i), box.YawRadians, precision: 6);
        }
    }

    [Fact]
    public void SpawnIsCollisionFree()
    {
        var set = LonghouseCollisions();
        var spawn = LonghouseLayout.SpawnPosition;

        Assert.False(set.ContainsPoint(new Vector2(spawn.X, spawn.Z), Radius));
        Assert.False(IsPenetrating(set, spawn));
    }

    [Fact]
    public void LeftWallBlocks()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, -1, 0, -5, 0);

        Assert.True(result.Constrained);
        Assert.True(result.Position.X >= -2.575f - 1e-3f, $"stopped at x={result.Position.X:F4}");
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void RightWallBlocks()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, 1, 0, 5, 0);

        Assert.True(result.Constrained);
        Assert.True(result.Position.X <= 2.575f + 1e-3f, $"stopped at x={result.Position.X:F4}");
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void RearWallBlocks()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, 0, -6, 0, -5);

        Assert.True(result.Constrained);
        Assert.Equal("Wall.Rear", result.LastHitName);
        Assert.True(result.Position.Y >= -7.575f - 1e-3f, $"stopped at z={result.Position.Y:F4}");
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void FrontLeftSegmentBlocks()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, -1.8f, 6, 0, 5);

        Assert.True(result.Constrained);
        Assert.Equal("Wall.Front.Left", result.LastHitName);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void FrontRightSegmentBlocks()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, 1.8f, 6, 0, 5);

        Assert.True(result.Constrained);
        Assert.Equal("Wall.Front.Right", result.LastHitName);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void DoorwayCenterIsTraversable()
    {
        var set = LonghouseCollisions();

        var result = Resolve(set, 0, 6, 0, 5);

        Assert.False(result.Constrained);
        Assert.Equal(5f, result.Delta.Y, precision: 5);
        Assert.Equal(0f, result.Delta.X, precision: 6);
    }

    [Fact]
    public void DoorwayEntryWorksWithPlayerRadius()
    {
        var set = LonghouseCollisions();

        // Inside the usable corridor (door 1.2 m, radius 0.30 m -> +/- 0.30 m).
        Assert.False(Resolve(set, 0.25f, 6, 0, 5).Constrained);
        // Just outside the corridor: the jamb blocks.
        Assert.True(Resolve(set, 0.40f, 6, 0, 5).Constrained);
    }

    [Fact]
    public void DoorwayDiagonalEntryWorks()
    {
        var set = LonghouseCollisions();

        // Approach at an angle but aim through the opening at the wall plane.
        var result = Resolve(set, 0.5f, 6, -0.7f, 5);

        Assert.False(result.Constrained);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void PlayerCanExitFromInside()
    {
        var set = LonghouseCollisions();

        // Start in the clear central aisle (0, 2) and walk back out the door;
        // the hearth now occupies the old (0, 0) origin point.
        var result = Resolve(set, 0, 2, 0, 10);

        Assert.False(result.Constrained);
        Assert.Equal(10f, result.Delta.Y, precision: 5);
    }

    [Fact]
    public void NoInvisibleColliderSpansTheDoorway()
    {
        var set = LonghouseCollisions();
        var frontZ = LonghouseLayout.FrontZ;

        Assert.False(set.ContainsPoint(new Vector2(0, frontZ), Radius));

        // Every blocker that overlaps the door plane must keep its expanded X
        // interval clear of the door center.
        foreach (var box in set.Boxes)
        {
            if (MathF.Abs(box.CenterXZ.Y - frontZ) > box.HalfExtentsXZ.Y + Radius)
            {
                continue;
            }

            var minX = box.CenterXZ.X - box.HalfExtentsXZ.X - Radius;
            var maxX = box.CenterXZ.X + box.HalfExtentsXZ.X + Radius;
            Assert.True(minX > 0f || maxX < 0f, $"{box.Name} spans the doorway center (x range [{minX:F3}, {maxX:F3}])");
        }
    }

    [Fact]
    public void TreeTrunkBlocksPlayer()
    {
        var set = LonghouseCollisions();
        var tree = LonghouseLayout.TreePosition(0);
        var name = "Tree.00.Trunk";

        var result = Resolve(set, tree.X - 2f, tree.Z, 4f, 0f);

        Assert.True(result.Constrained);
        Assert.Equal(name, result.LastHitName);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void PlayerCollisionSetIsDeterministic()
    {
        var first = LonghouseCollisions();
        var second = LonghouseCollisions();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first.Boxes[i], second.Boxes[i]);
        }
    }

    // --- gameplay integration --------------------------------------------

    [Fact]
    public void WalkForwardPassesThroughDoorwayIntoInterior()
    {
        var session = SessionWithClips();
        var set = session.PlayerCollisions;
        var timeline = new Timeline();

        timeline.Step(session, 400, _ => Move(forward: true));

        Assert.True(session.ErikaPosition.Z < LonghouseLayout.FrontZ, $"z={session.ErikaPosition.Z:F3} did not enter");
        Assert.False(IsPenetrating(set, session.ErikaPosition));
        Assert.Equal(0f, session.ErikaPosition.X, precision: 3);
    }

    [Fact]
    public void WalkForwardStopsAtHearth()
    {
        var session = SessionWithClips();
        var set = session.PlayerCollisions;
        var timeline = new Timeline();

        // Phase 2O: walking straight down the central aisle now meets the solid
        // hearth before the rear wall (the hearth is centered on the aisle).
        timeline.Step(session, 900, _ => Move(forward: true));

        var hearthFrontZ = LonghouseLayout.HearthCenterZ + LonghouseLayout.HearthLength / 2f;
        Assert.True(session.WasPlayerCollisionConstrained);
        Assert.Equal("Hearth", session.LastPlayerCollisionName);
        Assert.True(session.ErikaPosition.Z >= hearthFrontZ, $"z={session.ErikaPosition.Z:F3} passed into the hearth");
        Assert.True(session.ErikaPosition.Z <= hearthFrontZ + 0.45f, $"z={session.ErikaPosition.Z:F3} stopped too early");
        Assert.False(IsPenetrating(set, session.ErikaPosition));
    }

    [Fact]
    public void WalkDiagonallySlidesAlongLeftBench()
    {
        var session = SessionWithClips();
        var set = session.PlayerCollisions;
        var timeline = new Timeline();

        // Enter the interior straight through the door and up to the hearth.
        timeline.Step(session, 400, _ => Move(forward: true));
        var zBefore = session.ErikaPosition.Z;

        // Then push forward-left: Erika slides along the hearth front, rounds its
        // left corner, and continues down the left side until the long bench
        // blocks her. The inward component is removed and she keeps sliding
        // toward the rear along the bench (Phase 2N slide behavior reused).
        var minX = float.MaxValue;
        for (var i = 1; i <= 400; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            Assert.False(IsPenetrating(set, session.ErikaPosition));
            minX = MathF.Min(minX, session.ErikaPosition.X);
        }

        var benchInnerX = -LonghouseLayout.BenchCenterX + LonghouseLayout.BenchDepth / 2f;
        var expectedMinX = benchInnerX + Radius + Skin;
        Assert.True(session.ErikaPosition.Z < zBefore, "Erika did not slide past the hearth");
        Assert.True(minX > benchInnerX, $"penetrated the left bench surface (minX={minX:F3})");
        Assert.True(MathF.Abs(minX - expectedMinX) < 0.05f, $"did not settle against the left bench (minX={minX:F3}, expected~{expectedMinX:F3})");
    }

    [Fact]
    public void RunTowardHearthDoesNotTunnelOrPenetrate()
    {
        var session = SessionWithClips();
        var set = session.PlayerCollisions;
        var timeline = new Timeline();

        // Sprint straight down the central aisle into the solid hearth.
        var minZ = float.MaxValue;
        for (var i = 0; i < 600; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, sprint: true));
            minZ = MathF.Min(minZ, session.ErikaPosition.Z);
        }

        var hearthFrontZ = LonghouseLayout.HearthCenterZ + LonghouseLayout.HearthLength / 2f;
        Assert.True(minZ >= hearthFrontZ - 1e-3f, $"tunneled into the hearth: minZ={minZ:F3}");
        Assert.True(session.ErikaPosition.Z >= hearthFrontZ - 1e-3f, $"tunneled into the hearth: z={session.ErikaPosition.Z:F3}");
        Assert.True(session.ErikaPosition.Z <= hearthFrontZ + 0.45f);
        Assert.Equal("Hearth", session.LastPlayerCollisionName);
        Assert.False(IsPenetrating(set, session.ErikaPosition));
    }

    [Fact]
    public void StateRemainsFiniteWhileBlocked()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));

        for (var i = 0; i < 120; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, sprint: true));

            Assert.True(session.WasPlayerCollisionConstrained,
                $"i={i} pos=({session.ErikaPosition.X:F4},{session.ErikaPosition.Z:F4}) req=({session.RequestedPlayerDisplacement.X:F4},{session.RequestedPlayerDisplacement.Z:F4}) speed={session.CurrentMoveSpeedMetersPerSecond:F4} clip={session.ActiveClipName} hits={session.PlayerCollisionHitCount}");
            Assert.True(float.IsFinite(session.CurrentMoveSpeedMetersPerSecond));
            Assert.True(float.IsFinite(session.TargetMoveSpeedMetersPerSecond));
            Assert.True(float.IsFinite(session.RootMotionGain));
            Assert.True(float.IsFinite(session.VisualPlaybackRate));
            Assert.True(float.IsFinite(session.ErikaYawRadians));
            Assert.True(IsFinite(session.ErikaPosition));
            Assert.True(IsFinite(session.Camera.Position));
            Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        }

        // Pushing into a wall must not corrupt the speed envelope: the intent
        // still requests run speed.
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > 0f);
    }

    [Fact]
    public void ReleaseAtWallCompletesNormalIdleLifecycle()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);

        timeline.Step(session, 240, _ => NoInput());

        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond, precision: 6);
        Assert.False(session.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void TurnInPlaceNearWallKeepsZeroTranslation()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 600, _ => Move(forward: true));
        timeline.Step(session, 240, _ => NoInput());
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);

        var start = session.ErikaPosition;
        var turned = false;
        for (var i = 1; i <= 8; i++)
        {
            timeline.Step(session, 1, _ => Move(back: true));
            turned |= session.IsTurningInPlace;
            Assert.Equal(start.X, session.ErikaPosition.X, precision: 5);
            Assert.Equal(start.Z, session.ErikaPosition.Z, precision: 5);
        }

        Assert.True(turned, "test premise: a 180 degree stationary turn should gate translation");
    }

    [Fact]
    public void TurnThenWalkBackThroughDoorwayBehavesNormally()
    {
        var session = SessionWithClips();
        var set = session.PlayerCollisions;
        var timeline = new Timeline();

        // Face the rear wall, then turn around and walk back out the door.
        timeline.Step(session, 600, _ => Move(forward: true));
        timeline.Step(session, 240, _ => NoInput());
        timeline.Step(session, 900, _ => Move(back: true));

        Assert.True(session.ErikaPosition.Z > LonghouseLayout.FrontZ, $"z={session.ErikaPosition.Z:F3} did not exit");
        Assert.False(IsPenetrating(set, session.ErikaPosition));
    }

    // --- root-motion regression (clear-space A/B) ------------------------

    private static void AssertGameplayIdentical(GameSession enabled, GameSession disabled)
    {
        Assert.Equal(disabled.ErikaPosition.X, enabled.ErikaPosition.X, precision: 5);
        Assert.Equal(disabled.ErikaPosition.Z, enabled.ErikaPosition.Z, precision: 5);
        Assert.Equal(disabled.ErikaYawRadians, enabled.ErikaYawRadians, precision: 6);
        Assert.Equal(disabled.CurrentMoveSpeedMetersPerSecond, enabled.CurrentMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(disabled.TargetMoveSpeedMetersPerSecond, enabled.TargetMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(disabled.RootMotionGain, enabled.RootMotionGain, precision: 5);
        Assert.Equal(disabled.VisualPlaybackRate, enabled.VisualPlaybackRate, precision: 5);
        Assert.Equal(disabled.ActiveClipName, enabled.ActiveClipName);
        Assert.Equal(disabled.IsTurningInPlace, enabled.IsTurningInPlace);
        Assert.Equal(disabled.RequestedPlayerDisplacement, enabled.RequestedPlayerDisplacement);
        Assert.Equal(disabled.AcceptedPlayerDisplacement, enabled.AcceptedPlayerDisplacement);
        Assert.Equal(disabled.VisualPoseElapsedSeconds, enabled.VisualPoseElapsedSeconds, precision: 9);
        Assert.Equal(disabled.TransitionSourcePoseElapsedSeconds, enabled.TransitionSourcePoseElapsedSeconds, precision: 9);
        Assert.Equal(disabled.Transition is null, enabled.Transition is null);
    }

    private static (GameSession Enabled, GameSession Disabled) SessionPair()
    {
        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.PlayerCollisionEnabled = false;
        return (enabled, disabled);
    }

    private static void RunIdentical(GameSession enabled, GameSession disabled, int frames, Func<int, InputState> input)
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

    // Phase 2O: these A/B routes are deliberately short enough to stay clear of
    // the interior furnishings (the hearth is ~11.2 m down the aisle from spawn),
    // so the only difference between the two sessions is the collision toggle.
    [Fact]
    public void ClearSpaceWalkIsIdenticalWithAndWithoutPlayerCollision()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 300, _ => Move(forward: true));
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void ClearSpaceRunIsIdenticalWithAndWithoutPlayerCollision()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 40, _ => Move(forward: true, sprint: true));
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void ClearSpaceWalkToRunAndStopIsIdenticalWithAndWithoutPlayerCollision()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 40, _ => Move(forward: true));
        RunIdentical(enabled, disabled, 40, _ => Move(forward: true, sprint: true));
        RunIdentical(enabled, disabled, 60, _ => NoInput());
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void ClearSpaceStationaryTurnIsIdenticalWithAndWithoutPlayerCollision()
    {
        var (enabled, disabled) = SessionPair();
        RunIdentical(enabled, disabled, 1, _ => NoInput());
        RunIdentical(enabled, disabled, 30, _ => Move(right: true));
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    // --- frame-rate validation -------------------------------------------

    [Fact]
    public void HighSpeedWallStopIsFrameRateIndependent()
    {
        var set = LonghouseCollisions();
        var results = new (int Fps, Vector2 Position)[3];
        var r = 0;

        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1f / fps;
            // 40 m/s: at 30 Hz a step is 1.33 m, far enough to jump the
            // ~0.85 m expanded wall if the sweep were not continuous. Start
            // behind the hearth (z = -5) so the straight run to the rear wall is
            // clear of interior furnishings.
            var position = new Vector2(0f, -5f);
            for (var i = 0; i < fps * 4; i++)
            {
                var result = PlayerCollisionResolver.Resolve(set, position, new Vector2(0f, -40f * dt), Radius);
                position = result.Position;
            }

            Assert.False(IsPenetrating(set, position));
            Assert.True(position.Y >= LonghouseLayout.RearZ - 1e-3f);
            results[r++] = (fps, position);
        }

        for (var i = 1; i < results.Length; i++)
        {
            Assert.True(
                MathF.Abs(results[0].Position.Y - results[i].Position.Y) < 0.01f,
                $"wall stop diverged between {results[0].Fps} and {results[i].Fps} Hz: {results[0].Position.Y:F4} vs {results[i].Position.Y:F4}");
        }
    }

    [Fact]
    public void HighSpeedDoorwayPassageIsFrameRateIndependent()
    {
        var set = LonghouseCollisions();
        var results = new (int Fps, Vector2 Position)[3];
        var r = 0;

        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1f / fps;
            var position = new Vector2(0f, LonghouseLayout.SpawnPosition.Z);
            var constrained = false;
            for (var i = 0; i < fps * 4; i++)
            {
                var result = PlayerCollisionResolver.Resolve(set, position, new Vector2(0f, -40f * dt), Radius);
                position = result.Position;

                // Only the run from the door plane down to just in front of the
                // hearth must be unconstrained; past that the solid hearth is
                // expected to stop her.
                const float clearOfHearthZ = 1f;
                if (position.Y > clearOfHearthZ)
                {
                    constrained |= result.Constrained;
                }
            }

            Assert.False(constrained, $"doorway was blocked at {fps} Hz");
            results[r++] = (fps, position);
        }

        for (var i = 1; i < results.Length; i++)
        {
            Assert.True(MathF.Abs(results[0].Position.Y - results[i].Position.Y) < 0.01f);
        }
    }

    [Fact]
    public void GameplayHearthStopIsFrameRateIndependent()
    {
        var results = new (int Fps, float Z)[3];
        var r = 0;

        foreach (var fps in new[] { 30, 60, 144 })
        {
            var dt = 1.0 / fps;
            var session = SessionWithClips();
            for (var i = 1; i <= fps * 12; i++)
            {
                session.Update(new FrameTime(i * dt, dt), Move(forward: true, sprint: true));
            }

            Assert.False(IsPenetrating(session.PlayerCollisions, session.ErikaPosition));
            results[r++] = (fps, session.ErikaPosition.Z);
        }

        for (var i = 1; i < results.Length; i++)
        {
            Assert.True(
                MathF.Abs(results[0].Z - results[i].Z) < 0.02f,
                $"final hearth position diverged between {results[0].Fps} and {results[i].Fps} Hz: {results[0].Z:F4} vs {results[i].Z:F4}");
        }
    }

    // --- determinism ------------------------------------------------------

    [Fact]
    public void RepeatedResolveIsIdentical()
    {
        var set = LonghouseCollisions();

        var first = Resolve(set, 0.2f, 12, 0.1f, -8);
        var second = Resolve(set, 0.2f, 12, 0.1f, -8);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Delta, second.Delta);
        Assert.Equal(first.Constrained, second.Constrained);
        Assert.Equal(first.HitCount, second.HitCount);
        Assert.Equal(first.LastHitName, second.LastHitName);
    }

    [Fact]
    public void ResolverColliderOrderDoesNotChangeNearestWallResult()
    {
        var forward = Set(
            Box("Near", 0, 0, 5, 0.125f),
            Box("Far", 0, -3, 5, 0.125f));
        var reversed = Set(
            Box("Far", 0, -3, 5, 0.125f),
            Box("Near", 0, 0, 5, 0.125f));

        var first = Resolve(forward, 0, 5, 0, -10);
        var second = Resolve(reversed, 0, 5, 0, -10);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Delta, second.Delta);
        Assert.Equal(first.LastHitName, second.LastHitName);
    }
}
