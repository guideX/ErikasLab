using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2S UV/texture-architecture coverage: deterministic primitive UV
/// generation, world-repeat UV scaling, texture-scale validity, the
/// category-to-texture-id mapping, the missing/unknown-texture fallback
/// contract, unchanged Phase 2R fallback base colors, and mesh/index validity.
/// Pure/portable (no GPU, no MonoGame).
/// </summary>
public sealed class TextureUvTests
{
    // --- helpers ---------------------------------------------------------

    private static IEnumerable<SceneObject> WithPrefix(Scene scene, string prefix) =>
        scene.Objects.Where(o => o.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool AllFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    // --- UV generation -----------------------------------------------------

    [Fact]
    public void PrimitiveUvsAreFinite()
    {
        var box = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        var prism = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);
        var ground = MeshFactory.CreateGroundPlane(10f, ColorRgba.Ground);

        Assert.All(box.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));
        Assert.All(prism.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));
        Assert.All(ground.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));
    }

    [Fact]
    public void PrimitiveUvGenerationIsDeterministic()
    {
        var first = MeshFactory.CreateBox(new Vector3(2f, 3f, 4f), ColorRgba.Timber);
        var second = MeshFactory.CreateBox(new Vector3(2f, 3f, 4f), ColorRgba.Timber);
        Assert.Equal(
            first.Vertices.Select(v => v.TexCoord),
            second.Vertices.Select(v => v.TexCoord));

        var prismFirst = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);
        var prismSecond = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);
        Assert.Equal(
            prismFirst.Vertices.Select(v => v.TexCoord),
            prismSecond.Vertices.Select(v => v.TexCoord));

        var groundFirst = MeshFactory.CreateGroundPlane(36f, ColorRgba.Ground);
        var groundSecond = MeshFactory.CreateGroundPlane(36f, ColorRgba.Ground);
        Assert.Equal(
            groundFirst.Vertices.Select(v => v.TexCoord),
            groundSecond.Vertices.Select(v => v.TexCoord));
    }

    [Fact]
    public void BoxFacesReceiveValidUnitCoordinates()
    {
        var box = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        Assert.Equal(24, box.Vertices.Count);

        // Six quad faces of four vertices each; every face spans [0,1]² with a
        // full 0→1 run on both axes (deterministic planar per-face mapping).
        for (var face = 0; face < 6; face++)
        {
            var faceUvs = box.Vertices.Skip(face * 4).Take(4).Select(v => v.TexCoord).ToArray();
            Assert.All(faceUvs, uv => Assert.InRange(uv.X, 0f, 1f));
            Assert.All(faceUvs, uv => Assert.InRange(uv.Y, 0f, 1f));
            Assert.Contains(faceUvs, uv => uv.X == 0f);
            Assert.Contains(faceUvs, uv => uv.X == 1f);
            Assert.Contains(faceUvs, uv => uv.Y == 0f);
            Assert.Contains(faceUvs, uv => uv.Y == 1f);
        }
    }

    [Fact]
    public void BoxSideFacesShareVerticalUvOrientation()
    {
        // The four side faces (indices 0-15) must all run v along local Y, so
        // axis-aligned instances (every wall in the scene) share one
        // world-vertical texture orientation.
        var box = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        for (var face = 0; face < 4; face++)
        {
            var faceVertices = box.Vertices.Skip(face * 4).Take(4).ToArray();
            var vValues = faceVertices.Select(v => v.TexCoord.Y).ToArray();
            var yValues = faceVertices.Select(v => v.Position.Y).ToArray();

            // v increases exactly when local Y increases (same ordering).
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++)
                {
                    Assert.True(
                        (yValues[i] < yValues[j]) == (vValues[i] < vValues[j]) ||
                        yValues[i] == yValues[j],
                        "side-face v must track local Y");
                }
            }
        }
    }

    [Fact]
    public void PrismUvsAreFiniteAndDeterministic()
    {
        var prism = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);
        Assert.Equal(18, prism.Vertices.Count);
        Assert.All(prism.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));

        // Caps are triangles (3 vertices each); the remaining faces are quads.
        var cap = prism.Vertices.Take(3).Select(v => v.TexCoord).ToArray();
        Assert.Equal(new Vector2(0f, 0f), cap[0]);
        Assert.Equal(new Vector2(1f, 0f), cap[1]);
        Assert.Equal(new Vector2(0.5f, 1f), cap[2]);
    }

    [Fact]
    public void GroundUvsAreInLocalMeters()
    {
        var ground = MeshFactory.CreateGroundPlane(10f, ColorRgba.Ground);
        var uvs = ground.Vertices.Select(v => v.TexCoord).ToArray();
        var positions = ground.Vertices.Select(v => v.Position).ToArray();

        // u = x, v = z baked in local meters (negative coordinates allowed).
        for (var i = 0; i < uvs.Length; i++)
        {
            Assert.Equal(positions[i].X, uvs[i].X, precision: 5);
            Assert.Equal(positions[i].Z, uvs[i].Y, precision: 5);
        }
    }

    [Fact]
    public void GroundUvTilesBeyondUnitRangeAtRealisticDimensions()
    {
        var clearing = MeshFactory.CreateGroundPlane(LonghouseLayout.ClearingSize, ColorRgba.Ground);
        var forest = MeshFactory.CreateGroundPlane(LonghouseLayout.ForestGroundSize, ColorRgba.Ground);

        // 36 m / 140 m planes must span many texture repeats, never one
        // stretched image: UV magnitude reaches the plane size in meters.
        Assert.Contains(clearing.Vertices, v => MathF.Abs(v.TexCoord.X) > 1f);
        Assert.Contains(clearing.Vertices, v => MathF.Abs(v.TexCoord.Y) > 1f);
        Assert.Contains(forest.Vertices, v => MathF.Abs(v.TexCoord.X) > 10f);
        Assert.Contains(forest.Vertices, v => MathF.Abs(v.TexCoord.Y) > 10f);
    }

    // --- world-repeat UV scaling -------------------------------------------

    [Fact]
    public void ApplyUvScaleProducesWorldRepeatsForWalls()
    {
        var unitBox = MeshFactory.CreateBox(Vector3.One, ColorRgba.WallWood);
        var wallSize = new Vector3(LonghouseLayout.WallThickness, LonghouseLayout.WallHeight, LonghouseLayout.Length);
        var scaled = MeshFactory.ApplyUvScale(unitBox, wallSize, new Vector2(1f, 1f));

        Assert.All(scaled.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));

        // A 16 m wall at 1 repeat/m must reach 16 repeats along its length.
        var maxU = scaled.Vertices.Max(v => v.TexCoord.X);
        var maxV = scaled.Vertices.Max(v => v.TexCoord.Y);
        Assert.True(maxU >= LonghouseLayout.Length - 0.001f, "wall length must visibly repeat");
        Assert.True(maxV >= LonghouseLayout.WallHeight - 0.001f, "wall height must visibly repeat");
    }

    [Fact]
    public void ApplyUvScaleKeepsRoofUvsFiniteAfterPitchedLayoutUse()
    {
        var scene = EnvironmentFactory.Create();
        var roof = Assert.Single(WithPrefix(scene, "Roof.Left"));
        var roofSize = roof.Transform.Scale;

        var scaled = MeshFactory.ApplyUvScale(roof.Mesh, roofSize, EnvironmentMaterials.RoofTurf.TextureScale);
        Assert.All(scaled.Vertices, vertex => Assert.True(AllFinite(vertex.TexCoord)));

        // The slab's top face runs along the slope length: at 1 repeat/m the
        // scaled UVs reach the slope length, not a stretched single image.
        var maxU = scaled.Vertices.Max(v => v.TexCoord.X);
        Assert.True(maxU >= LonghouseLayout.RoofSlopeLength - 0.001f, "roof UVs must tile along the slope");
    }

    [Fact]
    public void ApplyUvScaleScalesGroundByTextureScaleOnly()
    {
        var ground = MeshFactory.CreateGroundPlane(LonghouseLayout.ClearingSize, ColorRgba.Ground);
        var scaled = MeshFactory.ApplyUvScale(ground, Vector3.One, EnvironmentMaterials.ClearingGround.TextureScale);

        // Ground UVs are baked in meters; the identity world scale leaves them
        // untouched and the material scale (0.25 repeats/m) applies once. The
        // plane is centered, so the largest |u| is half the size (18 m).
        var maxAbsU = scaled.Vertices.Max(v => MathF.Abs(v.TexCoord.X));
        Assert.Equal(LonghouseLayout.ClearingSize / 2f * EnvironmentMaterials.ClearingGround.TextureScale.X, maxAbsU, precision: 3);
    }

    [Fact]
    public void ApplyUvScaleIsDeterministicAndShapePreserving()
    {
        var unitBox = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        var first = MeshFactory.ApplyUvScale(unitBox, new Vector3(0.25f, 2.6f, 16f), new Vector2(1f, 1f));
        var second = MeshFactory.ApplyUvScale(unitBox, new Vector3(0.25f, 2.6f, 16f), new Vector2(1f, 1f));

        Assert.Equal(first.Vertices.Count, second.Vertices.Count);
        Assert.Equal(first.Indices, second.Indices);
        for (var i = 0; i < first.Vertices.Count; i++)
        {
            Assert.Equal(first.Vertices[i].Position, second.Vertices[i].Position);
            Assert.Equal(first.Vertices[i].Normal, second.Vertices[i].Normal);
            Assert.Equal(first.Vertices[i].Color, second.Vertices[i].Color);
            Assert.Equal(first.Vertices[i].TexCoord, second.Vertices[i].TexCoord);
        }
    }

    [Fact]
    public void ApplyUvScaleRejectsUnknownPrimitiveShapes()
    {
        var custom = new MeshData(
            [new MeshVertex(Vector3.Zero, Vector3.UnitY, ColorRgba.Ground, Vector2.Zero)],
            [0, 0, 0]);
        Assert.Throws<ArgumentException>(() => MeshFactory.ApplyUvScale(custom, Vector3.One, Vector2.One));
    }

    // --- texture scale values ------------------------------------------------

    [Fact]
    public void TextureScaleValuesAreFiniteAndPositiveWhereTexturesAreUsed()
    {
        foreach (var material in EnvironmentMaterials.All)
        {
            if (!material.TextureEnabled)
            {
                continue;
            }

            Assert.True(float.IsFinite(material.TextureScale.X) && float.IsFinite(material.TextureScale.Y));
            Assert.True(material.TextureScale.X > 0f && material.TextureScale.Y > 0f);
            Assert.True(material.IsValid);
        }
    }

    // --- texture id mapping ----------------------------------------------------

    [Fact]
    public void MaterialCategoryToTextureIdMappingIsDeterministic()
    {
        var expected = new (StaticMaterial Material, string Id)[]
        {
            (EnvironmentMaterials.StructuralTimber, EnvironmentTextureIds.StructuralTimber),
            (EnvironmentMaterials.WallPlanks, EnvironmentTextureIds.WallPlanks),
            (EnvironmentMaterials.RoofTurf, EnvironmentTextureIds.RoofTurf),
            (EnvironmentMaterials.HearthStone, EnvironmentTextureIds.HearthStone),
            (EnvironmentMaterials.InteriorFloor, EnvironmentTextureIds.InteriorFloor),
            (EnvironmentMaterials.ClearingGround, EnvironmentTextureIds.ClearingGround),
            (EnvironmentMaterials.ForestGround, EnvironmentTextureIds.ForestGround),
            (EnvironmentMaterials.Furniture, EnvironmentTextureIds.Furniture),
            (EnvironmentMaterials.TreeTrunk, EnvironmentTextureIds.TreeTrunk),
        };

        foreach (var (material, id) in expected)
        {
            Assert.Equal(id, material.TextureId);
        }

        Assert.Equal(EnvironmentTextureIds.All.Length, EnvironmentTextureIds.All.Distinct().Count());

        // The mapping is stable across scene builds (materials are shared statics).
        var first = EnvironmentFactory.Create();
        var second = EnvironmentFactory.Create();
        for (var i = 0; i < first.Objects.Count; i++)
        {
            Assert.Equal(first.Objects[i].Material, second.Objects[i].Material);
        }
    }

    [Fact]
    public void EveryTexturedCategoryMapsToASceneObject()
    {
        var scene = EnvironmentFactory.Create();
        foreach (var id in EnvironmentTextureIds.All)
        {
            var material = EnvironmentMaterials.All.Single(m => m.TextureId == id);
            Assert.Contains(scene.Objects, o => o.Material.Equals(material));
        }
    }

    // --- fallback contract -------------------------------------------------------

    [Fact]
    public void KnownTextureIdResolvesThroughTheCatalog()
    {
        var catalog = new TextureCatalog();
        var material = new StaticMaterial { TextureId = EnvironmentTextureIds.WallPlanks };

        Assert.True(material.TextureEnabled);
        Assert.False(catalog.CanUseTexture(material));

        catalog.Register(EnvironmentTextureIds.WallPlanks);
        Assert.True(catalog.CanUseTexture(material));
    }

    [Fact]
    public void UnknownTextureIdReturnsFallbackPath()
    {
        var catalog = new TextureCatalog();
        var material = new StaticMaterial
        {
            BaseColor = new ColorRgba(88, 76, 60),
            TextureId = "environment/does_not_exist",
        };

        Assert.True(material.TextureEnabled);
        Assert.False(catalog.CanUseTexture(material), "unknown id must resolve to the base-color fallback");
        Assert.True(material.IsValid, "a missing texture never invalidates the material");
    }

    [Fact]
    public void MissingTextureNeverInvalidatesMaterial()
    {
        var catalog = new TextureCatalog();
        var material = new StaticMaterial
        {
            BaseColor = new ColorRgba(10, 20, 30),
            TextureId = EnvironmentTextureIds.RoofTurf,
        };

        Assert.True(material.IsValid);
        Assert.False(catalog.CanUseTexture(material));
        Assert.True(material.BaseColor == new ColorRgba(10, 20, 30), "fallback base color stays intact");
    }

    [Fact]
    public void BlankTextureIdsAreRejectedByMaterialsAndCatalog()
    {
        var catalog = new TextureCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Register(""));
        Assert.Throws<ArgumentException>(() => catalog.Register("  "));

        var blank = new StaticMaterial { TextureId = " " };
        Assert.False(blank.IsValid, "a blank texture id is not a usable texture request");
    }

    // --- Phase 2R fallback base colors unchanged ---------------------------------

    [Fact]
    public void MaterialFallbackBaseColorsAreUnchangedFromPhase2R()
    {
        Assert.Equal(new ColorRgba(56, 38, 24), EnvironmentMaterials.StructuralTimber.BaseColor);
        Assert.Equal(new ColorRgba(88, 76, 60), EnvironmentMaterials.WallPlanks.BaseColor);
        Assert.Equal(new ColorRgba(44, 48, 32), EnvironmentMaterials.RoofTurf.BaseColor);
        Assert.Equal(new ColorRgba(72, 50, 32), EnvironmentMaterials.InteriorFloor.BaseColor);
        Assert.Equal(new ColorRgba(62, 74, 52), EnvironmentMaterials.ClearingGround.BaseColor);
        Assert.Equal(new ColorRgba(28, 34, 28), EnvironmentMaterials.ForestGround.BaseColor);
        Assert.Equal(new ColorRgba(66, 64, 60), EnvironmentMaterials.HearthStone.BaseColor);
        Assert.Equal(new ColorRgba(46, 20, 10), EnvironmentMaterials.HearthEmbers.BaseColor);
        Assert.Equal(new ColorRgba(240, 104, 32), EnvironmentMaterials.HearthEmbers.EmissiveColor);
        Assert.Equal(new ColorRgba(98, 70, 42), EnvironmentMaterials.Furniture.BaseColor);
        Assert.Equal(new ColorRgba(42, 32, 24), EnvironmentMaterials.TreeTrunk.BaseColor);
        Assert.Equal(new ColorRgba(22, 38, 24), EnvironmentMaterials.TreeCanopy.BaseColor);
    }

    [Fact]
    public void VertexColorsStillMirrorMaterialBaseColors()
    {
        var scene = EnvironmentFactory.Create();
        foreach (var sceneObject in scene.Objects)
        {
            foreach (var vertex in sceneObject.Mesh.Vertices)
            {
                Assert.Equal(sceneObject.Material.BaseColor, vertex.Color);
            }
        }
    }

    // --- mesh/index validity unchanged --------------------------------------------

    [Fact]
    public void MeshAndIndexValidityAreUnchanged()
    {
        var box = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        Assert.Equal(24, box.Vertices.Count);
        Assert.Equal(36, box.Indices.Count);
        Assert.All(box.Indices, index => Assert.InRange(index, 0, box.Vertices.Count - 1));

        var prism = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);
        Assert.Equal(18, prism.Vertices.Count);
        Assert.Equal(24, prism.Indices.Count);
        Assert.All(prism.Indices, index => Assert.InRange(index, 0, prism.Vertices.Count - 1));

        var ground = MeshFactory.CreateGroundPlane(36f, ColorRgba.Ground);
        Assert.Equal(4, ground.Vertices.Count);
        Assert.Equal(6, ground.Indices.Count);
        Assert.All(ground.Indices, index => Assert.InRange(index, 0, ground.Vertices.Count - 1));
    }

    [Fact]
    public void SceneCompositionIsUnchanged()
    {
        var scene = EnvironmentFactory.Create();
        Assert.True(scene.Objects.Count > 100, "the full environment scene is present");
        Assert.Single(WithPrefix(scene, "ForestGround"));
        Assert.Single(WithPrefix(scene, "Clearing"));
        Assert.Single(WithPrefix(scene, "LonghouseFloor"));
        Assert.Equal(2, WithPrefix(scene, "Gable.").Count());
        Assert.Equal(3, WithPrefix(scene, "Roof.").Count());
        Assert.Equal(6, WithPrefix(scene, "Wall.").Count());
        Assert.Equal(5, WithPrefix(scene, "Hearth.").Count());
        Assert.Equal(LonghouseLayout.TreeCount * 4, WithPrefix(scene, "Tree.").Count());
    }

    [Fact]
    public void CollisionSetsAreUnchanged()
    {
        var session = new GameSession();
        Assert.Equal(34, session.PlayerCollisions.Count);
        Assert.Equal(32, session.CameraObstructions.Count);
    }
}
