using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2O interior-furnishing collision. Extends the Phase 2N flat-XZ
/// player-collision set to the longhouse hearth, both long benches, and both
/// tables using their rendered <see cref="LonghouseLayout"/> footprints, and
/// verifies the interior stays navigable (doorway, central aisle, both lateral
/// hearth bypasses, rear interior). Covers geometry, effective route widths,
/// head-on/diagonal/corner behavior, clear-aisle traversal, high-speed sweeps,
/// starting overlaps, collider ordering, gameplay integration through
/// <see cref="GameSession"/>, and 30/60/144 Hz frame-rate behavior. The
/// collision architecture itself (circle, skin, sweep-and-slide, resolver) is
/// reused unchanged.
/// </summary>
public sealed class FurnishingCollisionTests
{
    private const float Radius = PlayerCollisionPolicy.PlayerCollisionRadiusMeters;
    private const float Skin = PlayerCollisionPolicy.PlayerCollisionSkinMeters;
    private const float PenetrationTolerance = 1e-3f;

    private static PlayerCollisionSet Set() => EnvironmentFactory.CreatePlayerCollisionSet();

    private static PlayerCollisionResult Resolve(PlayerCollisionSet set, Vector2 start, Vector2 delta) =>
        PlayerCollisionResolver.Resolve(set, start, delta, Radius);

    private static bool IsPenetrating(PlayerCollisionSet set, Vector2 position) =>
        set.ContainsPoint(position, Radius - PenetrationTolerance);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static PlayerCollisionBox Box(string name) =>
        Set().Boxes.Single(b => b.Name == name);

    // --- layout-derived reference values ---------------------------------

    private static float HearthFrontZ => LonghouseLayout.HearthCenterZ + LonghouseLayout.HearthLength / 2f;

    private static float HearthRearZ => LonghouseLayout.HearthCenterZ - LonghouseLayout.HearthLength / 2f;

    private static float BenchInnerX => LonghouseLayout.BenchCenterX - LonghouseLayout.BenchDepth / 2f;

    private static float TableInnerX => LonghouseLayout.TableCenterX - LonghouseLayout.TableWidth / 2f;

    /// <summary>World X of the left bench's inner (aisle-facing) surface.</summary>
    private static float BenchInnerEdgeX => -LonghouseLayout.BenchCenterX + LonghouseLayout.BenchDepth / 2f;

    /// <summary>Player-center X when resting against the left bench.</summary>
    private static float BenchStopX => BenchInnerEdgeX + Radius + Skin;

    /// <summary>World X of the left table's inner (aisle-facing) surface.</summary>
    private static float TableInnerEdgeX => -LonghouseLayout.TableCenterX + LonghouseLayout.TableWidth / 2f;

    /// <summary>Player-center X when resting against the left table.</summary>
    private static float TableStopX => TableInnerEdgeX + Radius + Skin;

    // --- geometry: hearth ------------------------------------------------

    [Fact]
    public void HearthColliderMatchesLayoutFootprint()
    {
        var box = Box("Hearth");

        Assert.Equal(LonghouseLayout.Origin.X, box.CenterXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.HearthCenterZ, box.CenterXZ.Y, precision: 5);
        Assert.Equal(LonghouseLayout.HearthWidth / 2f, box.HalfExtentsXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.HearthLength / 2f, box.HalfExtentsXZ.Y, precision: 5);
        Assert.Equal(0f, box.YawRadians, precision: 6);
        Assert.True(IsFinite(box.CenterXZ));
    }

