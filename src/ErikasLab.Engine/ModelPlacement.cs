using System.Numerics;

namespace ErikasLab.Engine;

/// <summary>
/// Portable model-placement math: world-unit conversion and ground alignment.
/// Pure functions so they stay testable without a GPU or content pipeline.
/// </summary>
public static class ModelPlacement
{
    /// <summary>
    /// Uniform scale that maps a model's native height to a target world height.
    /// </summary>
    public static float UniformScaleForTargetHeight(float nativeHeightUnits, float targetHeightUnits)
    {
        if (nativeHeightUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeHeightUnits), "Native height must be positive.");
        }

        if (targetHeightUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeightUnits), "Target height must be positive.");
        }

        return targetHeightUnits / nativeHeightUnits;
    }

    /// <summary>
    /// World-space Y position that puts the model's native lower bound exactly
    /// on the ground plane (y = 0).
    /// </summary>
    public static float LiftToGround(float nativeMinYUnits, float scale)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive.");
        }

        return -nativeMinYUnits * scale;
    }

    /// <summary>
    /// Yaw (radians, Y-up) that rotates a bind-pose facing vector onto world
    /// +Z. A model already facing +Z yields ~0.
    /// </summary>
    public static float YawToFacePlusZ(Vector3 facing)
    {
        if (facing.X == 0 && facing.Z == 0)
        {
            throw new ArgumentException("Facing vector must have a horizontal component.", nameof(facing));
        }

        return -MathF.Atan2(facing.X, facing.Z);
    }
}
