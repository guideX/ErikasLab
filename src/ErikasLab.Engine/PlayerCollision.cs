using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Phase 2N centralized player-collision policy constants. One place for the
/// player circle radius, the contact skin, the bounded slide/depenetration
/// iteration limits, and the depenetration distance bound, so no collision
/// tuning values are scattered through the code.
///
/// This is deliberately *not* a physics engine: there is no mass, velocity,
/// restitution, friction, gravity, or vertical motion. It is static
/// character-vs-environment constraint in the flat XZ plane only.
/// </summary>
public static class PlayerCollisionPolicy
{
    /// <summary>
    /// Radius (meters) of the horizontal circle the player is approximated by.
    /// 0.30 m is wider than Erika's shoulders, leaves a positive usable doorway
    /// corridor (1.2 m door - 2 * 0.30 m = 0.60 m) and keeps her from scraping
    /// posts/walls. The player is never a bone/mesh/capsule collider.
    /// </summary>
    public const float PlayerCollisionRadiusMeters = 0.30f;

    /// <summary>
    /// Contact skin (meters) kept between the player circle and a blocker
    /// surface after a hit, so the solver does not land exactly on the surface
    /// and chatter across frames. Small enough (1 cm) to be visually invisible.
    /// </summary>
    public const float PlayerCollisionSkinMeters = 0.01f;

    /// <summary>
    /// Maximum sweep-and-slide iterations per resolve. A head-on or shallow
    /// hit needs one, a corner two; four is a small finite bound that absorbs
    /// numerical contact without ever looping to convergence.
    /// </summary>
    public const int MaxSlideIterations = 4;

    /// <summary>
    /// Maximum depenetration iterations used to push an already-overlapping
    /// player center out before the sweep. Bounded and deterministic; never an
    /// unbounded resolution loop.
    /// </summary>
    public const int MaxDepenetrationIterations = 4;

    /// <summary>
    /// Upper bound (meters) on total starting-overlap correction, so a corrupt
    /// spawn or debug position is nudged out by at most this much instead of
    /// teleporting across the world. Larger than the deepest possible single
    /// wall penetration (wall half thickness 0.125 m + radius 0.30 m).
    /// </summary>
    public const float MaxDepenetrationDistanceMeters = 1.0f;
}

/// <summary>
/// Phase 2N static player blocker: one oriented box in the flat XZ plane.
/// Walls and tree trunks are axis-aligned in the current longhouse, but the
/// portable representation carries a yaw so rotated blockers are supported
/// without a type change. Immutable value type; the whole set is built once at
/// session construction and never rebuilt.
/// </summary>
/// <param name="Name">Stable identifier (matches the scene object it mirrors) for tests/diagnostics.</param>
/// <param name="CenterXZ">World-space center in the XZ plane (meters): <c>X = X</c>, <c>Y = Z</c>.</param>
/// <param name="HalfExtentsXZ">Positive half sizes along the local XZ axes (meters).</param>
/// <param name="YawRadians">Rigid rotation of the box about the world Y axis (radians).</param>
public readonly record struct PlayerCollisionBox(string Name, Vector2 CenterXZ, Vector2 HalfExtentsXZ, float YawRadians)
{
    /// <summary>Transform a world XZ point into the box's local axes (rigid: rotation + translation only).</summary>
    public Vector2 ToLocal(Vector2 point)
    {
        var d = point - CenterXZ;
        if (YawRadians == 0f)
        {
            return d;
        }

        var cos = MathF.Cos(YawRadians);
        var sin = MathF.Sin(YawRadians);
        return new Vector2(cos * d.X - sin * d.Y, sin * d.X + cos * d.Y);
    }

    /// <summary>Rotate a local-space vector back into world XZ axes.</summary>
    public Vector2 LocalToWorld(Vector2 local)
    {
        if (YawRadians == 0f)
        {
            return local;
        }

        var cos = MathF.Cos(YawRadians);
        var sin = MathF.Sin(YawRadians);
        return new Vector2(cos * local.X + sin * local.Y, -sin * local.X + cos * local.Y);
    }
}

/// <summary>
/// Earliest circle-vs-blocker contact along a swept segment. <see cref="Fraction"/>
/// is the hit parameter in [0, 1] along the queried segment; <see cref="Normal"/>
/// is the world-space outward surface normal (unit length) at the contact;
/// <see cref="Box"/> identifies the blocker hit.
/// </summary>
public readonly record struct PlayerSweepHit(float Fraction, Vector2 Normal, PlayerCollisionBox Box);

