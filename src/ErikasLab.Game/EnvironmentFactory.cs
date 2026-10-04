using System.Numerics;
using ErikasLab.Engine;

namespace ErikasLab.Game;

/// <summary>
/// Phase 2L environment blockout builder. Owns the static world geometry only
/// (ground, clearing, forest ring, longhouse shell, timber structure, and
/// interior masses); Erika's position/locomotion/camera stay in
/// <see cref="GameSession"/> and the renderer only consumes the resulting
/// <see cref="Scene"/>. Every transform derives from
/// <see cref="LonghouseLayout"/> so no raw coordinates are scattered here.
///
/// The geometry is built once at session construction and never rebuilt, so
/// there are no per-frame allocations or draw-call churn. Materials are simple
/// vertex colors (one shared unit mesh per palette entry), which the renderer
/// already consumes; this is placeholder art, not final environment art.
/// </summary>
public static class EnvironmentFactory
{
    /// <summary>Build the complete Phase 2L home-area scene.</summary>
    public static Scene Create()
    {
        var scene = new Scene();

        // One shared unit primitive per palette entry: every instance reuses the
        // same MeshData (and therefore one GPU buffer in the renderer cache) and
        // only differs by its Transform.
        var timberMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.Timber);
        var wallMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.WallWood);
        var roofMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.Roof);
        var stoneMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.Stone);
        var hearthBedMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.HearthBed);
        var furnitureMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.Furniture);
        var trunkMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.TreeTrunk);
        var canopyMesh = MeshFactory.CreateBox(Vector3.One, ColorRgba.TreeCanopy);
        var gableMesh = MeshFactory.CreateTriangularPrism(Vector3.One, ColorRgba.WallWood);

        var forestGroundMesh = MeshFactory.CreateGroundPlane(LonghouseLayout.ForestGroundSize, ColorRgba.ForestGround);
        var clearingMesh = MeshFactory.CreateGroundPlane(LonghouseLayout.ClearingSize, ColorRgba.Clearing);
        var floorMesh = MeshFactory.CreateGroundRectangle(
            LonghouseLayout.Width - 2f * LonghouseLayout.FloorMargin,
            LonghouseLayout.Length - 2f * LonghouseLayout.FloorMargin,
            ColorRgba.FloorWood);

        AddGrounds(scene, forestGroundMesh, clearingMesh, floorMesh);
        AddWalls(scene, wallMesh);
        AddGableEnds(scene, gableMesh);
        AddRoof(scene, roofMesh, timberMesh);
        AddTimberStructure(scene, timberMesh);
        AddHearth(scene, stoneMesh, hearthBedMesh);
        AddFurnishings(scene, furnitureMesh);
        AddDoorFrame(scene, timberMesh);
        AddForest(scene, trunkMesh, canopyMesh);

        return scene;
    }

    /// <summary>
    /// Phase 2M static camera-obstruction set for the Phase 2L environment. Owns
    /// the obstruction descriptions (per the responsibility split: environment
    /// owns them, the camera rig consumes them). Every box derives from the same
    /// authoritative <see cref="LonghouseLayout"/> dimensions as the rendered
    /// geometry and mirrors the matching scene object's name, center, half
    /// extents, and orientation: the long walls, rear wall, the two front
    /// doorway flanking segments, the doorway lintel, and the two rotated
    /// pitched roof slabs, plus one box per tree trunk. The lintel and flanking
    /// segments keep the genuine doorway open for camera sight lines. Posts are
    /// subsumed by the wall boxes they sit in; gables are triangular prisms and
    /// are intentionally not approximated by oversized boxes; canopies are not
    /// aggressive blockers. Built once; no per-frame collider construction.
    /// </summary>
    public static CameraObstructionSet CreateCameraObstructions()
    {
        var boxes = new List<CameraObstructionBox>();

        var halfWidth = LonghouseLayout.HalfWidth;
        var height = LonghouseLayout.WallHeight;
        var thickness = LonghouseLayout.WallThickness;
        var halfThickness = thickness / 2f;
        var y = height / 2f;

        boxes.Add(new CameraObstructionBox(
            "Wall.Left",
            new Vector3(-halfWidth, y, LonghouseLayout.Origin.Z),
            new Vector3(halfThickness, height / 2f, LonghouseLayout.HalfLength),
            Quaternion.Identity));
        boxes.Add(new CameraObstructionBox(
            "Wall.Right",
            new Vector3(halfWidth, y, LonghouseLayout.Origin.Z),
            new Vector3(halfThickness, height / 2f, LonghouseLayout.HalfLength),
            Quaternion.Identity));
        boxes.Add(new CameraObstructionBox(
            "Wall.Rear",
            new Vector3(LonghouseLayout.Origin.X, y, LonghouseLayout.RearZ),
            new Vector3(halfWidth, height / 2f, halfThickness),
            Quaternion.Identity));

        // Front end wall: two flanking segments plus the lintel above the
        // opening, mirroring the rendered arrangement so the doorway itself
        // stays open for camera lines.
        var doorHalf = LonghouseLayout.DoorWidth / 2f;
        var segmentWidth = halfWidth - doorHalf;
        var segmentCenterX = doorHalf + segmentWidth / 2f;
        var frontZ = LonghouseLayout.FrontZ;
        boxes.Add(new CameraObstructionBox(
            "Wall.Front.Left",
            new Vector3(-segmentCenterX, y, frontZ),
            new Vector3(segmentWidth / 2f, height / 2f, halfThickness),
            Quaternion.Identity));
        boxes.Add(new CameraObstructionBox(
            "Wall.Front.Right",
            new Vector3(segmentCenterX, y, frontZ),
            new Vector3(segmentWidth / 2f, height / 2f, halfThickness),
            Quaternion.Identity));

        var lintelHeight = height - LonghouseLayout.DoorHeight;
        var lintelCenterY = LonghouseLayout.DoorHeight + lintelHeight / 2f;
        boxes.Add(new CameraObstructionBox(
            "Wall.Front.Lintel",
            new Vector3(LonghouseLayout.Origin.X, lintelCenterY, frontZ),
            new Vector3(doorHalf, lintelHeight / 2f, halfThickness),
            Quaternion.Identity));

        // Pitched roof slabs: same centers, half extents, and rotations as the
        // rendered slabs (oriented boxes, not a world AABB).
        var roofHalf = new Vector3(
            LonghouseLayout.RoofSlopeLength / 2f,
            LonghouseLayout.RoofThickness / 2f,
            LonghouseLayout.RoofLengthZ / 2f);
        var roofCenterX = LonghouseLayout.RoofHalfSpanX / 2f;
        var roofCenterY = LonghouseLayout.RoofSlabCenterY;
        var roofZ = LonghouseLayout.Origin.Z;
        var roofLeft = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, LonghouseLayout.RoofSlopeAngleRadians);
        var roofRight = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - LonghouseLayout.RoofSlopeAngleRadians);
        boxes.Add(new CameraObstructionBox("Roof.Left", new Vector3(-roofCenterX, roofCenterY, roofZ), roofHalf, roofLeft));
        boxes.Add(new CameraObstructionBox("Roof.Right", new Vector3(roofCenterX, roofCenterY, roofZ), roofHalf, roofRight));

        // Tree trunks (canopies intentionally excluded).
        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var position = LonghouseLayout.TreePosition(i);
            var scale = LonghouseLayout.TreeScale(i);
            var trunkWidth = LonghouseLayout.TreeTrunkWidth * scale;
            var trunkHeight = LonghouseLayout.TreeTrunkHeight * scale;
            boxes.Add(new CameraObstructionBox(
                $"Tree.{i:00}.Trunk",
                position + new Vector3(0f, trunkHeight / 2f, 0f),
                new Vector3(trunkWidth / 2f, trunkHeight / 2f, trunkWidth / 2f),
                Quaternion.CreateFromYawPitchRoll(LonghouseLayout.TreeYawRadians(i), 0f, 0f)));
        }

        return new CameraObstructionSet(boxes.ToArray());
    }

    /// <summary>
    /// Phase 2N static player-collision set for the Phase 2L environment. This
    /// is deliberately a *different* set from the camera obstructions: it holds
    /// only the flat-XZ blockers that define navigable space at foot level, so
    /// the pitched roof, doorway lintel, gables, rafters, and interior masses
    /// are excluded. Built once from the same authoritative
    /// <see cref="LonghouseLayout"/> dimensions as the rendered geometry.
    ///
    /// Included: the two long walls, the rear wall, the two front doorway
    /// flanking segments (leaving the genuine 1.2 m doorway open), the central
    /// hearth, both long benches, both tables, and the 24 tree trunks. Phase 2O
    /// makes the interior furnishings solid using their rendered XZ footprints
    /// (the hearth as one box, each table by its tabletop projection with no
    /// individual legs). There is still no vertical collision: the player is a
    /// flat-XZ circle, so low obstacles and the doorway lintel are represented
    /// only by their foot-level footprint.
    /// </summary>
    public static PlayerCollisionSet CreatePlayerCollisionSet()
    {
        var boxes = new List<PlayerCollisionBox>();

        var halfWidth = LonghouseLayout.HalfWidth;
        var halfThickness = LonghouseLayout.WallThickness / 2f;

        boxes.Add(new PlayerCollisionBox(
            "Wall.Left",
            new Vector2(-halfWidth, LonghouseLayout.Origin.Z),
            new Vector2(halfThickness, LonghouseLayout.HalfLength),
            0f));
        boxes.Add(new PlayerCollisionBox(
            "Wall.Right",
            new Vector2(halfWidth, LonghouseLayout.Origin.Z),
            new Vector2(halfThickness, LonghouseLayout.HalfLength),
            0f));
        boxes.Add(new PlayerCollisionBox(
            "Wall.Rear",
            new Vector2(LonghouseLayout.Origin.X, LonghouseLayout.RearZ),
            new Vector2(halfWidth, halfThickness),
            0f));

        // Front end wall: two flanking segments only. The doorway gap itself
        // has no blocker, so the opening is genuinely traversable; there is no
        // lintel because the player is a flat-XZ circle and the door header is
        // above her head.
        var doorHalf = LonghouseLayout.DoorWidth / 2f;
        var segmentWidth = halfWidth - doorHalf;
        var segmentCenterX = doorHalf + segmentWidth / 2f;
        var frontZ = LonghouseLayout.FrontZ;
        boxes.Add(new PlayerCollisionBox(
            "Wall.Front.Left",
            new Vector2(-segmentCenterX, frontZ),
            new Vector2(segmentWidth / 2f, halfThickness),
            0f));
        boxes.Add(new PlayerCollisionBox(
            "Wall.Front.Right",
            new Vector2(segmentCenterX, frontZ),
            new Vector2(segmentWidth / 2f, halfThickness),
            0f));

        // Phase 2O interior furnishings. Each blocker mirrors the rendered XZ
        // footprint from the same LonghouseLayout values the geometry uses, so
        // collision and rendering cannot drift. The hearth is one solid box over
        // its full stone footprint (Erika walks around the fire bed, never over
        // it); benches use their full seat footprint; tables use the tabletop
        // projection (no individual legs, and walking underneath is not allowed).
        boxes.Add(new PlayerCollisionBox(
            "Hearth",
            new Vector2(LonghouseLayout.Origin.X, LonghouseLayout.HearthCenterZ),
            new Vector2(LonghouseLayout.HearthWidth / 2f, LonghouseLayout.HearthLength / 2f),
            0f));

        var benchX = LonghouseLayout.BenchCenterX;
        var benchHalf = new Vector2(LonghouseLayout.BenchDepth / 2f, LonghouseLayout.BenchLength / 2f);
        boxes.Add(new PlayerCollisionBox("Bench.Left", new Vector2(-benchX, 0f), benchHalf, 0f));
        boxes.Add(new PlayerCollisionBox("Bench.Right", new Vector2(benchX, 0f), benchHalf, 0f));

        var tableX = LonghouseLayout.TableCenterX;
        var tableHalf = new Vector2(LonghouseLayout.TableWidth / 2f, LonghouseLayout.TableLength / 2f);
        boxes.Add(new PlayerCollisionBox("Table.Left", new Vector2(-tableX, LonghouseLayout.TableCenterZ), tableHalf, 0f));
        boxes.Add(new PlayerCollisionBox("Table.Right", new Vector2(tableX, LonghouseLayout.TableCenterZ), tableHalf, 0f));

        // Tree trunks (canopies are far above the player and not blockers).
        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var position = LonghouseLayout.TreePosition(i);
            var scale = LonghouseLayout.TreeScale(i);
            var trunkHalf = LonghouseLayout.TreeTrunkWidth * scale / 2f;
            boxes.Add(new PlayerCollisionBox(
                $"Tree.{i:00}.Trunk",
                new Vector2(position.X, position.Z),
                new Vector2(trunkHalf, trunkHalf),
                LonghouseLayout.TreeYawRadians(i)));
        }

        return new PlayerCollisionSet(boxes.ToArray());
    }

    private static void AddGrounds(Scene scene, MeshData forestGround, MeshData clearing, MeshData floor)
    {
        scene.Add(new SceneObject(
            "ForestGround",
            forestGround,
            new Transform(new Vector3(0f, LonghouseLayout.ForestGroundY, 0f), Quaternion.Identity, Vector3.One)));
        scene.Add(new SceneObject(
            "Clearing",
            clearing,
            new Transform(new Vector3(0f, 0f, 0f), Quaternion.Identity, Vector3.One)));
        scene.Add(new SceneObject(
            "LonghouseFloor",
            floor,
            new Transform(new Vector3(0f, LonghouseLayout.FloorTopY, 0f), Quaternion.Identity, Vector3.One)));
    }

    private static void AddWalls(Scene scene, MeshData wall)
    {
        var halfWidth = LonghouseLayout.HalfWidth;
        var height = LonghouseLayout.WallHeight;
        var thickness = LonghouseLayout.WallThickness;
        var length = LonghouseLayout.Length;
        var y = height / 2f;

        AddBox(scene, "Wall.Left", wall, new Vector3(-halfWidth, y, 0f), new Vector3(thickness, height, length));
        AddBox(scene, "Wall.Right", wall, new Vector3(halfWidth, y, 0f), new Vector3(thickness, height, length));
        AddBox(scene, "Wall.Rear", wall, new Vector3(0f, y, LonghouseLayout.RearZ), new Vector3(LonghouseLayout.Width, height, thickness));

        // Front end wall with a genuine open doorway: two flanking segments plus
        // a lintel above the opening, leaving the opening itself unrendered.
        var doorHalf = LonghouseLayout.DoorWidth / 2f;
        var segmentWidth = halfWidth - doorHalf;
        var segmentCenterX = doorHalf + segmentWidth / 2f;
        var frontZ = LonghouseLayout.FrontZ;
        AddBox(scene, "Wall.Front.Left", wall, new Vector3(-segmentCenterX, y, frontZ), new Vector3(segmentWidth, height, thickness));
        AddBox(scene, "Wall.Front.Right", wall, new Vector3(segmentCenterX, y, frontZ), new Vector3(segmentWidth, height, thickness));

        var lintelHeight = LonghouseLayout.WallHeight - LonghouseLayout.DoorHeight;
        var lintelCenterY = LonghouseLayout.DoorHeight + lintelHeight / 2f;
        AddBox(scene, "Wall.Front.Lintel", wall, new Vector3(0f, lintelCenterY, frontZ), new Vector3(LonghouseLayout.DoorWidth, lintelHeight, thickness));
    }

    private static void AddGableEnds(Scene scene, MeshData gable)
    {
        var size = new Vector3(2f * LonghouseLayout.RoofHalfSpanX, LonghouseLayout.RoofRise, LonghouseLayout.WallThickness);
        scene.Add(new SceneObject(
            "Gable.Front",
            gable,
            new Transform(new Vector3(0f, LonghouseLayout.WallHeight, LonghouseLayout.FrontZ), Quaternion.Identity, size)));
        scene.Add(new SceneObject(
            "Gable.Rear",
            gable,
            new Transform(new Vector3(0f, LonghouseLayout.WallHeight, LonghouseLayout.RearZ), Quaternion.Identity, size)));
    }

    private static void AddRoof(Scene scene, MeshData roof, MeshData timber)
    {
        var size = new Vector3(LonghouseLayout.RoofSlopeLength, LonghouseLayout.RoofThickness, LonghouseLayout.RoofLengthZ);
        var centerX = LonghouseLayout.RoofHalfSpanX / 2f;
        var centerY = LonghouseLayout.RoofSlabCenterY;
        var z = LonghouseLayout.Origin.Z;

        var leftYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, LonghouseLayout.RoofSlopeAngleRadians);
        var rightYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - LonghouseLayout.RoofSlopeAngleRadians);
        scene.Add(new SceneObject("Roof.Left", roof, new Transform(new Vector3(-centerX, centerY, z), leftYaw, size)));
        scene.Add(new SceneObject("Roof.Right", roof, new Transform(new Vector3(centerX, centerY, z), rightYaw, size)));

        var ridgeSize = LonghouseLayout.BeamSize * 1.5f;
        AddBox(scene, "Roof.Ridge", timber, new Vector3(0f, LonghouseLayout.RidgeHeight, z), new Vector3(ridgeSize, ridgeSize, LonghouseLayout.RoofLengthZ));
    }

    private static void AddTimberStructure(Scene scene, MeshData timber)
    {
        var postSize = LonghouseLayout.PostSize;
        var height = LonghouseLayout.WallHeight;
        var y = height / 2f;

        // Corner posts.
        var corners = new (float X, float Z)[]
        {
            (-LonghouseLayout.HalfWidth, LonghouseLayout.FrontZ),
            (LonghouseLayout.HalfWidth, LonghouseLayout.FrontZ),
            (-LonghouseLayout.HalfWidth, LonghouseLayout.RearZ),
            (LonghouseLayout.HalfWidth, LonghouseLayout.RearZ),
        };
        for (var i = 0; i < corners.Length; i++)
        {
            var (x, z) = corners[i];
            AddBox(scene, $"Post.Corner.{i:00}", timber, new Vector3(x, y, z), new Vector3(postSize, height, postSize));
        }

        // Repeated side-wall posts plus tie beams and rafters at the same
        // deterministic spacing (one loop expresses the whole rhythm).
        var rafterSize = new Vector3(LonghouseLayout.RoofSlopeLength, LonghouseLayout.BeamSize, LonghouseLayout.BeamSize);
        var rafterCenterX = LonghouseLayout.RoofHalfSpanX / 2f;
        var rafterCenterY = LonghouseLayout.RoofSlabCenterY - LonghouseLayout.RoofThickness / 2f - LonghouseLayout.BeamSize / 2f;
        var rafterLeft = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, LonghouseLayout.RoofSlopeAngleRadians);
        var rafterRight = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - LonghouseLayout.RoofSlopeAngleRadians);
        var tieY = LonghouseLayout.WallHeight - LonghouseLayout.BeamSize / 2f;

        for (var i = 0; i < LonghouseLayout.SidePostCount; i++)
        {
            var z = LonghouseLayout.SidePostZ(i);
            AddBox(scene, $"Post.Side.Left.{i:00}", timber, new Vector3(-LonghouseLayout.HalfWidth, y, z), new Vector3(postSize, height, postSize));
            AddBox(scene, $"Post.Side.Right.{i:00}", timber, new Vector3(LonghouseLayout.HalfWidth, y, z), new Vector3(postSize, height, postSize));
            AddBox(scene, $"Beam.Tie.{i:00}", timber, new Vector3(0f, tieY, z), new Vector3(LonghouseLayout.Width, LonghouseLayout.BeamSize, LonghouseLayout.BeamSize));
            scene.Add(new SceneObject($"Rafter.Left.{i:00}", timber, new Transform(new Vector3(-rafterCenterX, rafterCenterY, z), rafterLeft, rafterSize)));
            scene.Add(new SceneObject($"Rafter.Right.{i:00}", timber, new Transform(new Vector3(rafterCenterX, rafterCenterY, z), rafterRight, rafterSize)));
        }
    }

    private static void AddHearth(Scene scene, MeshData stone, MeshData bed)
    {
        var centerZ = LonghouseLayout.HearthCenterZ;
        var width = LonghouseLayout.HearthWidth;
        var length = LonghouseLayout.HearthLength;
        var rimHeight = LonghouseLayout.HearthRimHeight;
        var rimThickness = LonghouseLayout.HearthRimThickness;
        var rimY = rimHeight / 2f;

        AddBox(
            scene,
            "Hearth.Bed",
            bed,
            new Vector3(0f, LonghouseLayout.FloorTopY + 0.03f, centerZ),
            new Vector3(width - 2f * rimThickness, 0.06f, length - 2f * rimThickness));

        var sideX = width / 2f - rimThickness / 2f;
        AddBox(scene, "Hearth.Rim.Left", stone, new Vector3(-sideX, rimY, centerZ), new Vector3(rimThickness, rimHeight, length));
        AddBox(scene, "Hearth.Rim.Right", stone, new Vector3(sideX, rimY, centerZ), new Vector3(rimThickness, rimHeight, length));

        var endZ = length / 2f - rimThickness / 2f;
        AddBox(scene, "Hearth.Rim.Front", stone, new Vector3(0f, rimY, centerZ + endZ), new Vector3(width - 2f * rimThickness, rimHeight, rimThickness));
        AddBox(scene, "Hearth.Rim.Rear", stone, new Vector3(0f, rimY, centerZ - endZ), new Vector3(width - 2f * rimThickness, rimHeight, rimThickness));
    }

    private static void AddFurnishings(Scene scene, MeshData furniture)
    {
        var benchX = LonghouseLayout.BenchCenterX;
        var benchY = LonghouseLayout.BenchHeight / 2f;
        var benchSize = new Vector3(LonghouseLayout.BenchDepth, LonghouseLayout.BenchHeight, LonghouseLayout.BenchLength);
        AddBox(scene, "Bench.Left", furniture, new Vector3(-benchX, benchY, 0f), benchSize);
        AddBox(scene, "Bench.Right", furniture, new Vector3(benchX, benchY, 0f), benchSize);

        var tableY = LonghouseLayout.TableHeight / 2f;
        var tableSize = new Vector3(LonghouseLayout.TableWidth, LonghouseLayout.TableHeight, LonghouseLayout.TableLength);
        var tableX = LonghouseLayout.TableCenterX;
        var tableZ = LonghouseLayout.TableCenterZ;
        AddBox(scene, "Table.Right", furniture, new Vector3(tableX, tableY, tableZ), tableSize);
        AddBox(scene, "Table.Left", furniture, new Vector3(-tableX, tableY, tableZ), tableSize);
    }

    private static void AddDoorFrame(Scene scene, MeshData timber)
    {
        var frameX = LonghouseLayout.DoorWidth / 2f + LonghouseLayout.PostSize / 2f;
        var frameHeight = LonghouseLayout.DoorHeight + 0.2f;
        var frontZ = LonghouseLayout.FrontZ;
        var size = new Vector3(LonghouseLayout.PostSize, frameHeight, LonghouseLayout.PostSize);
        AddBox(scene, "Door.Frame.Left", timber, new Vector3(-frameX, frameHeight / 2f, frontZ), size);
        AddBox(scene, "Door.Frame.Right", timber, new Vector3(frameX, frameHeight / 2f, frontZ), size);
        AddBox(
            scene,
            "Door.Frame.Header",
            timber,
            new Vector3(0f, LonghouseLayout.DoorHeight + 0.1f, frontZ),
            new Vector3(LonghouseLayout.DoorWidth + 2f * LonghouseLayout.PostSize, LonghouseLayout.BeamSize, LonghouseLayout.PostSize));
    }

    private static void AddForest(Scene scene, MeshData trunk, MeshData canopy)
    {
        var tiers = new (float Width, float Height, float CenterY)[]
        {
            (3.2f, 2.2f, 4.2f),
            (2.3f, 2.0f, 5.6f),
            (1.4f, 1.8f, 6.9f),
        };

        for (var i = 0; i < LonghouseLayout.TreeCount; i++)
        {
            var position = LonghouseLayout.TreePosition(i);
            var scale = LonghouseLayout.TreeScale(i);
            var rotation = Quaternion.CreateFromYawPitchRoll(LonghouseLayout.TreeYawRadians(i), 0f, 0f);

            var trunkSize = new Vector3(LonghouseLayout.TreeTrunkWidth, LonghouseLayout.TreeTrunkHeight, LonghouseLayout.TreeTrunkWidth) * scale;
            scene.Add(new SceneObject(
                $"Tree.{i:00}.Trunk",
                trunk,
                new Transform(position + new Vector3(0f, trunkSize.Y / 2f, 0f), rotation, trunkSize)));

            for (var t = 0; t < tiers.Length; t++)
            {
                var tier = tiers[t];
                var tierSize = new Vector3(tier.Width, tier.Height, tier.Width) * scale;
                scene.Add(new SceneObject(
                    $"Tree.{i:00}.Canopy.{t}",
                    canopy,
                    new Transform(position + new Vector3(0f, tier.CenterY * scale, 0f), rotation, tierSize)));
            }
        }
    }

    private static void AddBox(Scene scene, string name, MeshData unitMesh, Vector3 center, Vector3 size)
    {
        scene.Add(new SceneObject(name, unitMesh, new Transform(center, Quaternion.Identity, size)));
    }
}
