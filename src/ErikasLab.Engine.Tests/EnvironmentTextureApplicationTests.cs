using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2T real-asset application coverage: the six integrated runtime PNGs
/// exist and decode as sane PNGs, the textured materials reference exactly the
/// ids those files provide, texture scales stay at the tuned repeats-per-meter
/// values, missing assets still fall back safely, and the Phase 2R lighting
/// values are byte-identical (2T performs no lighting retune). Pure/portable
/// except for reading the committed source PNGs (signature + IHDR only, no
/// image decoding dependency).
/// </summary>
public sealed class EnvironmentTextureApplicationTests
{
    private static readonly (string FileName, string TextureId, int Width, int Height)[] IntegratedSlots =
    [
        ("wall_planks.png", EnvironmentTextureIds.WallPlanks, 1024, 683),
        ("structural_timber.png", EnvironmentTextureIds.StructuralTimber, 1024, 773),
        ("roof_turf.png", EnvironmentTextureIds.RoofTurf, 512, 512),
        ("hearth_stone.png", EnvironmentTextureIds.HearthStone, 1024, 683),
        ("interior_floor.png", EnvironmentTextureIds.InteriorFloor, 1024, 683),
        ("furniture_wood.png", EnvironmentTextureIds.Furniture, 512, 342),
    ];

