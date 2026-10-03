using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2F locomotion crossfades and smooth yaw. Covers blend math, the
/// transition lifecycle, interruption, root-motion authority during blends, and
/// frame-rate-independent turning.
/// </summary>
public sealed class LocomotionBlendTests
{
    // --- shared fixtures -------------------------------------------------

    private static Skeleton TestSkeleton() => new([
        new SkeletonBone("mixamorig:Hips", Skeleton.NoParent, new Vector3(0, 100, 0), Quaternion.Identity),
        new SkeletonBone("mixamorig:Spine", 0, new Vector3(0, 10, 0), Quaternion.Identity),
    ]);

    private static AnimationClip Idle() => new(
        "idle_looking_around", 4.0f, 30f, [
            new AnimationChannel(0, [0f, 4f],
                [Vector3.Zero, Vector3.Zero],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Walk() => new(
        "walk", 1.0f, 30f, [
            new AnimationChannel(0, [0f, 1f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Run() => new(
        "run", 0.5f, 30f, [
            new AnimationChannel(0, [0f, 0.5f],
                [Vector3.Zero, new Vector3(0, 0, 100)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip RotatingClip(string name, float duration, float angle) => new(
        name, duration, 30f, [
            new AnimationChannel(1, [0f, duration], null,
                [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle)]),
        ]);

    private static GameSession SessionWithClips()
    {
        var session = new GameSession();
        session.SetAnimationData(TestSkeleton(), Idle(), Walk(), Run());
        return session;
    }

    private static InputState Move(
        bool forward = false,
        bool back = false,
        bool left = false,
        bool right = false,
        bool sprint = false,
        bool idle = false,
        bool walk = false,
        bool run = false) => new(
        MoveForward: forward, MoveBackward: back, StrafeLeft: left, StrafeRight: right,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        SelectIdle: idle, SelectWalk: walk, SelectRun: run, Sprint: sprint);

    private static InputState NoInput() => Move();

    private static void AssertFinite(Matrix4x4 value)
    {
        Assert.True(float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14));
        Assert.True(float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24));
        Assert.True(float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34));
        Assert.True(float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44));
    }

    // --- blend math ------------------------------------------------------

    [Fact]
    public void BlendAlphaZeroEqualsSourceExactly()
    {
        var skeleton = TestSkeleton();
        var source = RotatingClip("src", 1f, 0.7f);
        var destination = RotatingClip("dst", 1f, -1.2f);
        var count = skeleton.BoneCount;

        var sourceT = new Vector3[count];
        var sourceR = new Quaternion[count];
        var destinationT = new Vector3[count];
        var destinationR = new Quaternion[count];
        AnimationEvaluator.EvaluateLocalTransforms(skeleton, source, 0.25f, sourceT, sourceR);
        AnimationEvaluator.EvaluateLocalTransforms(skeleton, destination, 0.6f, destinationT, destinationR);

        var expected = new Matrix4x4[count];
        AnimationEvaluator.EvaluateLocal(skeleton, source, 0.25f, expected);

        var result = new Matrix4x4[count];
        PoseBlender.BlendLocal(sourceT, sourceR, destinationT, destinationR, 0f, result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BlendAlphaOneEqualsDestinationExactly()
    {
        var skeleton = TestSkeleton();
        var source = RotatingClip("src", 1f, 0.7f);
        var destination = RotatingClip("dst", 1f, -1.2f);
        var count = skeleton.BoneCount;

        var sourceT = new Vector3[count];
        var sourceR = new Quaternion[count];
        var destinationT = new Vector3[count];
        var destinationR = new Quaternion[count];
        AnimationEvaluator.EvaluateLocalTransforms(skeleton, source, 0.25f, sourceT, sourceR);
        AnimationEvaluator.EvaluateLocalTransforms(skeleton, destination, 0.6f, destinationT, destinationR);

        var expected = new Matrix4x4[count];
        AnimationEvaluator.EvaluateLocal(skeleton, destination, 0.6f, expected);

        var result = new Matrix4x4[count];
        PoseBlender.BlendLocal(sourceT, sourceR, destinationT, destinationR, 1f, result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BlendMidpointTranslationIsLinear()
    {
        var sourceT = new Vector3[] { new(0, 0, 0), new(2, 0, 0) };
        var sourceR = new Quaternion[] { Quaternion.Identity, Quaternion.Identity };
        var destinationT = new Vector3[] { new(10, 0, 4), new(2, 20, 0) };
        var destinationR = new Quaternion[] { Quaternion.Identity, Quaternion.Identity };
        var result = new Matrix4x4[2];

        PoseBlender.BlendLocal(sourceT, sourceR, destinationT, destinationR, 0.5f, result);

        Assert.Equal(5f, result[0].Translation.X, precision: 5);
        Assert.Equal(0f, result[0].Translation.Y, precision: 5);
        Assert.Equal(2f, result[0].Translation.Z, precision: 5);
        Assert.Equal(10f, result[1].Translation.Y, precision: 5);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.9f)]
    public void BlendRotationStaysNormalized(float alpha)
    {
        var from = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        var to = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.9f);
        var mid = PoseBlender.SlerpShortest(from, to, alpha);
        Assert.Equal(1f, mid.Length(), precision: 5);
        Assert.False(float.IsNaN(mid.X + mid.Y + mid.Z + mid.W));
    }

    [Fact]
    public void BlendRotationHandlesDegenerateInputs()
    {
        var zero = default(Quaternion);
        var mid = PoseBlender.SlerpShortest(zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f), 0.5f);
        Assert.Equal(1f, mid.Length(), precision: 5);

        var bothZero = PoseBlender.SlerpShortest(default, default, 0.5f);
        Assert.Equal(1f, bothZero.Length(), precision: 5);
    }

    [Fact]
    public void BlendTakesShortestRotationalPath()
    {
        var from = Quaternion.Identity;
        var to = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 3.0f);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.5f);

        var mid = PoseBlender.SlerpShortest(from, to, 0.5f);
        Assert.True(MathF.Abs(Quaternion.Dot(mid, expected)) > 0.9999f);

        // Negating the destination is the same rotation; the path must not flip.
        var midNegated = PoseBlender.SlerpShortest(from, Quaternion.Negate(to), 0.5f);
        Assert.True(MathF.Abs(Quaternion.Dot(midNegated, expected)) > 0.9999f);
    }

    [Fact]
    public void BlendAllOutputsRemainFinite()
    {
        var sourceT = new Vector3[] { new(0, 0, 0), new(1e20f, 0, 0) };
        var sourceR = new Quaternion[] { default, Quaternion.Identity };
        var destinationT = new Vector3[] { new(1e20f, 1e20f, 1e20f), new(0, 0, 0) };
        var destinationR = new Quaternion[] { Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), default };
        var result = new Matrix4x4[2];

        PoseBlender.BlendLocal(sourceT, sourceR, destinationT, destinationR, 0.5f, result);
        AssertFinite(result[0]);
        AssertFinite(result[1]);
    }

    [Fact]
    public void BlendRejectsMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() => PoseBlender.BlendLocal(
            new Vector3[2], new Quaternion[2], new Vector3[2], new Quaternion[2], 0.5f, new Matrix4x4[3]));
    }

    // --- transition lifecycle -------------------------------------------

    [Fact]
    public void IdleToWalkTransitionCompletes()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.NotNull(session.Transition);

        session.Update(new FrameTime(1.0 + GameSession.LocomotionBlendDurationSeconds, 0.2), Move(forward: true));
        Assert.Null(session.Transition);
    }

    [Fact]
    public void WalkToIdleTransitionCompletes()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(1.1, 0.1), Move(forward: true));
        session.Update(new FrameTime(1.1, 0.0), NoInput());
        Assert.NotNull(session.Transition);

