using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Bone-by-bone local-pose blending for locomotion crossfades (Phase 2F).
///
/// The animation source carries no scale (translation and rotation tracks
/// only), so blending happens in (translation, rotation) space and is composed
/// to a matrix once at the end. Translation blends linearly; rotation blends
/// along the shortest quaternion arc and is normalized, so every output matrix
/// stays a rigid transform (no matrix-element lerp shear) and finite.
/// </summary>
public static class PoseBlender
{
    /// <summary>
    /// Blend source and destination local poses into <paramref name="result"/>.
    /// At <paramref name="alpha"/> 0 the source is copied exactly; at 1 the
    /// destination is copied exactly; in between each bone blends.
    /// </summary>
    public static void BlendLocal(
        Vector3[] sourceTranslations,
        Quaternion[] sourceRotations,
        Vector3[] destinationTranslations,
        Quaternion[] destinationRotations,
        float alpha,
        Matrix4x4[] result)
    {
        ArgumentNullException.ThrowIfNull(sourceTranslations);
        ArgumentNullException.ThrowIfNull(sourceRotations);
        ArgumentNullException.ThrowIfNull(destinationTranslations);
        ArgumentNullException.ThrowIfNull(destinationRotations);
        ArgumentNullException.ThrowIfNull(result);

        var count = sourceTranslations.Length;
        if (sourceRotations.Length != count
            || destinationTranslations.Length != count
            || destinationRotations.Length != count
            || result.Length != count)
        {
            throw new ArgumentException("All blend arrays must have equal length.");
        }

        if (alpha <= 0f)
        {
            Compose(sourceTranslations, sourceRotations, result);
            return;
        }

        if (alpha >= 1f)
        {
            Compose(destinationTranslations, destinationRotations, result);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var translation = Vector3.Lerp(sourceTranslations[i], destinationTranslations[i], alpha);
            var rotation = SlerpShortest(sourceRotations[i], destinationRotations[i], alpha);
            result[i] = Skeleton.Compose(rotation, translation);
        }
    }

    /// <summary>
    /// Normalized shortest-path spherical interpolation between two rotations.
    /// Mirrors the destination hemisphere when needed, degenerates to a
    /// normalized lerp for near-parallel inputs, and never returns a NaN.
    /// </summary>
    public static Quaternion SlerpShortest(Quaternion from, Quaternion to, float amount)
    {
        from = NormalizeOrIdentity(from);
        to = NormalizeOrIdentity(to);

        var dot = Quaternion.Dot(from, to);
        if (dot < 0f)
        {
            to = Quaternion.Negate(to);
            dot = -dot;
        }

        if (dot > 0.9995f)
        {
            var lerp = new Quaternion(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount,
                from.W + (to.W - from.W) * amount);
            return NormalizeOrIdentity(lerp);
        }

        dot = Math.Clamp(dot, -1f, 1f);
        var theta0 = MathF.Acos(dot);
        var theta = theta0 * amount;
        var sinTheta = MathF.Sin(theta);
        var sinTheta0 = MathF.Sin(theta0);
        var scaleFrom = MathF.Cos(theta) - dot * sinTheta / sinTheta0;
        var scaleTo = sinTheta / sinTheta0;
        return NormalizeOrIdentity(new Quaternion(
            from.X * scaleFrom + to.X * scaleTo,
            from.Y * scaleFrom + to.Y * scaleTo,
            from.Z * scaleFrom + to.Z * scaleTo,
            from.W * scaleFrom + to.W * scaleTo));
    }

    private static void Compose(Vector3[] translations, Quaternion[] rotations, Matrix4x4[] result)
    {
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Skeleton.Compose(rotations[i], translations[i]);
        }
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? Quaternion.Normalize(value) : Quaternion.Identity;
    }
}
