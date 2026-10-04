using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2M centralized camera-obstruction policy constants. One place for the
/// camera collision radius, the surface padding used when clamping against a
/// hit, the flat camera floor height, and the tiny numerical minimum camera
/// distance, so no padding/radius values are scattered through the code.
/// </summary>
public static class CameraCollisionPolicy
{
    /// <summary>
    /// Radius (meters) of the sphere the camera is approximated by during the
    /// obstruction query. The oriented boxes are Minkowski-expanded by this
    /// radius before the segment test, so the near plane does not scrape walls.
    /// </summary>
    public const float CollisionRadiusMeters = 0.25f;

    /// <summary>
    /// Extra distance (meters) kept between the camera sphere surface and the
    /// obstruction surface when clamping, to avoid numerical contact flicker.
    /// Applied once, after the radius-expanded hit (the radius is not subtracted
    /// twice).
    /// </summary>
    public const float SurfacePaddingMeters = 0.05f;

    /// <summary>
    /// Flat camera floor height (meters) at the established ground plane
    /// (y = 0). A single hard constraint; no terrain/heightmap queries. Needed
    /// because the positive pitch limit would otherwise put the camera below
    /// the flat ground.
    /// </summary>
    public const float CameraFloorHeightMeters = 0.15f;

    /// <summary>
    /// Tiny numerical minimum (meters) for the final camera-to-target distance,
    /// so the camera never exactly coincides with the look target (which would
    /// degenerate the view direction). Collision safety has priority: an
    /// obstruction may pull the camera much closer than the nominal distance.
    /// </summary>
    public const float MinCameraDistanceMeters = 0.05f;
}

/// <summary>
/// Phase 2M static camera blocker: one oriented box (OBB). Walls, roof slabs,
/// and tree trunks are all boxes or well approximated by them, and the roof
/// slabs are genuinely rotated, so oriented boxes (not world AABBs) keep the
/// obstruction set faithful to the environment geometry. Immutable value type;
/// the whole set is built once at session construction and never rebuilt.
/// </summary>
/// <param name="Name">Stable identifier (matches the scene object it mirrors) for tests/debugging.</param>
/// <param name="Center">World-space box center (meters).</param>
/// <param name="HalfExtents">Positive half sizes along the local axes (meters).</param>
/// <param name="Orientation">Rigid rotation from local box axes to world axes (unit quaternion).</param>
public readonly record struct CameraObstructionBox(string Name, Vector3 Center, Vector3 HalfExtents, Quaternion Orientation)
{
    /// <summary>
    /// True when <paramref name="point"/> lies inside the box expanded by
    /// <paramref name="radius"/> meters on every side (inclusive bounds, so a
    /// point exactly on the expanded boundary counts as inside).
    /// </summary>
    public bool ContainsPoint(Vector3 point, float radius)
    {
        var local = ToLocal(point);
        return MathF.Abs(local.X) <= HalfExtents.X + radius
            && MathF.Abs(local.Y) <= HalfExtents.Y + radius
            && MathF.Abs(local.Z) <= HalfExtents.Z + radius;
    }

    /// <summary>Transform a world point into the box's local orientation (rigid: rotation + translation only).</summary>
    public Vector3 ToLocal(Vector3 point) => Vector3.Transform(point - Center, Quaternion.Inverse(Orientation));
}

/// <summary>
/// Result of a target-to-camera segment obstruction query. <see cref="Fraction"/>
/// is the earliest hit parameter in [0, 1] along the queried segment;
/// <see cref="Distance"/> is the same hit expressed in meters from the segment
/// start. <see cref="Box"/> identifies the nearest blocker hit.
/// </summary>
public readonly record struct SegmentObstructionHit(float Fraction, float Distance, CameraObstructionBox Box);

