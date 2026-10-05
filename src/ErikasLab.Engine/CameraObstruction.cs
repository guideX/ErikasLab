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

    /// <summary>
    /// Contact skin (meters) kept between the camera sphere and an obstruction
    /// surface after a body-motion slide hit, so the solver does not land exactly
    /// on the radius-expanded surface and chatter across frames. Matches the
    /// player collision skin; small enough to be visually invisible.
    /// </summary>
    public const float CameraSlideSkinMeters = 0.01f;

    /// <summary>
    /// Maximum camera-body sweep-and-slide iterations per resolve. A head-on or
    /// shallow hit needs one, a corner two; three is a small finite bound that
    /// absorbs a two-surface corner contact without ever looping to convergence.
    /// </summary>
    public const int MaxCameraSlideIterations = 3;

    /// <summary>
    /// Maximum depenetration iterations used to push an already-overlapping
    /// camera sphere out before the sweep. Bounded and deterministic; never an
    /// unbounded resolution loop.
    /// </summary>
    public const int MaxCameraDepenetrationIterations = 4;

    /// <summary>
    /// Upper bound (meters) on total starting-overlap correction, so a corrupt
    /// debug state or numerical edge case is nudged out by at most this much
    /// instead of teleporting across the world. Larger than the deepest possible
    /// single wall penetration (wall half thickness 0.125 m + radius 0.25 m).
    /// </summary>
    public const float MaxCameraDepenetrationDistanceMeters = 1.0f;
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
/// Result of a camera obstruction query. <see cref="Fraction"/> is the earliest
/// hit parameter in [0, 1] along the queried segment; <see cref="Distance"/> is
/// the same hit expressed in meters from the segment start;
/// <see cref="Normal"/> is the world-space outward surface normal (unit length)
/// at the contact face; <see cref="Box"/> identifies the nearest blocker hit.
/// A segment that begins inside the expanded box reports fraction 0 with the
/// least-penetration outward normal.
/// </summary>
public readonly record struct SegmentObstructionHit(float Fraction, float Distance, Vector3 Normal, CameraObstructionBox Box);

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
        var bestNormal = Vector3.Zero;
        var bestBox = default(CameraObstructionBox);
        var found = false;

        foreach (var box in Boxes)
        {
            // Target-inside policy: never clamp against a box the target is in.
            if (box.ContainsPoint(target, radius))
            {
                continue;
            }

            if (TrySweepBox(box, target, segment, radius, out var fraction, out var normal) && fraction < bestFraction)
            {
                bestFraction = fraction;
                bestNormal = normal;
                bestBox = box;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        var segmentLength = MathF.Sqrt(segmentLengthSquared);
        hit = new SegmentObstructionHit(bestFraction, bestFraction * segmentLength, bestNormal, bestBox);
        return true;
    }

    /// <summary>
    /// Sweep the camera sphere center from <paramref name="start"/> along
    /// <paramref name="delta"/> against every box expanded by
    /// <paramref name="radius"/>. Returns the nearest valid hit (independent of
    /// iteration order) with the world-space outward contact normal, or no hit.
    /// Zero-length segments and non-finite inputs are safe (no hit, no NaN).
    /// Unlike <see cref="CastSegment"/> this query applies no target-inside
    /// policy: the camera body must avoid every blocker regardless of where the
    /// look target is.
    /// </summary>
    public bool Sweep(Vector3 start, Vector3 delta, float radius, out SegmentObstructionHit hit)
    {
        hit = default(SegmentObstructionHit);
        if (!IsFinite(start) || !IsFinite(delta) || !float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        var bestFraction = float.MaxValue;
        var bestNormal = Vector3.Zero;
        var bestBox = default(CameraObstructionBox);
        var found = false;

        for (var i = 0; i < Boxes.Length; i++)
        {
            var box = Boxes[i];
            if (TrySweepBox(box, start, delta, radius, out var fraction, out var normal) && fraction < bestFraction)
            {
                bestFraction = fraction;
                bestNormal = normal;
                bestBox = box;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        hit = new SegmentObstructionHit(bestFraction, bestFraction * delta.Length(), bestNormal, bestBox);
        return true;
    }

    /// <summary>
    /// Smallest world-space translation that moves the camera sphere center at
    /// <paramref name="point"/> outside the most deeply overlapped blocker.
    /// Returns false when the center is not overlapping any blocker. Picks the
    /// largest minimum translation, ties broken by list order, so the result is
    /// deterministic. The correction is a single box's minimum translation
    /// vector, not a resolved multi-box solution (the resolver iterates).
    /// </summary>
    public bool TryGetDepenetration(Vector3 point, float radius, out Vector3 correction)
    {
        correction = Vector3.Zero;
        if (!IsFinite(point) || !float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        var bestPenetration = 0f;
        var bestCorrection = Vector3.Zero;
        var found = false;

        for (var i = 0; i < Boxes.Length; i++)
        {
            if (!TryDepenetrateBox(Boxes[i], point, radius, out var candidate, out var penetration))
            {
                continue;
            }

            if (!found || penetration > bestPenetration + 1e-6f)
            {
                bestPenetration = penetration;
                bestCorrection = candidate;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        correction = bestCorrection;
        return true;
    }

    /// <summary>
    /// Earliest segment fraction in [0, 1] at which the sphere center enters
    /// the radius-expanded box, plus the world-space outward contact normal.
    /// Standard slab method in the box's local space: parallel slabs with the
    /// start outside miss; parallel slabs with the start inside impose no
    /// constraint; exact boundary contact counts as a hit (conservative, no
    /// NaN). The contact normal is the outward normal of the last slab crossed
    /// (the face actually entered through). A segment that begins inside the
    /// expanded box reports fraction 0 with the least-penetration outward
    /// normal (the resolver depenetrates first, so this is a robustness
    /// fallback, not the normal path).
    /// </summary>
    private static bool TrySweepBox(
        CameraObstructionBox box,
        Vector3 start,
        Vector3 delta,
        float radius,
        out float fraction,
        out Vector3 normal)
    {
        fraction = 0f;
        normal = Vector3.Zero;

        var inverse = Quaternion.Inverse(box.Orientation);
        var localStart = Vector3.Transform(start - box.Center, inverse);
        var localEnd = Vector3.Transform(start + delta - box.Center, inverse);
        var localDelta = localEnd - localStart;
        var half = box.HalfExtents + new Vector3(radius, radius, radius);

        var tmin = 0f;
        var tmax = 1f;
        var entryAxis = -1;
        var entrySign = 0f;

        if (!SlabAxis(localStart.X, localDelta.X, half.X, ref tmin, ref tmax, ref entryAxis, ref entrySign, 0) ||
            !SlabAxis(localStart.Y, localDelta.Y, half.Y, ref tmin, ref tmax, ref entryAxis, ref entrySign, 1) ||
            !SlabAxis(localStart.Z, localDelta.Z, half.Z, ref tmin, ref tmax, ref entryAxis, ref entrySign, 2))
        {
            return false;
        }

        if (tmin > 1f)
        {
            return false;
        }

        if (entryAxis < 0)
        {
            // The center already sits inside the radius-expanded box: report an
            // immediate contact and push out along the axis of least penetration.
            var penetrationX = half.X - MathF.Abs(localStart.X);
            var penetrationY = half.Y - MathF.Abs(localStart.Y);
            var penetrationZ = half.Z - MathF.Abs(localStart.Z);
            Vector3 localNormal;
            if (penetrationX <= penetrationY && penetrationX <= penetrationZ)
            {
                localNormal = new Vector3(localStart.X < 0f ? -1f : 1f, 0f, 0f);
            }
            else if (penetrationY <= penetrationZ)
            {
                localNormal = new Vector3(0f, localStart.Y < 0f ? -1f : 1f, 0f);
            }
            else
            {
                localNormal = new Vector3(0f, 0f, localStart.Z < 0f ? -1f : 1f);
            }

            normal = NormalizeSafe(Vector3.Transform(localNormal, box.Orientation));
            return true;
        }

        var localEntryNormal = entryAxis == 0
            ? new Vector3(entrySign, 0f, 0f)
            : entryAxis == 1
                ? new Vector3(0f, entrySign, 0f)
                : new Vector3(0f, 0f, entrySign);
        normal = NormalizeSafe(Vector3.Transform(localEntryNormal, box.Orientation));
        fraction = MathF.Max(tmin, 0f);
        return true;
    }

    private static bool SlabAxis(
        float start,
        float direction,
        float half,
        ref float tmin,
        ref float tmax,
        ref int entryAxis,
        ref float entrySign,
        int axis)
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

        if (t1 > tmin)
        {
            tmin = t1;
            entryAxis = axis;
            entrySign = direction > 0f ? -1f : 1f;
        }

        tmax = MathF.Min(tmax, t2);
        return tmin <= tmax;
    }

    private static bool TryDepenetrateBox(
        CameraObstructionBox box,
        Vector3 point,
        float radius,
        out Vector3 correction,
        out float penetration)
    {
        correction = Vector3.Zero;
        penetration = 0f;

        var inverse = Quaternion.Inverse(box.Orientation);
        var local = Vector3.Transform(point - box.Center, inverse);
        var half = box.HalfExtents;
        var closest = new Vector3(
            Math.Clamp(local.X, -half.X, half.X),
            Math.Clamp(local.Y, -half.Y, half.Y),
            Math.Clamp(local.Z, -half.Z, half.Z));
        var diff = local - closest;
        var distanceSquared = diff.LengthSquared();

        Vector3 localNormal;
        if (distanceSquared > 1e-12f)
        {
            // Center outside the box but inside the radius-expanded region:
            // push straight away from the closest point on the box.
            var distance = MathF.Sqrt(distanceSquared);
            if (distance >= radius)
            {
                return false;
            }

            localNormal = diff / distance;
            penetration = radius - distance;
        }
        else
        {
            // Center inside the box: leave through the nearest expanded face,
            // deterministically preferring the lower axis index on a tie.
            var penetrationX = half.X + radius - MathF.Abs(local.X);
            var penetrationY = half.Y + radius - MathF.Abs(local.Y);
            var penetrationZ = half.Z + radius - MathF.Abs(local.Z);
            if (penetrationX <= penetrationY && penetrationX <= penetrationZ)
            {
                localNormal = new Vector3(local.X < 0f ? -1f : 1f, 0f, 0f);
                penetration = penetrationX;
            }
            else if (penetrationY <= penetrationZ)
            {
                localNormal = new Vector3(0f, local.Y < 0f ? -1f : 1f, 0f);
                penetration = penetrationY;
            }
            else
            {
                localNormal = new Vector3(0f, 0f, local.Z < 0f ? -1f : 1f);
                penetration = penetrationZ;
            }
        }

        correction = Vector3.Transform(localNormal, box.Orientation) * penetration;
        return penetration > 0f;
    }

    private static Vector3 NormalizeSafe(Vector3 value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Result of a Phase 2Q camera-body motion resolve: the accepted world-space
/// camera position, the accepted displacement (position - start), whether any
/// contact was resolved (depenetration or slide), the number of slide
/// contacts, and the last blocker hit. All values are finite for finite
/// inputs.
/// </summary>
public readonly record struct CameraMotionResult(
    Vector3 Position,
    Vector3 Delta,
    bool Constrained,
    int HitCount,
    string? LastHitName);

/// <summary>
/// Phase 2Q sweep-and-slide resolver for the camera sphere against the static
/// camera-obstruction set. This is environment constraint, not a motion
/// authority: it takes the *requested* camera displacement (the follow
/// smoothing step) and returns the accepted portion. It never invents orbit
/// motion, never touches the control basis, and never alters gameplay.
///
/// Algorithm:
/// <list type="number">
/// <item>Bounded depenetration: if the camera sphere already overlaps a
/// blocker, push it out along the smallest translation (a small finite
/// iteration bound and a hard distance cap, deterministic tie-breaking).</item>
/// <item>Bounded sweep-and-slide: sweep the remaining segment, move to the
/// safe contact (plus skin), project out the inward normal component, and
/// continue with the tangential remainder.</item>
/// </list>
/// No bounce, no restitution, no spring forces, no friction simulation.
/// </summary>
public static class CameraCollisionResolver
{
    /// <summary>
    /// Resolve the requested camera displacement for the sphere at
    /// <paramref name="start"/>. Non-finite or non-positive-radius inputs are
    /// rejected safely (no movement, no NaN). The returned delta is the
    /// accepted portion of <paramref name="requestedDelta"/>; it equals the
    /// requested delta exactly when nothing is hit, so an unobstructed route is
    /// a bit-identical no-op.
    /// </summary>
    public static CameraMotionResult Resolve(
        CameraObstructionSet set,
        Vector3 start,
        Vector3 requestedDelta,
        float radius,
        float skin = CameraCollisionPolicy.CameraSlideSkinMeters,
        int maxSlideIterations = CameraCollisionPolicy.MaxCameraSlideIterations,
        int maxDepenetrationIterations = CameraCollisionPolicy.MaxCameraDepenetrationIterations,
        float maxDepenetrationDistance = CameraCollisionPolicy.MaxCameraDepenetrationDistanceMeters)
    {
        if (!IsFinite(start) || !IsFinite(requestedDelta) ||
            !float.IsFinite(radius) || radius <= 0f ||
            !float.IsFinite(skin) || skin < 0f ||
            maxSlideIterations < 0 || maxDepenetrationIterations < 0 ||
            !float.IsFinite(maxDepenetrationDistance) || maxDepenetrationDistance < 0f)
        {
            return new CameraMotionResult(start, Vector3.Zero, false, 0, null);
        }

        var position = start;
        var constrained = false;
        var hitCount = 0;
        string? lastHitName = null;

        // Bounded starting-overlap depenetration.
        var corrected = 0f;
        for (var i = 0; i < maxDepenetrationIterations; i++)
        {
            if (!set.TryGetDepenetration(position, radius, out var correction))
            {
                break;
            }

            var magnitude = correction.Length();
            if (magnitude < 1e-6f)
            {
                break;
            }

            var allowed = MathF.Min(magnitude, maxDepenetrationDistance - corrected);
            if (allowed <= 0f)
            {
                break;
            }

            position += correction / magnitude * allowed;
            corrected += allowed;
            constrained = true;
            if (corrected >= maxDepenetrationDistance)
            {
                break;
            }
        }

        // Bounded sweep-and-slide.
        var remaining = requestedDelta;
        for (var i = 0; i < maxSlideIterations; i++)
        {
            if (remaining.LengthSquared() < 1e-12f)
            {
                break;
            }

            if (!set.Sweep(position, remaining, radius, out var hit))
            {
                position += remaining;
                remaining = Vector3.Zero;
                break;
            }

            constrained = true;
            hitCount++;
            lastHitName = hit.Box.Name;

            position += remaining * hit.Fraction;
            position += hit.Normal * skin;

            var leftover = remaining * (1f - hit.Fraction);
            var intoSurface = Vector3.Dot(leftover, hit.Normal);
            if (intoSurface < 0f)
            {
                leftover -= hit.Normal * intoSurface;
            }

            remaining = leftover;
        }

        if (!IsFinite(position))
        {
            return new CameraMotionResult(start, Vector3.Zero, false, 0, null);
        }

        // When nothing was hit, report the requested delta verbatim so an
        // unobstructed route is a bit-identical no-op (the clear-space A/B
        // regression), not merely a numerically close reconstruction.
        var delta = constrained ? position - start : requestedDelta;
        return new CameraMotionResult(position, delta, constrained, hitCount, lastHitName);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