        session.Update(new FrameTime(1.4, 0.3), NoInput());
        Assert.Null(session.Transition);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void WalkToRunTransitionCompletes()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(1.1, 0.1), Move(forward: true, sprint: true));
        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);

        session.Update(new FrameTime(1.4, 0.3), Move(forward: true, sprint: true));
        Assert.Null(session.Transition);
    }

    [Fact]
    public void RunToWalkTransitionCompletes()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true, sprint: true));
        session.Update(new FrameTime(1.1, 0.1), Move(forward: true));
        Assert.NotNull(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);

        session.Update(new FrameTime(1.4, 0.3), Move(forward: true));
        Assert.Null(session.Transition);
    }

    [Fact]
    public void TransitionTimingIsElapsedTimeBased()
    {
        var single = SessionWithClips();
        single.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        single.Update(new FrameTime(1.1, 0.1), Move(forward: true));

        var split = SessionWithClips();
        split.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        split.Update(new FrameTime(1.05, 0.05), Move(forward: true));
        split.Update(new FrameTime(1.1, 0.05), Move(forward: true));

        Assert.Equal(single.ActiveClipName, split.ActiveClipName);
        Assert.Equal(single.ErikaYawRadians, split.ErikaYawRadians, precision: 5);
        Assert.Equal(single.Transition!.Value.ProgressAt(1.1), split.Transition!.Value.ProgressAt(1.1), precision: 5);

        // Travel follows the per-frame smoothed yaw, so splitting the same total
        // time into smaller steps integrates the curve a little more finely.
        // The result must be effectively equivalent, not bit-identical.
        Assert.True(MathF.Abs(single.ErikaPosition.X - split.ErikaPosition.X) < 0.05f);
        Assert.True(MathF.Abs(single.ErikaPosition.Z - split.ErikaPosition.Z) < 0.05f);
    }

    // --- interrupted transitions ----------------------------------------

    [Fact]
    public void ReleasingMidBlendReversesContinuously()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(1.05, 0.05), Move(forward: true));
        var alpha = session.Transition!.Value.ProgressAt(1.05);
        Assert.True(alpha > 0f && alpha < 1f);

        session.Update(new FrameTime(1.05, 0.0), NoInput());

        var reversed = session.Transition;
        Assert.NotNull(reversed);
        Assert.Equal(ErikaFigure.WalkClipName, reversed.Value.SourceClipName);
        Assert.Equal(ErikaFigure.IdleClipName, reversed.Value.DestinationClipName);
        Assert.Equal(1f - alpha, reversed.Value.ProgressAt(1.05), precision: 4);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
    }

    [Fact]
    public void WalkRunWalkRapidlyRemainsBounded()
    {
        var session = SessionWithClips();
        var time = 2.0;
        for (var i = 0; i < 60; i++)
        {
            session.Update(new FrameTime(time, 0.01), Move(forward: true, sprint: i % 2 == 0));
            time += 0.01;
            Assert.True(float.IsFinite(session.ErikaPosition.X + session.ErikaPosition.Z));
            Assert.True(float.IsFinite(session.ErikaYawRadians));
        }

        // Holding a single intent must settle to exactly one clip, no transition.
        time += 0.5;
        session.Update(new FrameTime(time, 0.5), Move(forward: true));
        Assert.Null(session.Transition);
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
    }

    [Fact]
    public void RepeatedInterruptionNeverAccumulatesState()
    {
        var session = SessionWithClips();
        var time = 3.0;
        for (var i = 0; i < 40; i++)
        {
            session.Update(new FrameTime(time, 0.005), Move(forward: true));
            time += 0.005;
            session.Update(new FrameTime(time, 0.005), NoInput());
            time += 0.005;
        }

        Assert.True(float.IsFinite(session.ErikaPosition.X + session.ErikaPosition.Z));
        time += 0.5;
        session.Update(new FrameTime(time, 0.5), NoInput());
        Assert.Null(session.Transition);
    }

    [Fact]
    public void NoStaleSourceAfterCompletion()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(1.3, 0.3), Move(forward: true));
        Assert.Null(session.Transition);

        session.Update(new FrameTime(1.3, 0.0), NoInput());
        var fresh = session.Transition;
        Assert.NotNull(fresh);
        Assert.Equal(ErikaFigure.WalkClipName, fresh.Value.SourceClipName);
        Assert.Equal(ErikaFigure.IdleClipName, fresh.Value.DestinationClipName);
    }

    // --- root motion during transitions ---------------------------------

    [Fact]
    public void IdleToWalkMatchesAuthoritativeWalkDisplacement()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(0.0, 0.016), Move(forward: true));
        var origin = session.ErikaPosition;

        // One second of walk authority, transition long since complete.
        session.Update(new FrameTime(1.0, 1.0), Move(forward: true));
        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(100f * ErikaFigure.Scale, traveled, precision: 3);
    }

    [Fact]
    public void SourcePoseDoesNotAddSecondRootDeltaDuringBlend()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(0.0, 0.016), Move(forward: true));
        session.Update(new FrameTime(0.5, 0.5), Move(forward: true));

        // Switch to run; the switch frame itself applies no delta.
        session.Update(new FrameTime(0.516, 0.016), Move(forward: true, sprint: true));
        var origin = session.ErikaPosition;

        // Still inside the 0.2 s blend: only the run authority may move her.
        session.Update(new FrameTime(0.6, 0.084), Move(forward: true, sprint: true));
        Assert.NotNull(session.Transition);
        var traveled = (session.ErikaPosition - origin).Length();

        // Run net 100 native per 0.5 s loop => 0.084 s of run travel only.
        var expected = (0.084f / 0.5f) * 100f * ErikaFigure.Scale;
        Assert.Equal(expected, traveled, precision: 3);
    }

    [Fact]
    public void TransitionBeginAndEndDoNotTeleport()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(5.0, 0.016), NoInput());
        var beforeStart = session.ErikaPosition;
        session.Update(new FrameTime(5.016, 0.016), Move(forward: true));
        Assert.Equal(beforeStart, session.ErikaPosition);

        var maxStep = 0f;
        var previous = session.ErikaPosition;
        var time = 5.016;
        for (var i = 0; i < 60; i++)
        {
            time += 0.02;
            session.Update(new FrameTime(time, 0.02), Move(forward: true));
            maxStep = MathF.Max(maxStep, (session.ErikaPosition - previous).Length());
            previous = session.ErikaPosition;
        }

        // A teleport (or doubled root motion) would spike well past one frame of travel.
        Assert.True(maxStep < 1.0f);
    }

    [Fact]
    public void LoopSeamRemainsCorrectAfterTransition()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(10.0, 0.016), Move(forward: true));
        var origin = session.ErikaPosition;

        // Three complete walk loops after the blend settles.
        for (var i = 1; i <= 12; i++)
        {
            session.Update(new FrameTime(10.0 + i * 0.25, 0.25), Move(forward: true));
        }

        var traveled = (session.ErikaPosition - origin).Length();
        Assert.Equal(300f * ErikaFigure.Scale, traveled, precision: 2);
    }

    // --- yaw smoothing ---------------------------------------------------

    [Fact]
    public void YawStepsTowardTargetWithoutOvershoot()
    {
        Assert.Equal(0.1f, YawSmoothing.StepTowards(0f, 0.1f, 1f), precision: 5);
        Assert.Equal(1.0f, YawSmoothing.StepTowards(0f, 1.0f, 10f), precision: 5);
        Assert.Equal(0.2f, YawSmoothing.StepTowards(0f, 1.0f, 0.2f), precision: 5);
    }

    [Fact]
    public void YawTurnsShortestDirectionAcrossWrap()
    {
        // Target +6.0 rad wraps to a small negative angle; shortest is negative.
        var step = YawSmoothing.StepTowards(0f, 6.0f, 0.1f);
        Assert.Equal(-0.1f, step, precision: 5);

        // Straddling the +/-pi boundary must not jump.
        var near = YawSmoothing.StepTowards(MathF.PI - 0.05f, -MathF.PI + 0.05f, 0.1f);
        Assert.True(MathF.Abs(YawSmoothing.WrapToPi(near - (MathF.PI - 0.05f))) <= 0.1001f);
    }

    [Fact]
    public void YawWrapToPiNormalizes()
    {
        Assert.Equal(0.5f, YawSmoothing.WrapToPi(0.5f), precision: 5);
        Assert.Equal(0.5f, YawSmoothing.WrapToPi(0.5f + YawSmoothing.TwoPi), precision: 5);
        Assert.Equal(-0.5f, YawSmoothing.WrapToPi(-0.5f - YawSmoothing.TwoPi), precision: 5);
        Assert.Equal(0f, YawSmoothing.WrapToPi(float.NaN));
    }

    [Fact]
    public void StationaryErikaRetainsHeading()
    {
        var session = SessionWithClips();
        session.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        var heading = session.ErikaYawRadians;

        session.Update(new FrameTime(2.0, 1.0), NoInput());
        session.Update(new FrameTime(3.0, 1.0), NoInput());
        Assert.Equal(heading, session.ErikaYawRadians, precision: 6);
    }

    [Fact]
    public void EquivalentElapsedTimeGivesEquivalentYaw()
    {
        var single = SessionWithClips();
        single.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        single.Update(new FrameTime(1.1, 0.1), Move(forward: true));

        var split = SessionWithClips();
        split.Update(new FrameTime(1.0, 0.016), Move(forward: true));
        split.Update(new FrameTime(1.05, 0.05), Move(forward: true));
        split.Update(new FrameTime(1.1, 0.05), Move(forward: true));

        Assert.Equal(single.ErikaYawRadians, split.ErikaYawRadians, precision: 5);
    }

    [Fact]
    public void HalfTurnIsDeterministic()
    {
        var first = SessionWithClips();
        var second = SessionWithClips();
        var time = 0.0;
        for (var i = 0; i < 20; i++)
        {
            time += 0.05;
            first.Update(new FrameTime(time, 0.05), Move(back: true));
            second.Update(new FrameTime(time, 0.05), Move(back: true));
        }

        Assert.Equal(first.ErikaYawRadians, second.ErikaYawRadians, precision: 6);
        Assert.True(float.IsFinite(first.ErikaYawRadians));
    }

    [Fact]
    public void RootTravelFollowsSmoothedYawNotTargetSnap()
    {
        var session = SessionWithClips();
        // Phase 2H spawns Erika facing -Z, aligned with W. Pressing S requests +Z,
        // a 180 degree turn, so travel must curve through the smoothed yaw rather
        // than snap onto the target heading.
        session.Update(new FrameTime(0.0, 0.016), Move(back: true));
        var origin = session.ErikaPosition;

        session.Update(new FrameTime(0.05, 0.05), Move(back: true));
        var step = session.ErikaPosition - origin;
        var yaw = session.ErikaYawRadians;

        // Still mid-turn: the target is +Z, but travel follows the visible yaw.
        var remaining = MathF.Abs(YawSmoothing.WrapToPi(0f - yaw));
        Assert.True(remaining > 0f && remaining < MathF.PI);
        var direction = Vector3.Normalize(new Vector3(step.X, 0f, step.Z));
        Assert.Equal(MathF.Sin(yaw), direction.X, precision: 4);
        Assert.Equal(MathF.Cos(yaw), direction.Z, precision: 4);
        // Not yet snapped to the +Z target direction.
        Assert.True(direction.Z < 0.99f);
    }
}
