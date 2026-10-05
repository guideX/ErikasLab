using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2R material/lighting foundation coverage: material value validity,
/// category assignment, identity determinism, texture fallback policy,
/// lighting-policy invariants, and the collision/layout regression guards that
/// prove the visual pass touched no gameplay geometry. Pure/portable (no GPU,
/// no MonoGame).
/// </summary>
public sealed class MaterialTests
{
    // --- helpers ---------------------------------------------------------

    private static IEnumerable<SceneObject> WithPrefix(Scene scene, string prefix) =>
        scene.Objects.Where(o => o.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static ColorRgba EmissiveOf(StaticMaterial material) => material.EmissiveColor;

    // --- material values ---------------------------------------------------

    [Fact]
    public void AllEnvironmentMaterialsAreValidAndFinite()
    {
        foreach (var material in EnvironmentMaterials.All)
        {
            Assert.True(material.IsValid, "environment material must hold finite, usable values");
            Assert.True(material.LightingEnabled, "the environment keeps lighting enabled");
            Assert.False(material.TextureEnabled, "Phase 2R ships no environment textures");
        }
    }

    [Fact]
    public void MaterialColorsStayWithinByteRange()
    {
        foreach (var material in EnvironmentMaterials.All)
        {
            Assert.InRange(material.BaseColor.R, 0, 255);
            Assert.InRange(material.BaseColor.G, 0, 255);
            Assert.InRange(material.BaseColor.B, 0, 255);
            Assert.InRange(material.EmissiveColor.R, 0, 255);
            Assert.InRange(material.EmissiveColor.G, 0, 255);
            Assert.InRange(material.EmissiveColor.B, 0, 255);
        }
    }

    [Fact]
    public void EmissiveValuesAreFiniteAndNonNegative()
    {
        foreach (var material in EnvironmentMaterials.All)
        {
            Assert.True(material.EmissiveColor.R >= 0 && material.EmissiveColor.G >= 0 && material.EmissiveColor.B >= 0);
            var emissive = new Vector3(
                material.EmissiveColor.R / 255f,
                material.EmissiveColor.G / 255f,
                material.EmissiveColor.B / 255f);
            Assert.True(float.IsFinite(emissive.X) && float.IsFinite(emissive.Y) && float.IsFinite(emissive.Z));
        }
    }

    [Fact]
    public void OnlyTheHearthBedIsEmissive()
    {
        foreach (var sceneObject in EnvironmentFactory.Create().Objects)
        {
            var isEmissive = sceneObject.Material.EmissiveColor.R > 0
                || sceneObject.Material.EmissiveColor.G > 0
                || sceneObject.Material.EmissiveColor.B > 0;
            Assert.True(
                sceneObject.Name.StartsWith("Hearth.Bed", StringComparison.Ordinal) == isEmissive,
                $"{sceneObject.Name} emissive state is unexpected");
        }
    }

    // --- material identity -------------------------------------------------

    [Fact]
    public void MaterialIdentitiesAreDeterministicAcrossSceneBuilds()
    {
        var first = EnvironmentFactory.Create();
        var second = EnvironmentFactory.Create();

        Assert.Equal(first.Objects.Count, second.Objects.Count);
        for (var i = 0; i < first.Objects.Count; i++)
        {
            Assert.Equal(first.Objects[i].Name, second.Objects[i].Name);
            Assert.Equal(first.Objects[i].Material, second.Objects[i].Material);
        }
    }

    [Fact]
    public void EnvironmentMaterialsAreSharedAndDistinct()
    {
        var all = EnvironmentMaterials.All;
        Assert.Equal(11, all.Length);
        Assert.Equal(11, all.Distinct().Count());

        var scene = EnvironmentFactory.Create();
        var used = scene.Objects.Select(o => o.Material).Distinct().ToHashSet();
        Assert.All(used, material => Assert.Contains(material, all));
    }

    // --- category assignment ----------------------------------------------

    [Fact]
    public void EnvironmentObjectsReceiveExpectedMaterialCategory()
    {
        var scene = EnvironmentFactory.Create();

        void AssertCategory(string prefix, StaticMaterial material, bool exceptRidge = false)
        {
            foreach (var sceneObject in WithPrefix(scene, prefix))
            {
                if (exceptRidge && sceneObject.Name.StartsWith("Roof.Ridge", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.Equal(material, sceneObject.Material);
            }
        }

        AssertCategory("Wall.", EnvironmentMaterials.WallPlanks);
        AssertCategory("Gable.", EnvironmentMaterials.WallPlanks);
        AssertCategory("Roof.", EnvironmentMaterials.RoofTurf, exceptRidge: true);
        AssertCategory("Roof.Ridge", EnvironmentMaterials.StructuralTimber);
        AssertCategory("Post.", EnvironmentMaterials.StructuralTimber);
        AssertCategory("Beam.", EnvironmentMaterials.StructuralTimber);
        AssertCategory("Rafter.", EnvironmentMaterials.StructuralTimber);
        AssertCategory("Door.Frame.", EnvironmentMaterials.StructuralTimber);
        AssertCategory("Hearth.Rim.", EnvironmentMaterials.HearthStone);
        AssertCategory("Hearth.Bed", EnvironmentMaterials.HearthEmbers);
        AssertCategory("Bench.", EnvironmentMaterials.Furniture);
        AssertCategory("Table.", EnvironmentMaterials.Furniture);
        Assert.Equal(EnvironmentMaterials.InteriorFloor, Assert.Single(WithPrefix(scene, "LonghouseFloor")).Material);
        Assert.Equal(EnvironmentMaterials.ClearingGround, Assert.Single(WithPrefix(scene, "Clearing")).Material);
        Assert.Equal(EnvironmentMaterials.ForestGround, Assert.Single(WithPrefix(scene, "ForestGround")).Material);

        foreach (var trunk in WithPrefix(scene, "Tree.").Where(o => o.Name.EndsWith(".Trunk", StringComparison.Ordinal)))
        {
            Assert.Equal(EnvironmentMaterials.TreeTrunk, trunk.Material);
        }

        foreach (var canopy in WithPrefix(scene, "Tree.").Where(o => o.Name.Contains(".Canopy.", StringComparison.Ordinal)))
        {
            Assert.Equal(EnvironmentMaterials.TreeCanopy, canopy.Material);
        }
    }

    [Fact]
    public void NoEnvironmentObjectUsesTheDefaultMaterial()
    {
        var scene = EnvironmentFactory.Create();
        Assert.NotEmpty(scene.Objects);
        foreach (var sceneObject in scene.Objects)
        {
            Assert.True(sceneObject.Material.IsValid, $"{sceneObject.Name} has an invalid material");
            Assert.NotEqual(StaticMaterial.Default, sceneObject.Material);
        }
    }

    [Fact]
    public void MeshVertexColorsMirrorMaterialBaseColors()
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

    // --- texture fallback --------------------------------------------------

    [Fact]
    public void TextureFallbackAppliesWhenTextureIsAbsent()
    {
        var catalog = new TextureCatalog();
        var textured = new StaticMaterial
        {
            BaseColor = new ColorRgba(10, 20, 30),
            TextureId = "textures/turf",
        };

        Assert.True(textured.TextureEnabled);
        Assert.False(catalog.CanUseTexture(textured), "unknown texture id must fall back to base color");

        catalog.Register("textures/turf");
        Assert.True(catalog.CanUseTexture(textured));
    }

    [Fact]
    public void UntexturedMaterialsNeverRequestATextureBind()
    {
        var catalog = new TextureCatalog();
        foreach (var material in EnvironmentMaterials.All)
        {
            Assert.False(material.TextureEnabled);
            Assert.False(catalog.CanUseTexture(material));
        }

        Assert.False(StaticMaterial.Default.TextureEnabled);
        Assert.False(catalog.CanUseTexture(StaticMaterial.Default));
    }

    [Fact]
    public void TextureCatalogRejectsBlankIds()
    {
        var catalog = new TextureCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Register(""));
        Assert.Throws<ArgumentException>(() => catalog.Register("   "));
        Assert.Throws<ArgumentNullException>(() => catalog.Register(null!));
        Assert.Equal(0, catalog.Count);
    }

    // --- no per-frame material allocation ----------------------------------

    [Fact]
    public void MaterialReadsDoNotAllocateOrMutate()
    {
        var scene = EnvironmentFactory.Create();
        var target = Assert.Single(WithPrefix(scene, "Wall.Left"));
        var before = target.Transform;

        var read1 = target.Material;
        var read2 = target.Material;

        Assert.Equal(read1, read2);
        Assert.True(before.Equals(target.Transform), "reading a material must not touch the transform");
        Assert.Equal(EnvironmentMaterials.WallPlanks, read1);
    }

    // --- lighting policy -----------------------------------------------------

    [Fact]
    public void LightingPolicyValuesAreFinite()
    {
        var lighting = EnvironmentLighting.FimbulWinter;
        Assert.True(lighting.IsValid);

        Assert.True(float.IsFinite(lighting.AmbientLightColor.X));
        Assert.True(float.IsFinite(lighting.AmbientLightColor.Y));
        Assert.True(float.IsFinite(lighting.AmbientLightColor.Z));

        foreach (var directional in new[] { lighting.Directional0, lighting.Directional1, lighting.Directional2 })
        {
            Assert.True(directional.IsValid);
            Assert.True(
                directional.Direction.LengthSquared() > 0f || !directional.Enabled,
                "enabled lights need a nonzero direction");
            Assert.True(directional.DiffuseColor.X >= 0f && directional.DiffuseColor.Y >= 0f && directional.DiffuseColor.Z >= 0f);
            Assert.True(directional.SpecularColor.X >= 0f && directional.SpecularColor.Y >= 0f && directional.SpecularColor.Z >= 0f);
        }
    }

    [Fact]
    public void LightingPolicyIsCoolAndDim()
    {
        var lighting = EnvironmentLighting.FimbulWinter;
        Assert.True(lighting.AmbientLightColor.Z >= lighting.AmbientLightColor.X, "ambient reads cool (blue >= red)");
        Assert.True(lighting.AmbientLightColor.X < 0.25f, "ambient stays dim");
        Assert.True(lighting.Directional0.DiffuseColor.Z >= lighting.Directional0.DiffuseColor.X, "key light reads cool");
        Assert.True(lighting.Directional0.DiffuseColor.X < 0.8f, "key light stays subdued");
        Assert.False(lighting.Directional2.Enabled, "the third light slot stays disabled");
    }

    // --- material mood invariants --------------------------------------------

    [Fact]
    public void HearthEmissiveIsWarmerAndBrighterThanHearthStone()
    {
        var embers = EnvironmentMaterials.HearthEmbers;
        var stone = EnvironmentMaterials.HearthStone;

        var embersWarmth = embers.EmissiveColor.R - embers.EmissiveColor.B;
        var stoneWarmth = stone.EmissiveColor.R - stone.EmissiveColor.B;
        Assert.True(embersWarmth > stoneWarmth, "ember emissive must be warmer than stone");

        var embersBrightness = embers.EmissiveColor.R + embers.EmissiveColor.G + embers.EmissiveColor.B;
        var stoneBrightness = stone.EmissiveColor.R + stone.EmissiveColor.G + stone.EmissiveColor.B;
        Assert.True(embersBrightness > stoneBrightness, "ember emissive must outshine stone");
    }

    [Fact]
    public void ForestGroundIsDarkerThanClearingGround()
    {
        var forest = EnvironmentMaterials.ForestGround.BaseColor;
        var clearing = EnvironmentMaterials.ClearingGround.BaseColor;
        Assert.True(
            forest.R + forest.G + forest.B < clearing.R + clearing.G + clearing.B,
            "deep forest ground must stay darker than the clearing");
    }

    [Fact]
    public void RoofTimberAndWallMaterialsAreDistinct()
    {
        var roof = EnvironmentMaterials.RoofTurf;
        var timber = EnvironmentMaterials.StructuralTimber;
        var wall = EnvironmentMaterials.WallPlanks;

        Assert.NotEqual(roof, timber);
        Assert.NotEqual(roof, wall);
        Assert.NotEqual(timber, wall);

        // Timber is the darkest structural family member; walls separate lighter.
        Assert.True(
            timber.BaseColor.R + timber.BaseColor.G + timber.BaseColor.B <
            wall.BaseColor.R + wall.BaseColor.G + wall.BaseColor.B,
            "structural timber reads darker than wall planks");
    }

    [Fact]
    public void TimberIsDarkerAndRicherThanThePhase2LBlockout()
    {
        var timber = EnvironmentMaterials.StructuralTimber.BaseColor;
        var blockout = ColorRgba.Timber;
        Assert.True(
            timber.R + timber.G + timber.B < blockout.R + blockout.G + blockout.B,
            "Phase 2R structural timber is darker than the 2L blockout");
    }

    // --- collision / layout regression guards (Phase 2R touched the builder) -

    [Fact]
    public void CollisionSetsAreUnchanged()
    {
        var session = new GameSession();
        Assert.Equal(34, session.PlayerCollisions.Count);
        Assert.Equal(32, session.CameraObstructions.Count);
    }

    [Fact]
    public void LonghouseLayoutDimensionsAreUnchanged()
    {
        Assert.Equal(16f, LonghouseLayout.Length);
        Assert.Equal(6f, LonghouseLayout.Width);
        Assert.Equal(2.6f, LonghouseLayout.WallHeight);
        Assert.Equal(5f, LonghouseLayout.RidgeHeight);
        Assert.Equal(1.2f, LonghouseLayout.DoorWidth);
        Assert.Equal(2f, LonghouseLayout.DoorHeight);
        Assert.Equal(36f, LonghouseLayout.ClearingSize);
        Assert.Equal(140f, LonghouseLayout.ForestGroundSize);
        Assert.Equal(24, LonghouseLayout.TreeCount);
    }

    [Fact]
    public void SpawnIsUnchanged()
    {
        Assert.Equal(new Vector3(0f, 0f, 12f), LonghouseLayout.SpawnPosition);
        Assert.Equal(MathF.PI, LonghouseLayout.SpawnFacingYawRadians, precision: 6);
    }

    [Fact]
    public void SceneCompositionIsUnchanged()
    {
        var scene = EnvironmentFactory.Create();
        Assert.Single(WithPrefix(scene, "ForestGround"));
        Assert.Single(WithPrefix(scene, "Clearing"));
        Assert.Single(WithPrefix(scene, "LonghouseFloor"));
        Assert.Equal(2, WithPrefix(scene, "Gable.").Count());
        Assert.Equal(3, WithPrefix(scene, "Roof.").Count());
        Assert.Equal(6, WithPrefix(scene, "Wall.").Count());
        Assert.Equal(4 + 2 * LonghouseLayout.SidePostCount, WithPrefix(scene, "Post.").Count());
        Assert.Equal(LonghouseLayout.SidePostCount, WithPrefix(scene, "Beam.Tie.").Count());
        Assert.Equal(2 * LonghouseLayout.SidePostCount, WithPrefix(scene, "Rafter.").Count());
        Assert.Equal(5, WithPrefix(scene, "Hearth.").Count());
        Assert.Equal(2, WithPrefix(scene, "Bench.").Count());
        Assert.Equal(2, WithPrefix(scene, "Table.").Count());
        Assert.Equal(3, WithPrefix(scene, "Door.Frame.").Count());
        Assert.Equal(LonghouseLayout.TreeCount * 4, WithPrefix(scene, "Tree.").Count());
    }
}