    private static string EnvironmentDirectory()
    {
        // Walk up from the test output directory to the repository root, then
        // into the platform source tree where the runtime PNGs live.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "ErikasLab.Platform.MonoGame", "Content", "environment");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/ErikasLab.Platform.MonoGame/Content/environment from " + AppContext.BaseDirectory);
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        // Minimal PNG validation: 8-byte signature + IHDR width/height
        // (big-endian uint32 at bytes 16..24). No image library required.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 32, $"{path} is too small to be a PNG");
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(signature, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes[12..16]));
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return (width, height);
    }

    [Fact]
    public void IntegratedSlotPngsExistAndDecodeAsSanePngs()
    {
        var directory = EnvironmentDirectory();
        foreach (var (fileName, _, width, height) in IntegratedSlots)
        {
            var path = Path.Combine(directory, fileName);
            Assert.True(File.Exists(path), $"integrated texture {fileName} must ship in Content/environment");
            var actual = ReadPngDimensions(path);
            Assert.Equal(width, actual.Width);
            Assert.Equal(height, actual.Height);
        }
    }

    [Fact]
    public void IntegratedMaterialsReferenceExactlyTheShippedTextureIds()
    {
        var byId = new Dictionary<string, StaticMaterial>
        {
            [EnvironmentTextureIds.WallPlanks] = EnvironmentMaterials.WallPlanks,
            [EnvironmentTextureIds.StructuralTimber] = EnvironmentMaterials.StructuralTimber,
            [EnvironmentTextureIds.RoofTurf] = EnvironmentMaterials.RoofTurf,
            [EnvironmentTextureIds.HearthStone] = EnvironmentMaterials.HearthStone,
            [EnvironmentTextureIds.InteriorFloor] = EnvironmentMaterials.InteriorFloor,
            [EnvironmentTextureIds.Furniture] = EnvironmentMaterials.Furniture,
        };

        foreach (var (fileName, textureId, _, _) in IntegratedSlots)
        {
            var expectedId = "environment/" + Path.GetFileNameWithoutExtension(fileName);
            Assert.Equal(textureId, expectedId);
            Assert.True(byId.TryGetValue(textureId, out var material), $"no material wires {textureId}");
            Assert.Equal(textureId, material.TextureId);
            Assert.True(material.IsValid, $"{textureId} material must stay valid");
        }
    }

    [Fact]
    public void UnintegratedSlotsKeepFallbackCapableTextureIds()
    {
        // Clearing, forest ground, and tree bark ship no suitable asset in 2T:
        // the slots stay wired so future art drops in, and the materials render
        // through their Phase 2R base colors until then.
        Assert.Equal(EnvironmentTextureIds.ClearingGround, EnvironmentMaterials.ClearingGround.TextureId);
        Assert.Equal(EnvironmentTextureIds.ForestGround, EnvironmentMaterials.ForestGround.TextureId);
        Assert.Equal(EnvironmentTextureIds.TreeTrunk, EnvironmentMaterials.TreeTrunk.TextureId);

        var empty = new TextureCatalog();
        Assert.False(empty.CanUseTexture(EnvironmentMaterials.ClearingGround));
        Assert.False(empty.CanUseTexture(EnvironmentMaterials.ForestGround));
        Assert.False(empty.CanUseTexture(EnvironmentMaterials.TreeTrunk));
    }

    [Fact]
    public void IntegratedTextureScalesAreOneRepeatPerMeter()
    {
        // 2T visual tuning outcome: every integrated asset tiles at exactly one
        // texture repeat per world meter (planks ~8 boards/m, thatch strands at
        // photo scale, stone/floor grain at photo scale); grounds keep their
        // Phase 2S 0.25 values while they remain fallback.
        foreach (var material in new[]
        {
            EnvironmentMaterials.WallPlanks,
            EnvironmentMaterials.StructuralTimber,
            EnvironmentMaterials.RoofTurf,
            EnvironmentMaterials.HearthStone,
            EnvironmentMaterials.InteriorFloor,
            EnvironmentMaterials.Furniture,
        })
        {
            Assert.True(float.IsFinite(material.TextureScale.X) && float.IsFinite(material.TextureScale.Y));
            Assert.True(material.TextureScale.X > 0f && material.TextureScale.Y > 0f);
            Assert.Equal(new Vector2(1f, 1f), material.TextureScale);
        }

        Assert.Equal(new Vector2(0.25f, 0.25f), EnvironmentMaterials.ClearingGround.TextureScale);
        Assert.Equal(new Vector2(0.25f, 0.25f), EnvironmentMaterials.ForestGround.TextureScale);
    }

    [Fact]
    public void EmptyCatalogFallsBackForEveryTexturedMaterial()
    {
        // Temporarily missing any one texture must still render through the
        // Phase 2R base color: with nothing registered, no textured material
        // resolves, and every material still holds a valid base color.
        var empty = new TextureCatalog();
        foreach (var material in EnvironmentMaterials.All.Where(m => m.TextureEnabled))
        {
            Assert.False(empty.CanUseTexture(material), $"{material.TextureId} must fall back when absent");
            Assert.True(material.IsValid);
        }
    }

    [Fact]
    public void FullCatalogResolvesAllSlotsAndDuplicateRegistrationIsDeterministic()
    {
        var catalog = new TextureCatalog();
        foreach (var id in EnvironmentTextureIds.All)
        {
            catalog.Register(id);
        }

        Assert.Equal(EnvironmentTextureIds.All.Length, catalog.Count);
        foreach (var material in EnvironmentMaterials.All.Where(m => m.TextureEnabled))
        {
            Assert.True(catalog.CanUseTexture(material), $"{material.TextureId} must resolve when registered");
        }

        foreach (var id in EnvironmentTextureIds.All)
        {
            catalog.Register(id);
        }

        Assert.Equal(EnvironmentTextureIds.All.Length, catalog.Count);
    }

    [Fact]
    public void LightingIsUnchangedFromPhase2R()
    {
        // 2T performs no lighting retune: real textures were integrated under
        // the exact Phase 2R policy and the hearth stayed dominant without it.
        var lighting = EnvironmentLighting.FimbulWinter;
        Assert.Equal(new Vector3(0.20f, 0.23f, 0.28f), lighting.AmbientLightColor);
        Assert.Equal(new Vector3(-0.45f, -1f, -0.35f), lighting.Directional0.Direction);
        Assert.Equal(new Vector3(0.62f, 0.70f, 0.82f), lighting.Directional0.DiffuseColor);
        Assert.Equal(new Vector3(0.10f, 0.11f, 0.14f), lighting.Directional1.DiffuseColor);
        Assert.False(lighting.Directional2.Enabled);
        Assert.True(lighting.IsValid);
    }

    [Fact]
    public void TimberStaysDarkerThanWallsAndHearthStaysEmissive()
    {
        // 2T tint policy: tints were deliberately left at the Phase 2R base
        // colors, so the material-identity invariants still hold verbatim.
        var timber = EnvironmentMaterials.StructuralTimber.BaseColor;
        var wall = EnvironmentMaterials.WallPlanks.BaseColor;
        Assert.True(
            timber.R + timber.G + timber.B < wall.R + wall.G + wall.B,
            "structural timber reads darker than wall planks");

        var embers = EnvironmentMaterials.HearthEmbers;
        Assert.False(embers.TextureEnabled);
        Assert.True(embers.EmissiveColor.R > 200, "ember bed stays the emissive focal point");
    }
}
