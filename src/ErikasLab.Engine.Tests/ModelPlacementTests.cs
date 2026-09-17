using System.Numerics;
using ErikasLab.Engine;
using Xunit;

namespace ErikasLab.Engine.Tests;

public sealed class ModelPlacementTests
{
    [Fact]
    public void UniformScaleMapsNativeHeightToTargetHeight()
    {
        Assert.Equal(1.7f / 180.1f, ModelPlacement.UniformScaleForTargetHeight(180.1f, 1.7f), precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UniformScaleRejectsNonPositiveNativeHeight(float nativeHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModelPlacement.UniformScaleForTargetHeight(nativeHeight, 1.7f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UniformScaleRejectsNonPositiveTargetHeight(float targetHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModelPlacement.UniformScaleForTargetHeight(180.1f, targetHeight));
    }

    [Fact]
    public void LiftToGroundRestsNativeMinimumOnZero()
    {
        var scale = ModelPlacement.UniformScaleForTargetHeight(180.1f, 1.7f);
        var lift = ModelPlacement.LiftToGround(-0.6f, scale);
        Assert.Equal(0.6f * scale, lift, precision: 6);
        Assert.Equal(0f, (-0.6f * scale) + lift, precision: 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void LiftToGroundRejectsNonPositiveScale(float scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelPlacement.LiftToGround(-0.6f, scale));
    }

    [Fact]
    public void YawToFacePlusZLeavesPlusZFacingAlone()
    {
        Assert.Equal(0f, ModelPlacement.YawToFacePlusZ(Vector3.UnitZ), precision: 6);
    }

    [Fact]
    public void YawToFacePlusZRotatesMinusZByHalfTurn()
    {
        Assert.Equal(MathF.PI, MathF.Abs(ModelPlacement.YawToFacePlusZ(-Vector3.UnitZ)), precision: 5);
    }

    [Fact]
    public void YawToFacePlusZRotatesPlusXByQuarterTurn()
    {
        var yaw = ModelPlacement.YawToFacePlusZ(Vector3.UnitX);
        var rotated = Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromYawPitchRoll(yaw, 0, 0));
        Assert.Equal(0f, rotated.X, precision: 5);
        Assert.Equal(1f, rotated.Z, precision: 5);
    }

    [Fact]
    public void YawToFacePlusZRejectsDegenerateFacing()
    {
        Assert.Throws<ArgumentException>(() => ModelPlacement.YawToFacePlusZ(Vector3.UnitY));
    }
}