/// <summary>
/// Phase 2N immutable static player-collision collection built once alongside
/// the environment. The per-frame query is O(box count), allocation-free, and
/// uses a Minkowski-expanded slab test (player-center segment vs. box expanded
/// by the player radius, in the box's local space). Because the transform is
/// rigid, the segment parameter stays meaningful after the local transform.
///
/// This type only answers geometric questions (sweep, overlap); the actual
/// sweep-and-slide response lives in <see cref="PlayerCollisionResolver"/>.
/// </summary>
/// <param name="Boxes">The static blocker set (finite, non-empty in practice).</param>
public readonly record struct PlayerCollisionSet(PlayerCollisionBox[] Boxes)
{
    /// <summary>Number of static player blockers.</summary>
    public int Count => Boxes.Length;

    /// <summary>Blocker at <paramref name="index"/> (for tests/diagnostics).</summary>
    public PlayerCollisionBox this[int index] => Boxes[index];

    /// <summary>
    /// True when the player circle centered at <paramref name="point"/> with
    /// <paramref name="radius"/> overlaps any blocker's radius-expanded volume
    /// (inclusive boundary). Used for spawn validation and diagnostics.
    /// </summary>
    public bool ContainsPoint(Vector2 point, float radius)
    {
        foreach (var box in Boxes)
        {
            var local = box.ToLocal(point);
            if (MathF.Abs(local.X) <= box.HalfExtentsXZ.X + radius &&
                MathF.Abs(local.Y) <= box.HalfExtentsXZ.Y + radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sweep the player circle center from <paramref name="start"/> along
    /// <paramref name="delta"/> against every box expanded by
    /// <paramref name="radius"/>. Returns the nearest valid hit (independent of
    /// iteration order), or no hit. Zero-length segments and non-finite inputs
    /// are safe (no hit, no NaN).
    /// </summary>
    public bool Sweep(Vector2 start, Vector2 delta, float radius, out PlayerSweepHit hit)
    {
        hit = default;
        if (!IsFinite(start) || !IsFinite(delta) || !float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        var bestFraction = float.MaxValue;
        var bestNormal = Vector2.Zero;
        var bestBox = default(PlayerCollisionBox);
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

        hit = new PlayerSweepHit(bestFraction, bestNormal, bestBox);
        return true;
    }

    /// <summary>
    /// Smallest world-space translation that moves the player circle center at
    /// <paramref name="point"/> outside the most deeply overlapped blocker.
    /// Returns false when the center is not overlapping any blocker. Picks the
    /// largest penetration, ties broken by list order, so the result is
    /// deterministic. The correction is a single box's minimum translation
    /// vector, not a resolved multi-box solution (the resolver iterates).
    /// </summary>
    public bool TryGetDepenetration(Vector2 point, float radius, out Vector2 correction)
    {
        correction = Vector2.Zero;
        if (!IsFinite(point) || !float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        var bestPenetration = 0f;
        var bestCorrection = Vector2.Zero;
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
    /// Earliest segment fraction in [0, 1] at which the player circle enters
    /// the radius-expanded box, plus the world-space outward contact normal.
    /// Standard slab method in the box's local space: parallel slabs with the
    /// start outside miss; parallel slabs with the start inside impose no
    /// constraint; exact boundary contact counts as a hit (conservative, no
    /// NaN). A segment that begins inside the expanded box reports fraction 0
    /// with the least-penetration outward normal (the resolver depenetrates
    /// first, so this is a robustness fallback, not the normal path).
    /// </summary>
    private static bool TrySweepBox(
        PlayerCollisionBox box,
        Vector2 start,
        Vector2 delta,
        float radius,
        out float fraction,
        out Vector2 normal)
    {
        fraction = 0f;
        normal = Vector2.Zero;

        var localStart = box.ToLocal(start);
        var localEnd = box.ToLocal(start + delta);
        var localDelta = localEnd - localStart;
        var half = box.HalfExtentsXZ + new Vector2(radius, radius);

        var tmin = 0f;
        var tmax = 1f;
        var entryAxis = -1;
        var entrySign = 0f;

        if (!Slab(localStart.X, localDelta.X, half.X, ref tmin, ref tmax, ref entryAxis, ref entrySign, 0) ||
            !Slab(localStart.Y, localDelta.Y, half.Y, ref tmin, ref tmax, ref entryAxis, ref entrySign, 1))
        {
            return false;
        }

        if (entryAxis < 0)
        {
            // The center already sits inside the radius-expanded box: report an
            // immediate contact and push out along the axis of least
            // penetration, deterministically preferring the X axis on a tie.
            var penetrationX = half.X - MathF.Abs(localStart.X);
            var penetrationY = half.Y - MathF.Abs(localStart.Y);
            Vector2 localNormal;
            if (penetrationX <= penetrationY)
            {
                localNormal = new Vector2(localStart.X < 0f ? -1f : 1f, 0f);
            }
            else
            {
                localNormal = new Vector2(0f, localStart.Y < 0f ? -1f : 1f);
            }

            normal = NormalizeSafe(box.LocalToWorld(localNormal));
            return true;
        }

        var localEntryNormal = entryAxis == 0
            ? new Vector2(entrySign, 0f)
            : new Vector2(0f, entrySign);
        normal = NormalizeSafe(box.LocalToWorld(localEntryNormal));
        fraction = MathF.Max(tmin, 0f);
        return true;
    }

    private static bool Slab(
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
        PlayerCollisionBox box,
        Vector2 point,
        float radius,
        out Vector2 correction,
        out float penetration)
    {
        correction = Vector2.Zero;
        penetration = 0f;

        var local = box.ToLocal(point);
        var half = box.HalfExtentsXZ;
        var closest = new Vector2(
            Math.Clamp(local.X, -half.X, half.X),
            Math.Clamp(local.Y, -half.Y, half.Y));
        var diff = local - closest;
        var distanceSquared = diff.LengthSquared();

        Vector2 localNormal;
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
            // deterministically preferring the X axis on a tie.
            var penetrationX = (half.X + radius) - MathF.Abs(local.X);
            var penetrationY = (half.Y + radius) - MathF.Abs(local.Y);
            if (penetrationX <= penetrationY)
            {
                localNormal = new Vector2(local.X < 0f ? -1f : 1f, 0f);
                penetration = penetrationX;
            }
            else
            {
                localNormal = new Vector2(0f, local.Y < 0f ? -1f : 1f);
                penetration = penetrationY;
            }
        }

        correction = box.LocalToWorld(localNormal) * penetration;
        return penetration > 0f;
    }

    private static Vector2 NormalizeSafe(Vector2 value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : Vector2.Zero;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}

/// <summary>
/// Result of a Phase 2N player-collision resolve: the accepted world XZ
/// position, the accepted displacement (position - start), whether any contact
/// was resolved (depenetration or sweep), the number of sweep contacts, and the
/// last blocker hit. All values are finite for finite inputs.
/// </summary>
public readonly record struct PlayerCollisionResult(
    Vector2 Position,
    Vector2 Delta,
    bool Constrained,
    int HitCount,
    string? LastHitName);

/// <summary>
/// Phase 2N sweep-and-slide resolver for the player circle against the static
/// player-collision set. This is environment constraint, not a motion
/// authority: it takes the *requested* displacement produced by the authored
/// root-motion stack and returns the accepted portion. It never invents
/// velocity, never rotates the character, and never alters gameplay speed.
///
/// Algorithm:
/// <list type="number">
/// <item>Bounded depenetration: if the center already overlaps a blocker,
/// push it out along the smallest translation (a small finite iteration
/// bound and a hard distance cap, deterministic tie-breaking).</item>
/// <item>Bounded sweep-and-slide: sweep the remaining segment, move to the
/// safe contact (minus skin), project out the inward normal component, and
/// continue with the tangential remainder.</item>
/// </list>
/// No gravity, no vertical motion, no restitution, no friction simulation.
/// </summary>
public static class PlayerCollisionResolver
{
    /// <summary>
    /// Resolve the requested displacement for a player circle at
    /// <paramref name="start"/>. Non-finite or non-positive-radius inputs are
    /// rejected safely (no movement, no NaN). The returned delta is the
    /// accepted portion of <paramref name="requestedDelta"/>; it equals the
    /// requested delta exactly when nothing is hit.
    /// </summary>
    public static PlayerCollisionResult Resolve(
        PlayerCollisionSet set,
        Vector2 start,
        Vector2 requestedDelta,
        float radius,
        float skin = PlayerCollisionPolicy.PlayerCollisionSkinMeters,
        int maxSlideIterations = PlayerCollisionPolicy.MaxSlideIterations,
        int maxDepenetrationIterations = PlayerCollisionPolicy.MaxDepenetrationIterations,
        float maxDepenetrationDistance = PlayerCollisionPolicy.MaxDepenetrationDistanceMeters)
    {
        if (!IsFinite(start) || !IsFinite(requestedDelta) ||
            !float.IsFinite(radius) || radius <= 0f ||
            !float.IsFinite(skin) || skin < 0f ||
            maxSlideIterations < 0 || maxDepenetrationIterations < 0 ||
            !float.IsFinite(maxDepenetrationDistance) || maxDepenetrationDistance < 0f)
        {
            return new PlayerCollisionResult(start, Vector2.Zero, false, 0, null);
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
                remaining = Vector2.Zero;
                break;
            }

            constrained = true;
            hitCount++;
            lastHitName = hit.Box.Name;

            position += remaining * hit.Fraction;
            position += hit.Normal * skin;

            var leftover = remaining * (1f - hit.Fraction);
            var intoSurface = Vector2.Dot(leftover, hit.Normal);
            if (intoSurface < 0f)
            {
                leftover -= hit.Normal * intoSurface;
            }

            remaining = leftover;
        }

        if (!IsFinite(position))
        {
            return new PlayerCollisionResult(start, Vector2.Zero, false, 0, null);
        }

        // When nothing was hit, report the requested delta verbatim so an
        // unobstructed route is a bit-identical no-op (the clear-space A/B
        // regression), not merely a numerically close reconstruction.
        var delta = constrained ? position - start : requestedDelta;
        return new PlayerCollisionResult(position, delta, constrained, hitCount, lastHitName);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
