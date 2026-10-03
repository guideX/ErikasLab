using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2L environment blockout coverage: centralized layout sanity, doorway
/// and spawn placement, interior bounds, forest boundary, primitive geometry
/// well-formedness, deterministic placement, and material differentiation.
/// These tests are pure/portable (no GPU, no MonoGame).
/// </summary>
public sealed class EnvironmentBlockoutTests
{
    // --- helpers ---------------------------------------------------------

    private static (Vector3 Min, Vector3 Max) WorldBounds(SceneObject sceneObject)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in sceneObject.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, sceneObject.Transform.WorldMatrix);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (min, max);
    }

    private static bool ContainsPoint((Vector3 Min, Vector3 Max) bounds, Vector3 point) =>
        point.X >= bounds.Min.X && point.X <= bounds.Max.X &&
        point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y &&
        point.Z >= bounds.Min.Z && point.Z <= bounds.Max.Z;

    private static IEnumerable<SceneObject> Solids(Scene scene) =>
        scene.Objects.Where(o => WorldBounds(o).Max.Y - WorldBounds(o).Min.Y > 0.1f);

    private static IEnumerable<SceneObject> WithPrefix(Scene scene, string prefix) =>
        scene.Objects.Where(o => o.Name.StartsWith(prefix, StringComparison.Ordinal));

    // --- layout ----------------------------------------------------------

    [Fact]
    public void LayoutDimensionsArePositiveAndFinite()
    {
        Assert.True(LonghouseLayout.Length > 0f && float.IsFinite(LonghouseLayout.Length));
        Assert.True(LonghouseLayout.Width > 0f && float.IsFinite(LonghouseLayout.Width));
        Assert.True(LonghouseLayout.WallHeight > 0f && float.IsFinite(LonghouseLayout.WallHeight));
        Assert.True(LonghouseLayout.RidgeHeight > 0f && float.IsFinite(LonghouseLayout.RidgeHeight));
        Assert.True(LonghouseLayout.DoorWidth > 0f && float.IsFinite(LonghouseLayout.DoorWidth));
        Assert.True(LonghouseLayout.DoorHeight > 0f && float.IsFinite(LonghouseLayout.DoorHeight));
        Assert.True(LonghouseLayout.HearthLength > 0f && float.IsFinite(LonghouseLayout.HearthLength));
        Assert.True(LonghouseLayout.HearthWidth > 0f && float.IsFinite(LonghouseLayout.HearthWidth));
        Assert.True(LonghouseLayout.ClearingSize > 0f && float.IsFinite(LonghouseLayout.ClearingSize));
        Assert.True(LonghouseLayout.TreeRingRadius > 0f && float.IsFinite(LonghouseLayout.TreeRingRadius));
        Assert.True(LonghouseLayout.TreeCount > 0);
    }

    [Fact]
    public void RidgeExceedsWallHeight()
    {
        Assert.True(LonghouseLayout.RidgeHeight > LonghouseLayout.WallHeight);
        Assert.True(LonghouseLayout.RoofRise > 0f);
        Assert.True(float.IsFinite(LonghouseLayout.RoofSlopeAngleRadians));
        Assert.True(float.IsFinite(LonghouseLayout.RoofSlopeLength));
    }

    [Fact]
    public void DoorFitsWithinFrontWall()
    {
        Assert.True(LonghouseLayout.DoorWidth < LonghouseLayout.Width - 2f * LonghouseLayout.WallThickness);
        Assert.True(LonghouseLayout.DoorHeight < LonghouseLayout.WallHeight);
        var segmentWidth = LonghouseLayout.HalfWidth - LonghouseLayout.DoorWidth / 2f;
        Assert.True(segmentWidth > 0f);
        var lintelHeight = LonghouseLayout.WallHeight - LonghouseLayout.DoorHeight;
        Assert.True(lintelHeight > 0f);
    }

    [Fact]
    public void DoorwayClearsErika()
    {
        Assert.True(LonghouseLayout.DoorHeight > ErikaFigure.TargetHeightMeters);
        Assert.True(LonghouseLayout.DoorWidth > 0.6f);
    }

    [Fact]
    public void HearthLiesInsideFootprint()
    {
        Assert.True(MathF.Abs(LonghouseLayout.HearthCenterZ) + LonghouseLayout.HearthLength / 2f <= LonghouseLayout.HalfLength);
        Assert.True(LonghouseLayout.HearthWidth / 2f <= LonghouseLayout.HalfWidth);
    }

    [Fact]
    public void BenchesAndTablesLieInsideFootprint()
    {
        var benchX = LonghouseLayout.HalfWidth - LonghouseLayout.BenchInset - LonghouseLayout.BenchDepth / 2f;
        Assert.True(benchX + LonghouseLayout.BenchDepth / 2f <= LonghouseLayout.HalfWidth);
        Assert.True(LonghouseLayout.BenchLength / 2f <= LonghouseLayout.HalfLength);
        Assert.True(1.65f + LonghouseLayout.TableWidth / 2f <= LonghouseLayout.HalfWidth);
        Assert.True(3f + LonghouseLayout.TableLength / 2f <= LonghouseLayout.HalfLength);
    }

    // --- spawn -----------------------------------------------------------

    [Fact]
    public void SpawnIsOutsideFootprintAndOnTheGround()
    {
        Assert.False(LonghouseLayout.IsInsideFootprint(LonghouseLayout.SpawnPosition));
        Assert.Equal(0f, LonghouseLayout.SpawnPosition.Y, precision: 6);
        Assert.True(LonghouseLayout.SpawnPosition.Z > LonghouseLayout.FrontZ);
    }

    [Fact]
    public void SpawnIsSensibleDistanceFromDoorway()
    {
        var doorCenter = new Vector3(0f, 0f, LonghouseLayout.FrontZ);
        var distance = Vector3.Distance(LonghouseLayout.SpawnPosition, doorCenter);
        Assert.True(distance > 2f);
        Assert.True(distance < 8f);
    }

    [Fact]
    public void SpawnColumnDoesNotLieInsideAnySolid()
    {
        var scene = EnvironmentFactory.Create();
        var solids = Solids(scene).Select(o => (Object: o, Bounds: WorldBounds(o))).ToList();
        Assert.NotEmpty(solids);

        for (var y = 0f; y <= ErikaFigure.TargetHeightMeters; y += 0.1f)
        {
            var sample = new Vector3(LonghouseLayout.SpawnPosition.X, y, LonghouseLayout.SpawnPosition.Z);
            foreach (var (sceneObject, bounds) in solids)
            {
                Assert.False(
                    ContainsPoint(bounds, sample),
                    $"Spawn column intersects '{sceneObject.Name}' at y={y:F2}");
            }
        }
    }

    [Fact]
    public void SpawnFacingPointsAtDoorway()
    {
        Assert.True(float.IsFinite(LonghouseLayout.SpawnFacingYawRadians));
        // Locomotion convention: yaw = atan2(direction.X, direction.Z), so the
        // model forward is (sin(yaw), 0, cos(yaw)).
        var forward = new Vector3(
            MathF.Sin(LonghouseLayout.SpawnFacingYawRadians),
            0f,
            MathF.Cos(LonghouseLayout.SpawnFacingYawRadians));
        Assert.True(forward.Z < -0.99f); // facing -Z, toward the front doorway
    }

    // --- forest boundary -------------------------------------------------

    [Fact]
    public void ForestBoundaryIsOutsideLonghouseFootprint()
    {
        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var position = LonghouseLayout.TreePosition(i);
            Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Z));
            Assert.False(LonghouseLayout.IsInsideFootprint(position, margin: 1f));
            Assert.True(position.Length() > LonghouseLayout.HalfLength + 1f);
        }
    }

    [Fact]
    public void TreePositionsAreDeterministic()
    {
        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            Assert.Equal(LonghouseLayout.TreePosition(i), LonghouseLayout.TreePosition(i));
        }
    }

    // --- scene composition ----------------------------------------------

    [Fact]
    public void SceneContainsExpectedEnvironmentCategories()
    {
        var scene = EnvironmentFactory.Create();

        Assert.Single(WithPrefix(scene, "ForestGround"));
        Assert.Single(WithPrefix(scene, "Clearing"));
        Assert.Single(WithPrefix(scene, "LonghouseFloor"));
        Assert.Equal(2, WithPrefix(scene, "Gable.").Count());
        Assert.Equal(2, WithPrefix(scene, "Roof.").Count(o => !o.Name.StartsWith("Roof.Ridge", StringComparison.Ordinal)));
        Assert.Single(WithPrefix(scene, "Roof.Ridge"));
        Assert.True(WithPrefix(scene, "Wall.").Count() >= 6);
        Assert.True(WithPrefix(scene, "Post.").Count() >= 4 + 2 * LonghouseLayout.SidePostCount);
        Assert.Equal(5, WithPrefix(scene, "Hearth.").Count());
        Assert.Equal(2, WithPrefix(scene, "Bench.").Count());
        Assert.Equal(2, WithPrefix(scene, "Table.").Count());
        Assert.Equal(LonghouseLayout.TreeCount * 4, WithPrefix(scene, "Tree.").Count());
        Assert.Equal(LonghouseLayout.TreeCount, WithPrefix(scene, "Tree.").Count(o => o.Name.EndsWith(".Trunk", StringComparison.Ordinal)));
    }

    [Fact]
    public void RepeatedPostsUseDeterministicSpacing()
    {
        var scene = EnvironmentFactory.Create();
        var leftPosts = WithPrefix(scene, "Post.Side.Left.")
            .Select(o => o.Transform.Position.Z)
            .OrderBy(z => z)
            .ToArray();

        Assert.Equal(LonghouseLayout.SidePostCount, leftPosts.Length);
        for (var i = 1; i < leftPosts.Length; i++)
        {
            Assert.Equal(LonghouseLayout.PostSpacing, leftPosts[i] - leftPosts[i - 1], precision: 4);
        }
    }

    [Fact]
    public void EnvironmentCreationIsDeterministic()
    {
        var first = EnvironmentFactory.Create();
        var second = EnvironmentFactory.Create();

        Assert.Equal(first.Objects.Count, second.Objects.Count);
        for (var i = 0; i < first.Objects.Count; i++)
        {
            Assert.Equal(first.Objects[i].Name, second.Objects[i].Name);
            Assert.Equal(first.Objects[i].Transform, second.Objects[i].Transform);
        }
    }

    // --- geometry --------------------------------------------------------

    [Fact]
    public void BoxMeshHasExpectedVertexAndIndexCounts()
    {
        var mesh = MeshFactory.CreateBox(new Vector3(2f, 3f, 4f), ColorRgba.Timber);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        AssertWellFormed(mesh);
    }

    [Fact]
    public void TriangularPrismHasExpectedVertexAndIndexCounts()
    {
        var mesh = MeshFactory.CreateTriangularPrism(new Vector3(7f, 2.4f, 0.25f), ColorRgba.WallWood);
        Assert.Equal(18, mesh.Vertices.Count);
        Assert.Equal(24, mesh.Indices.Count);
        AssertWellFormed(mesh);
    }

    [Fact]
    public void GroundRectangleHasExpectedVertexAndIndexCounts()
    {
        var mesh = MeshFactory.CreateGroundRectangle(5.5f, 15.5f, ColorRgba.FloorWood);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(6, mesh.Indices.Count);
        AssertWellFormed(mesh);
    }

    [Fact]
    public void AllSceneMeshesAreWellFormedAndFinite()
    {
        var scene = EnvironmentFactory.Create();
        Assert.NotEmpty(scene.Objects);
        foreach (var sceneObject in scene.Objects)
        {
            AssertWellFormed(sceneObject.Mesh);
        }
    }

    [Fact]
    public void AllEnvironmentTransformsAreFiniteAndReasonable()
    {
        var scene = EnvironmentFactory.Create();
        foreach (var sceneObject in scene.Objects)
        {
            var transform = sceneObject.Transform;
            Assert.True(IsFinite(transform.Position), $"{sceneObject.Name} position non-finite");
            Assert.True(IsFinite(transform.Scale), $"{sceneObject.Name} scale non-finite");
            Assert.True(transform.Scale.X > 0f && transform.Scale.Y > 0f && transform.Scale.Z > 0f, $"{sceneObject.Name} non-positive scale");
            Assert.True(transform.Position.Length() < 1000f, $"{sceneObject.Name} kilometer-scale position");
        }
    }

    [Fact]
    public void RoofSitsAboveTheWallsAndBelowTheRidgeEnvelope()
    {
        var scene = EnvironmentFactory.Create();
        foreach (var roof in WithPrefix(scene, "Roof."))
        {
            var (min, max) = WorldBounds(roof);
            Assert.True(float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z));
            Assert.True(min.Y >= LonghouseLayout.WallHeight - 0.3f, $"{roof.Name} dips below the eave");
            Assert.True(max.Y <= LonghouseLayout.RidgeHeight + 0.3f, $"{roof.Name} rises past the ridge envelope");
        }
    }

    [Fact]
    public void MaterialsAreDifferentiated()
    {
        var scene = EnvironmentFactory.Create();
        var colors = scene.Objects
            .Select(o => o.Mesh.Vertices[0].Color)
            .Distinct()
            .Count();
        Assert.True(colors >= 6, $"Expected several blockout materials, found {colors}");
    }

    // --- integration -----------------------------------------------------

    [Fact]
    public void GameSessionStartsWithEnvironmentSpawnAndNoTestCubes()
    {
        var session = new GameSession();
        Assert.Equal(LonghouseLayout.SpawnPosition, session.ErikaPosition);
        Assert.Equal(ErikaFigure.GroundPosition, session.ErikaPosition);
        Assert.DoesNotContain(session.World.Objects, o => o.Name.Contains("Cube", StringComparison.Ordinal));
        Assert.Contains(session.World.Objects, o => o.Name.StartsWith("Tree.", StringComparison.Ordinal));
        Assert.Single(session.World.Models);
    }

    // --- assertions ------------------------------------------------------

    private static void AssertWellFormed(MeshData mesh)
    {
        Assert.NotEmpty(mesh.Vertices);
        Assert.NotEmpty(mesh.Indices);
        Assert.Equal(0, mesh.Indices.Count % 3);
        foreach (var vertex in mesh.Vertices)
        {
            Assert.True(IsFinite(vertex.Position), "non-finite vertex position");
            Assert.True(IsFinite(vertex.Normal), "non-finite vertex normal");
            Assert.Equal(1f, vertex.Normal.Length(), precision: 4);
        }

        foreach (var index in mesh.Indices)
        {
            Assert.InRange(index, 0, mesh.Vertices.Count - 1);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