    [Fact]
    public void HearthColliderCenterMatchesRenderedHearthCenter()
    {
        var scene = EnvironmentFactory.Create();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var found = false;
        foreach (var sceneObject in scene.Objects.Where(o => o.Name.StartsWith("Hearth.", StringComparison.Ordinal)))
        {
            found = true;
            foreach (var vertex in sceneObject.Mesh.Vertices)
            {
                var point = Vector3.Transform(vertex.Position, sceneObject.Transform.WorldMatrix);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
        }

        Assert.True(found, "no rendered hearth geometry found");
        var box = Box("Hearth");
        Assert.Equal((min.X + max.X) / 2f, box.CenterXZ.X, precision: 4);
        Assert.Equal((min.Z + max.Z) / 2f, box.CenterXZ.Y, precision: 4);
    }

    [Fact]
    public void HearthColliderIsInsideLonghouseAndClearOfDoorway()
    {
        var box = Box("Hearth");

        Assert.True(MathF.Abs(box.CenterXZ.X) + box.HalfExtentsXZ.X <= LonghouseLayout.HalfWidth);
        Assert.True(MathF.Abs(box.CenterXZ.Y) + box.HalfExtentsXZ.Y <= LonghouseLayout.HalfLength);
        Assert.True(HearthFrontZ + Radius < LonghouseLayout.FrontZ,
            "hearth collider reaches the doorway plane");
    }

    // --- geometry: benches -----------------------------------------------

    [Fact]
    public void BenchCollidersMatchLayoutAndAreSymmetric()
    {
        var left = Box("Bench.Left");
        var right = Box("Bench.Right");

        Assert.Equal(-LonghouseLayout.BenchCenterX, left.CenterXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.BenchCenterX, right.CenterXZ.X, precision: 5);
        Assert.Equal(0f, left.CenterXZ.Y, precision: 5);
        Assert.Equal(0f, right.CenterXZ.Y, precision: 5);
        Assert.Equal(LonghouseLayout.BenchDepth / 2f, left.HalfExtentsXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.BenchLength / 2f, left.HalfExtentsXZ.Y, precision: 5);
        Assert.Equal(left.HalfExtentsXZ, right.HalfExtentsXZ);
        Assert.Equal(0f, left.YawRadians, precision: 6);
        Assert.Equal(0f, right.YawRadians, precision: 6);
        Assert.Equal(left.CenterXZ.Y, right.CenterXZ.Y, precision: 6);
        Assert.Equal(-left.CenterXZ.X, right.CenterXZ.X, precision: 6);
        Assert.True(IsFinite(left.CenterXZ) && IsFinite(right.CenterXZ));
    }

    [Fact]
    public void BenchCollidersAreInsideLonghouse()
    {
        var box = Box("Bench.Left");
        Assert.True(LonghouseLayout.IsInsideFootprint(new Vector3(box.CenterXZ.X, 0f, box.CenterXZ.Y)));
        Assert.True(MathF.Abs(box.CenterXZ.X) + box.HalfExtentsXZ.X <= LonghouseLayout.HalfWidth);
        Assert.True(box.HalfExtentsXZ.Y <= LonghouseLayout.HalfLength);
    }

    // --- geometry: tables ------------------------------------------------

    [Fact]
    public void TableCollidersMatchLayoutAndAreSymmetric()
    {
        var left = Box("Table.Left");
        var right = Box("Table.Right");

        Assert.Equal(-LonghouseLayout.TableCenterX, left.CenterXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.TableCenterX, right.CenterXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.TableCenterZ, left.CenterXZ.Y, precision: 5);
        Assert.Equal(LonghouseLayout.TableCenterZ, right.CenterXZ.Y, precision: 5);
        Assert.Equal(LonghouseLayout.TableWidth / 2f, left.HalfExtentsXZ.X, precision: 5);
        Assert.Equal(LonghouseLayout.TableLength / 2f, left.HalfExtentsXZ.Y, precision: 5);
        Assert.Equal(left.HalfExtentsXZ, right.HalfExtentsXZ);
        Assert.Equal(0f, left.YawRadians, precision: 6);
        Assert.Equal(0f, right.YawRadians, precision: 6);
        Assert.Equal(-left.CenterXZ.X, right.CenterXZ.X, precision: 6);
        Assert.Equal(left.CenterXZ.Y, right.CenterXZ.Y, precision: 6);
        Assert.True(IsFinite(left.CenterXZ) && IsFinite(right.CenterXZ));
    }

    [Fact]
    public void TableCollidersAreInsideLonghouse()
    {
        var box = Box("Table.Left");
        Assert.True(LonghouseLayout.IsInsideFootprint(new Vector3(box.CenterXZ.X, 0f, box.CenterXZ.Y)));
        Assert.True(MathF.Abs(box.CenterXZ.X) + box.HalfExtentsXZ.X <= LonghouseLayout.HalfWidth);
        Assert.True(MathF.Abs(box.CenterXZ.Y) + box.HalfExtentsXZ.Y <= LonghouseLayout.HalfLength);
    }

    [Fact]
    public void FurnishingCollidersMatchSceneGeometry()
    {
        var scene = EnvironmentFactory.Create();
        var set = Set();

        foreach (var name in new[] { "Bench.Left", "Bench.Right", "Table.Left", "Table.Right" })
        {
            var sceneObject = scene.Objects.Single(o => o.Name == name);
            var box = set.Boxes.Single(b => b.Name == name);

            Assert.Equal(sceneObject.Transform.Position.X, box.CenterXZ.X, precision: 5);
            Assert.Equal(sceneObject.Transform.Position.Z, box.CenterXZ.Y, precision: 5);
            Assert.Equal(sceneObject.Transform.Scale.X / 2f, box.HalfExtentsXZ.X, precision: 5);
            Assert.Equal(sceneObject.Transform.Scale.Z / 2f, box.HalfExtentsXZ.Y, precision: 5);
            Assert.Equal(0f, box.YawRadians, precision: 6);
        }
    }

    // --- geometry: set composition ---------------------------------------

    [Fact]
    public void CollisionSetKeepsShellTreesAndAddsFurnishings()
    {
        var set = Set();

        Assert.Equal(34, set.Count);

        foreach (var name in new[]
                 {
                     "Wall.Left", "Wall.Right", "Wall.Rear", "Wall.Front.Left", "Wall.Front.Right",
                     "Hearth", "Bench.Left", "Bench.Right", "Table.Left", "Table.Right",
                 })
        {
            Assert.Contains(set.Boxes, b => b.Name == name);
        }

        Assert.Equal(LonghouseLayout.TreeCount, set.Boxes.Count(b => b.Name.EndsWith(".Trunk", StringComparison.Ordinal)));
    }

    [Fact]
    public void PlayerCollisionSetCreationIsDeterministic()
    {
        var first = Set();
        var second = Set();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first.Boxes[i], second.Boxes[i]);
        }
    }

    // --- navigation widths -----------------------------------------------

    [Fact]
    public void IntendedPassagesAreWiderThanPlayerDiameter()
    {
        var doorway = LonghouseLayout.DoorWidth - 2f * Radius;
        var centralAisle = 2f * TableInnerX - 2f * Radius;
        var hearthBypass = BenchInnerX - LonghouseLayout.HearthWidth / 2f - 2f * Radius;

        // The diagonal gap between the hearth's front corner and the table's
        // rear-inner corner is the true entry bottleneck into each side corridor.
        var cornerGap = Vector2.Distance(
            new Vector2(-LonghouseLayout.HearthWidth / 2f, HearthFrontZ),
            new Vector2(-TableInnerX, LonghouseLayout.TableCenterZ - LonghouseLayout.TableLength / 2f));
        var diagonalEntry = cornerGap - 2f * Radius;

        Assert.True(doorway > 2f * Skin, $"doorway clearance too small: {doorway:F3}");
        Assert.True(centralAisle > 0.5f, $"central aisle too narrow: {centralAisle:F3}");
        Assert.True(hearthBypass > 0.5f, $"hearth bypass too narrow: {hearthBypass:F3}");
        Assert.True(diagonalEntry > 2f * Skin, $"hearth-bypass entry too narrow: {diagonalEntry:F3}");

        Assert.Equal(0.60f, doorway, precision: 4);
        Assert.Equal(1.90f, centralAisle, precision: 4);
        Assert.Equal(0.85f, hearthBypass, precision: 4);
        Assert.InRange(diagonalEntry, 0.30f, 0.36f);
    }

    // --- clear routes ----------------------------------------------------

    [Fact]
    public void DoorwayAndCentralAisleToHearthIsClear()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, 10f), new Vector2(0f, -8f));

        Assert.False(result.Constrained);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void CentralAisleBetweenTablesIsClear()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, 5f), new Vector2(0f, -4f));

        Assert.False(result.Constrained);
        Assert.Equal(1f, result.Position.Y, precision: 4);
    }

    [Fact]
    public void LeftHearthBypassCorridorIsClear()
    {
        var set = Set();

        // Start just below the table's expanded rear edge (z = 0.95) and beside
        // the hearth, then walk straight back past the hearth's rear end.
        var result = Resolve(set, new Vector2(-1.4f, 0.8f), new Vector2(0f, -4.5f));

        Assert.False(result.Constrained);
        Assert.Equal(-3.7f, result.Position.Y, precision: 3);
        Assert.True(result.Position.Y < HearthRearZ, "did not pass behind the hearth");
    }

    [Fact]
    public void RightHearthBypassCorridorIsClear()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(1.4f, 0.8f), new Vector2(0f, -4.5f));

        Assert.False(result.Constrained);
        Assert.Equal(-3.7f, result.Position.Y, precision: 3);
        Assert.True(result.Position.Y < HearthRearZ, "did not pass behind the hearth");
    }

    [Fact]
    public void LeftHearthBypassEntryDiagonalIsTraversable()
    {
        var set = Set();

        // Weave from the central aisle, past the table's rear-inner corner, into
        // the left hearth corridor. This is the narrowest intended interior route.
        var result = Resolve(set, new Vector2(0f, 1.0f), new Vector2(-1.4f, -0.2f));

        Assert.False(result.Constrained);
        Assert.False(IsPenetrating(set, result.Position));
        Assert.Equal(-1.4f, result.Position.X, precision: 3);
    }

    [Fact]
    public void RightHearthBypassEntryDiagonalIsTraversable()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, 1.0f), new Vector2(1.4f, -0.2f));

        Assert.False(result.Constrained);
        Assert.False(IsPenetrating(set, result.Position));
        Assert.Equal(1.4f, result.Position.X, precision: 3);
    }

    // --- furnishing collision: head-on -----------------------------------

    [Fact]
    public void HearthLongEdgeBlocksHeadOn()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(-1.5f, -1.5f), new Vector2(3f, 0f));

        Assert.True(result.Constrained);
        Assert.Equal("Hearth", result.LastHitName);
        Assert.Equal(-LonghouseLayout.HearthWidth / 2f - Radius - Skin, result.Position.X, precision: 3);
        Assert.Equal(0f, result.Delta.Y, precision: 5);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void HearthShortEdgeBlocksHeadOn()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, 2f), new Vector2(0f, -3f));

        Assert.True(result.Constrained);
        Assert.Equal("Hearth", result.LastHitName);
        Assert.Equal(HearthFrontZ + Radius + Skin, result.Position.Y, precision: 3);
        Assert.Equal(0f, result.Delta.X, precision: 5);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void BenchBlocksHeadOn()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(-1.5f, 0f), new Vector2(-3f, 0f));

        Assert.True(result.Constrained);
        Assert.Equal("Bench.Left", result.LastHitName);
        Assert.Equal(BenchStopX, result.Position.X, precision: 3);
        Assert.Equal(0f, result.Delta.Y, precision: 5);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void TableBlocksHeadOn()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, LonghouseLayout.TableCenterZ), new Vector2(-3f, 0f));

        Assert.True(result.Constrained);
        Assert.Equal("Table.Left", result.LastHitName);
        Assert.Equal(TableStopX, result.Position.X, precision: 3);
        Assert.Equal(0f, result.Delta.Y, precision: 5);
        Assert.False(IsPenetrating(set, result.Position));
    }

    // --- furnishing collision: diagonal slide ----------------------------

    [Fact]
    public void BenchDiagonalContactSlidesTangentially()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(-1.5f, -4f), new Vector2(-3f, 3f));

        Assert.True(result.Constrained);
        Assert.Equal("Bench.Left", result.LastHitName);
        // The +Z tangential component is fully preserved; only inward -X is lost.
        Assert.Equal(3f, result.Delta.Y, precision: 3);
        Assert.Equal(BenchStopX - (-1.5f), result.Delta.X, precision: 3);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void TableDiagonalContactSlidesTangentially()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, LonghouseLayout.TableCenterZ), new Vector2(-3f, -1f));

        Assert.True(result.Constrained);
        Assert.Equal("Table.Left", result.LastHitName);
        Assert.Equal(-1f, result.Delta.Y, precision: 3);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void HearthDiagonalContactSlidesTangentially()
    {
        var set = Set();

        var result = Resolve(set, new Vector2(0f, 2f), new Vector2(0.5f, -3f));

        Assert.True(result.Constrained);
        Assert.Equal("Hearth", result.LastHitName);
        Assert.Equal(0.5f, result.Delta.X, precision: 3);
        Assert.Equal(HearthFrontZ + Radius + Skin - 2f, result.Delta.Y, precision: 3);
        Assert.False(IsPenetrating(set, result.Position));
    }

    // --- furnishing collision: corners -----------------------------------

    [Fact]
    public void FurnishingCornersAreBoundedAndDeterministic()
    {
        var set = Set();
        var cases = new (Vector2 Start, Vector2 Delta)[]
        {
            (new Vector2(0.3f, 1.5f), new Vector2(-2f, -3f)),   // hearth front-left corner
            (new Vector2(-0.3f, 1.5f), new Vector2(2f, -3f)),   // hearth front-right corner
            (new Vector2(0f, 1.0f), new Vector2(-2f, 1f)),      // table rear-inner corner
            (new Vector2(-1.0f, 6.5f), new Vector2(-3f, 2f)),   // bench front-inner corner
        };

        foreach (var (start, delta) in cases)
        {
            var first = Resolve(set, start, delta);
            var second = Resolve(set, start, delta);

            Assert.Equal(first.Position, second.Position);
            Assert.Equal(first.Delta, second.Delta);
            Assert.True(first.Constrained);
            Assert.InRange(first.HitCount, 1, PlayerCollisionPolicy.MaxSlideIterations);
            Assert.True(IsFinite(first.Position));
            Assert.False(IsPenetrating(set, first.Position));
        }
    }

    // --- high-speed sweeps -----------------------------------------------

    [Fact]
    public void HighSpeedSweepCannotTunnelThroughFurnishings()
    {
        var set = Set();

        var hearth = Resolve(set, new Vector2(0f, 5f), new Vector2(0f, -40f));
        Assert.Equal("Hearth", hearth.LastHitName);
        Assert.True(hearth.Position.Y >= HearthFrontZ, $"tunneled hearth: z={hearth.Position.Y:F3}");

        var bench = Resolve(set, new Vector2(-1.0f, -4f), new Vector2(-40f, 0f));
        Assert.Equal("Bench.Left", bench.LastHitName);
        Assert.True(bench.Position.X >= -BenchInnerX, $"tunneled bench: x={bench.Position.X:F3}");

        var table = Resolve(set, new Vector2(0f, LonghouseLayout.TableCenterZ), new Vector2(-40f, 0f));
        Assert.Equal("Table.Left", table.LastHitName);
        Assert.True(table.Position.X >= -TableInnerX, $"tunneled table: x={table.Position.X:F3}");

        Assert.False(IsPenetrating(set, hearth.Position));
        Assert.False(IsPenetrating(set, bench.Position));
        Assert.False(IsPenetrating(set, table.Position));
    }

    // --- starting overlap ------------------------------------------------

    [Fact]
    public void StartingInsideHearthResolvesSafely()
    {
        var set = Set();

        var result = PlayerCollisionResolver.Resolve(
            set, new Vector2(0f, LonghouseLayout.HearthCenterZ), Vector2.Zero, Radius);

        Assert.True(result.Constrained);
        Assert.True(IsFinite(result.Position));
        Assert.True(result.Delta.Length() <= PlayerCollisionPolicy.MaxDepenetrationDistanceMeters + 1e-4f);
        Assert.False(IsPenetrating(set, result.Position));
        Assert.True(result.Position.X > 0.9f, $"did not exit the hearth: x={result.Position.X:F3}");
    }

    [Fact]
    public void StartingInsideTableResolvesSafely()
    {
        var set = Set();

        var result = PlayerCollisionResolver.Resolve(
            set, new Vector2(-LonghouseLayout.TableCenterX, LonghouseLayout.TableCenterZ), Vector2.Zero, Radius);

        Assert.True(result.Constrained);
        Assert.True(IsFinite(result.Position));
        Assert.True(result.Delta.Length() <= PlayerCollisionPolicy.MaxDepenetrationDistanceMeters + 1e-4f);
        Assert.False(IsPenetrating(set, result.Position));
    }

    [Fact]
    public void StartingInsideBenchResolvesSafely()
    {
        var set = Set();

        var result = PlayerCollisionResolver.Resolve(
            set, new Vector2(-LonghouseLayout.BenchCenterX, 0f), Vector2.Zero, Radius);

        Assert.True(result.Constrained);
        Assert.True(IsFinite(result.Position));
        Assert.True(result.Delta.Length() <= PlayerCollisionPolicy.MaxDepenetrationDistanceMeters + 1e-4f);
        Assert.False(IsPenetrating(set, result.Position));
    }

    // --- collider ordering -----------------------------------------------

    [Fact]
    public void FurnishingResolverIsOrderIndependentForNonTieCases()
    {
        var set = Set();
        var reversedBoxes = (PlayerCollisionBox[])set.Boxes.Clone();
        Array.Reverse(reversedBoxes);
        var reversed = new PlayerCollisionSet(reversedBoxes);

        var forward = Resolve(set, new Vector2(0f, LonghouseLayout.TableCenterZ), new Vector2(-3f, 0f));
        var backward = Resolve(reversed, new Vector2(0f, LonghouseLayout.TableCenterZ), new Vector2(-3f, 0f));

        Assert.Equal(forward.Position, backward.Position);
        Assert.Equal(forward.Delta, backward.Delta);
        Assert.Equal(forward.LastHitName, backward.LastHitName);

        var forwardBench = Resolve(set, new Vector2(-1.5f, 0f), new Vector2(-3f, 0f));
        var backwardBench = Resolve(reversed, new Vector2(-1.5f, 0f), new Vector2(-3f, 0f));

        Assert.Equal(forwardBench.Position, backwardBench.Position);
        Assert.Equal(forwardBench.LastHitName, backwardBench.LastHitName);
    }

    // --- camera obstruction independence ---------------------------------

    [Fact]
    public void CameraObstructionsRemainIndependentOfFurnishings()
    {
        var obstructions = EnvironmentFactory.CreateCameraObstructions();

        Assert.Equal(32, obstructions.Count);
        Assert.DoesNotContain(obstructions.Boxes, b =>
            b.Name.Contains("Hearth", StringComparison.Ordinal) ||
            b.Name.Contains("Bench", StringComparison.Ordinal) ||
            b.Name.Contains("Table", StringComparison.Ordinal));
    }

    // --- frame-rate validation -------------------------------------------

    private static Vector2 RunFrames(PlayerCollisionSet set, Vector2 start, Vector2 totalDelta, int fps)
    {
        var steps = fps;
        var step = totalDelta / steps;
        var position = start;
        for (var i = 0; i < steps; i++)
        {
            position = PlayerCollisionResolver.Resolve(set, position, step, Radius).Position;
        }

        return position;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void HighSpeedRunIntoHearthIsFrameRateIndependent(int fps)
    {
        var set = Set();
        var position = RunFrames(set, new Vector2(0f, 5f), new Vector2(0f, -40f), fps);

        Assert.False(IsPenetrating(set, position));
        Assert.True(position.Y >= HearthFrontZ - 1e-3f, $"tunneled hearth at {fps} Hz: z={position.Y:F3}");
    }

    [Fact]
    public void RepresentativeInteriorRoutesAreFrameRateIndependent()
    {
        var set = Set();
        var routes = new (Vector2 Start, Vector2 Delta)[]
        {
            (new Vector2(0f, 10f), new Vector2(0f, -8f)),      // doorway + central aisle
            (new Vector2(-1.4f, 0.8f), new Vector2(0f, -4.5f)), // left hearth bypass
            (new Vector2(-1.5f, -4f), new Vector2(-3f, 3f)),   // bench diagonal slide
        };

        foreach (var (start, delta) in routes)
        {
            var at30 = RunFrames(set, start, delta, 30);
            var at60 = RunFrames(set, start, delta, 60);
            var at144 = RunFrames(set, start, delta, 144);

            Assert.True(Vector2.Distance(at30, at60) < 0.02f, $"30/60 diverged: {at30} vs {at60}");
            Assert.True(Vector2.Distance(at30, at144) < 0.02f, $"30/144 diverged: {at30} vs {at144}");
            Assert.False(IsPenetrating(set, at30));
            Assert.False(IsPenetrating(set, at60));
            Assert.False(IsPenetrating(set, at144));
        }
    }

    // --- gameplay integration --------------------------------------------

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

    [Fact]
    public void GameplayWalkDownAisleStopsAtHearth()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 900, _ => Move(forward: true));

        Assert.Equal("Hearth", session.LastPlayerCollisionName);
        Assert.True(session.WasPlayerCollisionConstrained);
        Assert.Equal(0f, session.ErikaPosition.X, precision: 3);
        Assert.True(session.ErikaPosition.Z >= HearthFrontZ - 1e-3f);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void GameplayCollidesWithTableThenSlidesAroundIt()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        // Walk down the aisle to the table band, then strafe left into Table.Left.
        timeline.Step(session, 330, _ => Move(forward: true));
        timeline.Step(session, 120, _ => Move(left: true));
        Assert.Equal("Table.Left", session.LastPlayerCollisionName);
        Assert.True(session.ErikaPosition.X < -0.5f);

        // Then forward-left: slide along the table edge and round its rear corner.
        timeline.Step(session, 300, _ => Move(forward: true, left: true));

        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
        Assert.True(session.ErikaPosition.Z < 1.25f, $"did not get past the table: z={session.ErikaPosition.Z:F3}");
    }

    [Fact]
    public void GameplayCollidesWithBenchAndSlidesAlongIt()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        // Walk to the hearth, then forward-left around it and down the left side
        // until the bench stops the lateral motion.
        timeline.Step(session, 900, _ => Move(forward: true));
        timeline.Step(session, 250, _ => Move(forward: true, left: true));

        var xAtBench = session.ErikaPosition.X;
        var zAtBench = session.ErikaPosition.Z;
        Assert.True(MathF.Abs(xAtBench - BenchStopX) < 0.06f,
            $"not settled against the bench: x={xAtBench:F3}");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));

        // Continue forward: the -Z tangential component keeps sliding along the bench.
        timeline.Step(session, 200, _ => Move(forward: true));
        Assert.True(session.ErikaPosition.Z < zAtBench - 1f, "did not slide along the bench");
        Assert.True(MathF.Abs(session.ErikaPosition.X - xAtBench) < 0.06f, "lateral drift along the bench");
    }

    [Fact]
    public void GameplayRunsAroundHearthToTheRear()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 200, _ => Move(forward: true));
        timeline.Step(session, 600, _ => Move(forward: true, left: true));
        timeline.Step(session, 300, _ => Move(forward: true));

        Assert.True(session.ErikaPosition.Z < HearthRearZ,
            $"did not reach behind the hearth: z={session.ErikaPosition.Z:F3}");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
        Assert.True(IsFinite(session.ErikaPosition));
        Assert.True(float.IsFinite(session.ErikaYawRadians));
    }

    [Fact]
    public void GameplayRunsAroundHearthToTheRearOnTheRight()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 200, _ => Move(forward: true));
        timeline.Step(session, 600, _ => Move(forward: true, right: true));
        timeline.Step(session, 300, _ => Move(forward: true));

        Assert.True(session.ErikaPosition.Z < HearthRearZ,
            $"did not reach behind the hearth: z={session.ErikaPosition.Z:F3}");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void GameplayRunIntoHearthIsFiniteAndBounded()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));
        Assert.Equal("Hearth", session.LastPlayerCollisionName);

        for (var i = 0; i < 120; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, sprint: true));

            Assert.True(session.WasPlayerCollisionConstrained);
            Assert.True(IsFinite(session.ErikaPosition));
            Assert.True(float.IsFinite(session.ErikaYawRadians));
            Assert.True(float.IsFinite(session.CurrentMoveSpeedMetersPerSecond));
            Assert.True(float.IsFinite(session.TargetMoveSpeedMetersPerSecond));
            Assert.True(float.IsFinite(session.RootMotionGain));
            Assert.True(float.IsFinite(session.VisualPlaybackRate));
            Assert.True(IsFinite(session.Camera.Position));
            Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        }

        Assert.True(session.ErikaPosition.Z >= HearthFrontZ - 1e-3f);
    }

    [Fact]
    public void GameplayStopAgainstTableThenRestartAway()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 330, _ => Move(forward: true));
        timeline.Step(session, 120, _ => Move(left: true));
        Assert.Equal("Table.Left", session.LastPlayerCollisionName);
        var xAtTable = session.ErikaPosition.X;

        timeline.Step(session, 240, _ => NoInput());
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond, precision: 6);
        Assert.False(session.WasPlayerCollisionConstrained);

        timeline.Step(session, 120, _ => Move(right: true));
        Assert.True(session.ErikaPosition.X > xAtTable + 0.5f, "did not restart away from the table");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void GameplayTurnInPlaceBesideTableKeepsZeroTranslation()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        timeline.Step(session, 330, _ => Move(forward: true));
        timeline.Step(session, 120, _ => Move(left: true));
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

        Assert.True(turned, "test premise: a stationary reversal beside the table should gate translation");
    }

    [Fact]
    public void GameplayRunThroughClearInteriorRouteIsUnconstrained()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        // Sprint down the central aisle; nothing blocks until the hearth.
        timeline.Step(session, 120, _ => Move(forward: true, sprint: true));

        Assert.False(session.WasPlayerCollisionConstrained);
        Assert.True(session.ErikaPosition.Z < LonghouseLayout.FrontZ, "did not enter the longhouse");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }
}