/// <summary>
/// Phase 2M bounded static camera-obstruction collection: an immutable list of
/// oriented boxes built once alongside the environment. The per-frame query is
/// O(box count), allocation-free, and uses a Minkowski-expanded slab test
/// (segment vs. radius-expanded OBB in the box's local space). Because the
/// transform is rigid, the segment parameter stays meaningful after the local
/// transform.
///
/// Target-inside policy: a box whose radius-expanded volume contains the segment
/// start (the look target) is ignored for that query. Player collision does not
/// exist yet, so Erika may stand inside a wall; this keeps the camera from
/// collapsing to zero distance or emitting NaNs in that case. It is a robust
/// placeholder, not a penetration solution.
/// </summary>
/// <param name="Boxes">The static blocker set (finite, non-empty in practice).</param>
public readonly record struct CameraObstructionSet(CameraObstructionBox[] Boxes)
{
    /// <summary>Number of static camera blockers.</summary>
    public int Count => Boxes.Length;

    /// <summary>Blocker at <paramref name="index"/> (for tests/diagnostics).</summary>
    public CameraObstructionBox this[int index] => Boxes[index];

    /// <summary>
    /// Cast the segment <paramref name="target"/> (look target) to
    /// <paramref name="candidate"/> (desired camera position) against every box
    /// expanded by <see cref="CameraCollisionPolicy.CollisionRadiusMeters"/>.
    /// Returns the nearest valid hit (independent of iteration order), or no
    /// hit. Zero-length segments and non-finite inputs are safe (no hit, no
    /// NaN). Boxes containing the target are skipped (target-inside policy).
    /// </summary>
    public bool CastSegment(Vector3 target, Vector3 candidate, out SegmentObstructionHit hit)
    {
        hit = default(SegmentObstructionHit);
        if (!IsFinite(target) || !IsFinite(candidate))
        {
            return false;
        }

        var segment = candidate - target;
        var segmentLengthSquared = segment.LengthSquared();
        if (segmentLengthSquared < 1e-12f)
        {
            return false;
        }

        var radius = CameraCollisionPolicy.CollisionRadiusMeters;
        var bestFraction = float.MaxValue;
        var bestBox = default(CameraObstructionBox);
        var found = false;

        foreach (var box in Boxes)
        {
            // Target-inside policy: never clamp against a box the target is in.
            if (box.ContainsPoint(target, radius))
            {
                continue;
            }

            if (TryEnterFraction(box, target, candidate, radius, out var fraction) && fraction < bestFraction)
            {
                bestFraction = fraction;
                bestBox = box;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        var segmentLength = MathF.Sqrt(segmentLengthSquared);
        hit = new SegmentObstructionHit(bestFraction, bestFraction * segmentLength, bestBox);
        return true;
    }

    /// <summary>
    /// Earliest segment fraction in [0, 1] at which the segment enters the
    /// radius-expanded box, or false when it does not. Standard slab method in
    /// the box's local space: parallel slabs with the start outside miss;
    /// parallel slabs with the start inside impose no constraint; exact
    /// boundary contact counts as a hit (conservative, no NaN).
    /// </summary>
    private static bool TryEnterFraction(
        CameraObstructionBox box,
        Vector3 target,
        Vector3 candidate,
        float radius,
        out float fraction)
    {
        fraction = 0f;
        var inverse = Quaternion.Inverse(box.Orientation);
        var localStart = Vector3.Transform(target - box.Center, inverse);
        var localEnd = Vector3.Transform(candidate - box.Center, inverse);
        var direction = localEnd - localStart;

        var tmin = 0f;
        var tmax = 1f;

        if (!SlabAxis(localStart.X, direction.X, box.HalfExtents.X + radius, ref tmin, ref tmax) ||
            !SlabAxis(localStart.Y, direction.Y, box.HalfExtents.Y + radius, ref tmin, ref tmax) ||
            !SlabAxis(localStart.Z, direction.Z, box.HalfExtents.Z + radius, ref tmin, ref tmax))
        {
            return false;
        }

        if (tmin > 1f)
        {
            return false;
        }

        fraction = MathF.Max(tmin, 0f);
        return true;
    }

    private static bool SlabAxis(float start, float direction, float half, ref float tmin, ref float tmax)
    {
        const float parallelEpsilon = 1e-9f;
        if (MathF.Abs(direction) < parallelEpsilon)
        {
            return start >= -half && start <= half;
        }

        var t1 = (-half - start) / direction;
        var t2 = (half - start) / direction;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tmin = MathF.Max(tmin, t1);
        tmax = MathF.Min(tmax, t2);
        return tmin <= tmax;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
