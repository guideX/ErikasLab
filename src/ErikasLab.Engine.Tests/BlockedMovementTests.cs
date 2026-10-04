using System.Numerics;
using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2P blocked-movement locomotion response. Covers the diagnostic probe
/// and progress-ratio metric, the enter/release hysteresis and elapsed-time
/// confirmation, sustained blocking against every solid obstacle class,
/// recovery when the route becomes viable, meaningful wall sliding, interior
/// navigation regressions, 30/60/144 Hz behavior, and the clear-space A/B
/// no-op gate. The collision resolver, speed envelope, root-motion authority,
/// and playback synchronization are reused unchanged; these tests verify the
/// blocked response feeds back only into the locomotion target-speed decision.
/// </summary>
public sealed class BlockedMovementTests
{
    private const float Radius = PlayerCollisionPolicy.PlayerCollisionRadiusMeters;
    private const float Skin = PlayerCollisionPolicy.PlayerCollisionSkinMeters;
    private const float Probe = BlockedMovementPolicy.BlockedMovementProbeDistanceMeters;
    private const float Enter = BlockedMovementPolicy.BlockedEnterProgressRatio;
    private const float Release = BlockedMovementPolicy.BlockedReleaseProgressRatio;
    private const float EnterDelay = BlockedMovementPolicy.BlockedEnterDelaySeconds;
    private const float ReleaseDelay = BlockedMovementPolicy.BlockedReleaseDelaySeconds;

    // --- detector helpers -------------------------------------------------

    private static PlayerCollisionSet SingleWall() => new([
        new PlayerCollisionBox("Wall", new Vector2(0f, 0f), new Vector2(0.125f, 5f), 0f),
    ]);

    private static BlockedMovementTracker Tracker(PlayerCollisionSet set) => new(set, Radius);

    private static Vector2 WallRestX(float gapFromExpandedWall) => new(0.125f + Radius + gapFromExpandedWall, 0f);

    private static void ProbeFrames(BlockedMovementTracker tracker, Vector2 position, Vector2 direction, int frames, double dt = 1.0 / 60.0)
    {
        for (var i = 0; i < frames; i++)
        {
            tracker.Update(position, direction, true, dt);
        }
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // --- detector: clear probe --------------------------------------------

    [Fact]
    public void ClearProbeIsUnblocked()
    {
        var tracker = Tracker(SingleWall());
        var position = new Vector2(5f, 0f);

        ProbeFrames(tracker, position, new Vector2(0f, -1f), 30);

        Assert.False(tracker.IsMovementBlocked);
        Assert.Equal(1f, tracker.BlockedMovementProgressRatio, precision: 5);
        Assert.Equal(0f, tracker.BlockedMovementSeconds, precision: 5);
    }

    // --- detector: head-on probes -----------------------------------------

    [Fact]
    public void HeadOnWallProbeHasLowProgress()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 1);

        Assert.True(tracker.BlockedMovementProgressRatio < Enter,
            $"ratio {tracker.BlockedMovementProgressRatio:F3} should be below enter {Enter}");
    }

    [Fact]
    public void HeadOnHearthProbeHasLowProgress()
    {
        var set = EnvironmentFactory.CreatePlayerCollisionSet();
        var tracker = Tracker(set);
        var hearthFrontZ = LonghouseLayout.HearthCenterZ + LonghouseLayout.HearthLength / 2f;
        var position = new Vector2(0f, hearthFrontZ + Radius + Skin);

        ProbeFrames(tracker, position, new Vector2(0f, -1f), 1);

        Assert.True(tracker.BlockedMovementProgressRatio < Enter,
            $"ratio {tracker.BlockedMovementProgressRatio:F3} should be below enter {Enter}");
    }

    private static (Vector2 Position, Vector2 Direction) HeadOnProbe(PlayerCollisionBox box)
    {
        var local = new Vector2(0f, box.HalfExtentsXZ.Y + Radius + Skin);
        var position = box.CenterXZ + box.LocalToWorld(local);
        var direction = Vector2.Normalize(box.LocalToWorld(new Vector2(0f, -1f)));
        return (position, direction);
    }

    [Fact]
    public void HeadOnTreeProbeHasLowProgress()
    {
        var set = EnvironmentFactory.CreatePlayerCollisionSet();
        var tracker = Tracker(set);
        var box = set.Boxes.Single(b => b.Name == "Tree.06.Trunk");
        var (position, direction) = HeadOnProbe(box);

        ProbeFrames(tracker, position, direction, 1);

        Assert.True(tracker.BlockedMovementProgressRatio < Enter,
            $"ratio {tracker.BlockedMovementProgressRatio:F3} should be below enter {Enter}");
    }

    // --- detector: meaningful slide vs near-head-on -----------------------

    [Fact]
    public void MeaningfulWallSlideIsNotBlocked()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);
        var direction = Vector2.Normalize(new Vector2(-1f, -1f));

        ProbeFrames(tracker, position, direction, 30);

        Assert.False(tracker.IsMovementBlocked);
        Assert.True(tracker.BlockedMovementProgressRatio > Release,
            $"ratio {tracker.BlockedMovementProgressRatio:F3} should exceed release {Release}");
    }

    [Fact]
    public void NearHeadOnLowProgressSlideIsBlocked()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);
        var direction = Vector2.Normalize(new Vector2(-1f, -0.087f));

        ProbeFrames(tracker, position, direction, 30);

        Assert.True(tracker.IsMovementBlocked);
        Assert.True(tracker.BlockedMovementProgressRatio < Enter,
            $"ratio {tracker.BlockedMovementProgressRatio:F3} should be below enter {Enter}");
    }

    // --- detector: thresholds ---------------------------------------------

    [Fact]
    public void EnterThresholdBracketsAtFifteenPercent()
    {
        var below = Tracker(SingleWall());
        ProbeFrames(below, WallRestX(0.020f), new Vector2(-1f, 0f), 30);
        Assert.True(below.IsMovementBlocked, "ratio 0.10 should latch");
        Assert.InRange(below.BlockedMovementProgressRatio, 0.08f, 0.12f);

        var above = Tracker(SingleWall());
        ProbeFrames(above, WallRestX(0.030f), new Vector2(-1f, 0f), 30);
        Assert.False(above.IsMovementBlocked, "ratio 0.20 is in the hysteresis band");
        Assert.InRange(above.BlockedMovementProgressRatio, 0.18f, 0.22f);
    }

    [Fact]
    public void ReleaseThresholdBracketsAtThirtyPercent()
    {
        var tracker = Tracker(SingleWall());
        ProbeFrames(tracker, WallRestX(Skin), new Vector2(-1f, 0f), 30);
        Assert.True(tracker.IsMovementBlocked);

        ProbeFrames(tracker, WallRestX(0.030f), new Vector2(-1f, 0f), 30);
        Assert.True(tracker.IsMovementBlocked, "ratio 0.20 is in the hysteresis band, holds blocked");

        ProbeFrames(tracker, WallRestX(0.050f), new Vector2(-1f, 0f), 30);
        Assert.False(tracker.IsMovementBlocked, "ratio 0.40 should release");
    }

    [Fact]
    public void ProgressRatioAtEnterThresholdDistanceIsFifteenPercent()
    {
        var tracker = Tracker(SingleWall());
        ProbeFrames(tracker, WallRestX(0.025f), new Vector2(-1f, 0f), 1);
        Assert.InRange(tracker.BlockedMovementProgressRatio, Enter - 0.02f, Enter + 0.02f);
    }

    // --- detector: enter delay --------------------------------------------

    [Fact]
    public void EnterDelayRequiresSustainedLowProgress()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 1);
        Assert.False(tracker.IsMovementBlocked, "one frame must not latch");
        Assert.True(tracker.BlockedMovementCandidateSeconds > 0f);

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 1);
        Assert.False(tracker.IsMovementBlocked, "below the delay must not latch");

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 3);
        Assert.True(tracker.IsMovementBlocked, "reaching the delay latches");
    }

    [Fact]
    public void OneFrameObstructionDoesNotLatch()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 1);
        ProbeFrames(tracker, new Vector2(5f, 0f), new Vector2(0f, -1f), 30);

        Assert.False(tracker.IsMovementBlocked);
        Assert.Equal(0f, tracker.BlockedMovementCandidateSeconds, precision: 5);
    }

    // --- detector: safety --------------------------------------------------

    [Fact]
    public void NonFiniteValuesAreSafe()
    {
        var tracker = Tracker(SingleWall());

        tracker.Update(new Vector2(float.NaN, 0f), new Vector2(-1f, 0f), true, 1.0 / 60.0);
        Assert.False(tracker.IsMovementBlocked);
        Assert.Equal(1f, tracker.BlockedMovementProgressRatio, precision: 5);

        tracker.Update(new Vector2(5f, 0f), new Vector2(float.NaN, 0f), true, 1.0 / 60.0);
        Assert.False(tracker.IsMovementBlocked);

        tracker.Update(new Vector2(float.PositiveInfinity, 0f), new Vector2(-1f, 0f), true, 1.0 / 60.0);
        Assert.False(tracker.IsMovementBlocked);
        Assert.True(float.IsFinite(tracker.BlockedMovementProgressRatio));
    }

    [Fact]
    public void ZeroProbeDirectionIsSafe()
    {
        var tracker = Tracker(SingleWall());

        tracker.Update(new Vector2(5f, 0f), Vector2.Zero, true, 1.0 / 60.0);

        Assert.False(tracker.IsMovementBlocked);
        Assert.Equal(1f, tracker.BlockedMovementProgressRatio, precision: 5);
    }

    [Fact]
    public void InputAbsenceClearsState()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);

        ProbeFrames(tracker, position, new Vector2(-1f, 0f), 30);
        Assert.True(tracker.IsMovementBlocked);

        tracker.Update(position, new Vector2(-1f, 0f), false, 1.0 / 60.0);

        Assert.False(tracker.IsMovementBlocked);
        Assert.Equal(0f, tracker.BlockedMovementSeconds, precision: 5);
        Assert.Equal(0f, tracker.BlockedMovementCandidateSeconds, precision: 5);
        Assert.Equal(1f, tracker.BlockedMovementProgressRatio, precision: 5);
    }

    [Fact]
    public void NonFiniteDeltaSecondsDoesNotAdvance()
    {
        var tracker = Tracker(SingleWall());
        var position = WallRestX(Skin);

        tracker.Update(position, new Vector2(-1f, 0f), true, double.NaN);
        Assert.Equal(0f, tracker.BlockedMovementCandidateSeconds, precision: 5);
        Assert.False(tracker.IsMovementBlocked);
    }

    // --- gameplay helpers -------------------------------------------------

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
                [Vector3.Zero, new Vector3(0, 0, 200)],
                [Quaternion.Identity, Quaternion.Identity]),
        ]);

    private static AnimationClip Run() => new(
        "run", 0.5f, 30f, [
            new AnimationChannel(0, [0f, 0.5f],
                [Vector3.Zero, new Vector3(0, 0, 300)],
                [Quaternion.Identity, Quaternion.Identity]),
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
        bool sprint = false) => new(
        MoveForward: forward, MoveBackward: back, StrafeLeft: left, StrafeRight: right,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        Sprint: sprint);

    private static InputState NoInput() => Move();

    private sealed class Timeline
    {
        public double Time { get; private set; }

        public void Step(GameSession session, int frames, Func<int, InputState> input, double dt = 1.0 / 60.0)
        {
            for (var i = 1; i <= frames; i++)
            {
                Time += dt;
                session.Update(new FrameTime(Time, dt), input(i));
            }
        }
    }

    private static bool IsPenetrating(PlayerCollisionSet set, Vector2 position) =>
        set.ContainsPoint(position, Radius - 1e-3f);

    private const float HearthFrontZ = -1.5f + 2.0f;

    private static double FaceTree(GameSession session, int treeIndex, bool settle)
    {
        var tree = LonghouseLayout.TreePosition(treeIndex);
        var yaw = LonghouseLayout.TreeYawRadians(treeIndex);
        var localY = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var frontPoint = new Vector2(tree.X, tree.Z) + localY * 5f;
        var spawnXZ = new Vector2(LonghouseLayout.SpawnPosition.X, LonghouseLayout.SpawnPosition.Z);

        session.CameraRig.SetOrbit(
            MathF.Atan2(frontPoint.X - spawnXZ.X, -(frontPoint.Y - spawnXZ.Y)),
            session.CameraRig.OrbitPitchRadians);
        var t = 0.0;
        for (var i = 0; i < 900; i++)
        {
            t += 1.0 / 60.0;
            session.Update(new FrameTime(t, 1.0 / 60.0), Move(forward: true));
            if (Vector2.Distance(new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z), frontPoint) < 0.05f)
            {
                break;
            }
        }

        session.CameraRig.SetOrbit(
            MathF.Atan2(tree.X - session.ErikaPosition.X, -(tree.Z - session.ErikaPosition.Z)),
            session.CameraRig.OrbitPitchRadians);

        if (!settle)
        {
            return t;
        }

        for (var i = 0; i < 60; i++)
        {
            t += 1.0 / 60.0;
            session.Update(new FrameTime(t, 1.0 / 60.0), NoInput());
        }

        return t;
    }

    private sealed record BlockedMeasurements(
        int LatchFrame,
        int ZeroSpeedFrame,
        int IdleFrame,
        Vector3 PositionAtLatch,
        float PostLatchTravel,
        float SpeedAtLatch);

    private static string? MeasureBlockedUntilHit(GameSession session, int maxFrames, Func<int, InputState> input, double startTime = 0.0)
    {
        var dt = 1.0 / 60.0;
        string? hitName = null;
        for (var i = 1; i <= maxFrames; i++)
        {
            var time = new FrameTime(startTime + i * dt, dt);
            session.Update(time, input(i));
            if (hitName is null && session.WasPlayerCollisionConstrained)
            {
                hitName = session.LastPlayerCollisionName;
            }
        }

        return hitName;
    }

    private static BlockedMeasurements MeasureBlocked(GameSession session, int maxFrames, Func<int, InputState> input, double startTime = 0.0)
    {
        var latchFrame = -1;
        var zeroSpeedFrame = -1;
        var idleFrame = -1;
        var positionAtLatch = Vector3.Zero;
        var speedAtLatch = 0f;
        var zero = MovementSpeedEnvelope.ZeroSpeedThresholdMetersPerSecond;
        var dt = 1.0 / 60.0;

        for (var i = 1; i <= maxFrames; i++)
        {
            var time = new FrameTime(startTime + i * dt, dt);
            session.Update(time, input(i));

            if (latchFrame < 0 && session.IsMovementBlocked)
            {
                latchFrame = i;
                positionAtLatch = session.ErikaPosition;
                speedAtLatch = session.CurrentMoveSpeedMetersPerSecond;
            }

            if (zeroSpeedFrame < 0 && session.CurrentMoveSpeedMetersPerSecond <= zero)
            {
                zeroSpeedFrame = i;
            }

            if (idleFrame < 0 && session.ActiveClipName == ErikaFigure.IdleClipName)
            {
                idleFrame = i;
            }
        }

        var postLatchTravel = latchFrame >= 0
            ? Vector3.Distance(positionAtLatch, session.ErikaPosition)
            : 0f;

        return new BlockedMeasurements(
            latchFrame, zeroSpeedFrame, idleFrame, positionAtLatch, postLatchTravel, speedAtLatch);
    }

    // --- sustained blocking: walk -----------------------------------------

    [Fact]
    public void WalkIntoHearthLatchesAndIdles()
    {
        var session = SessionWithClips();
        var m = MeasureBlocked(session, 900, _ => Move(forward: true));

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.True(m.ZeroSpeedFrame >= 0, "speed never reached zero");
        Assert.True(m.IdleFrame >= 0, "never reached idle");
        Assert.True(m.LatchFrame <= m.IdleFrame, "idle should follow the latch");
        Assert.True(m.LatchFrame < m.ZeroSpeedFrame, "zero speed should follow the latch");
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void WalkIntoTreeCollidesAndStops()
    {
        var session = SessionWithClips();
        var start = FaceTree(session, 6, settle: false);
        var hitName = MeasureBlockedUntilHit(session, 900, _ => Move(forward: true), startTime: start);

        Assert.Equal("Tree.06.Trunk", hitName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void WalkIntoTableLatchesAndIdles()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 330, _ => Move(forward: true));
        var m = MeasureBlocked(session, 300, _ => Move(right: true));

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void WalkIntoBenchLatchesAndIdles()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        timeline.Step(session, 250, _ => Move(forward: true, left: true));
        var m = MeasureBlocked(session, 300, _ => Move(left: true), startTime: 1150.0 / 60.0);

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void WalkIntoRearWallLatchesAndIdles()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 200, _ => Move(forward: true));
        timeline.Step(session, 600, _ => Move(forward: true, left: true));
        var m = MeasureBlocked(session, 600, _ => Move(forward: true), startTime: 800.0 / 60.0);

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    // --- sustained blocking: run ------------------------------------------

    [Fact]
    public void RunIntoHearthLatchesAndIdles()
    {
        var session = SessionWithClips();
        var m = MeasureBlocked(session, 600, _ => Move(forward: true, sprint: true));

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.True(m.ZeroSpeedFrame >= 0, "speed never reached zero");
        Assert.True(m.IdleFrame >= 0, "never reached idle");
        Assert.True(m.LatchFrame <= m.IdleFrame, "idle should follow the latch");
        Assert.True(m.LatchFrame < m.ZeroSpeedFrame, "zero speed should follow the latch");
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void RunIntoTreeCollidesAndStops()
    {
        var session = SessionWithClips();
        var start = FaceTree(session, 6, settle: true);
        var timeline = new Timeline();
        timeline.Step(session, 90, _ => Move(forward: true));
        var hitName = MeasureBlockedUntilHit(session, 600, _ => Move(forward: true, sprint: true), startTime: start + timeline.Time);

        Assert.Equal("Tree.06.Trunk", hitName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void RunIntoRearWallLatchesAndIdles()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 200, _ => Move(forward: true, sprint: true));
        timeline.Step(session, 600, _ => Move(forward: true, left: true, sprint: true));
        var m = MeasureBlocked(session, 600, _ => Move(forward: true, sprint: true), startTime: 800.0 / 60.0);

        Assert.True(m.LatchFrame >= 0, "blocked latch never fired");
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    // --- sustained blocking: speed envelope -------------------------------

    [Fact]
    public void BlockedSpeedDecreasesMonotonicallyThroughExistingEnvelope()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));

        var previous = session.CurrentMoveSpeedMetersPerSecond;
        for (var i = 0; i < 120; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, sprint: true));
            var current = session.CurrentMoveSpeedMetersPerSecond;
            Assert.True(current <= previous + 1e-5f, $"speed increased while blocked: {previous:F4} -> {current:F4}");
            previous = current;
        }

        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond, precision: 5);
    }

    [Fact]
    public void BlockedPlaybackRateDecreasesToIdle()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));

        var previous = session.VisualPlaybackRate;
        for (var i = 0; i < 120; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, sprint: true));
            var current = session.VisualPlaybackRate;
            Assert.True(current <= previous + 1e-4f, $"playback rate increased while blocked: {previous:F4} -> {current:F4}");
            previous = current;
        }
    }

    [Fact]
    public void HeldInputDoesNotRestartLocomotionWhileBlocked()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));

        var position = session.ErikaPosition;
        for (var i = 0; i < 300; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true));
            Assert.Equal(position.X, session.ErikaPosition.X, precision: 4);
            Assert.Equal(position.Z, session.ErikaPosition.Z, precision: 4);
            Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
            Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond, precision: 5);
        }
    }

    // --- recovery ----------------------------------------------------------

    [Fact]
    public void RedirectAlongWallReleasesAndAccelerates()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked);

        timeline.Step(session, 30, _ => Move(left: true));

        Assert.False(session.IsMovementBlocked);
        Assert.True(session.CurrentMoveSpeedMetersPerSecond > session.WalkAuthoredSpeedMetersPerSecond * 0.5f,
            $"did not accelerate after redirect: speed={session.CurrentMoveSpeedMetersPerSecond:F3}");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void RedirectAwayFromWallReleasesAndAccelerates()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked);
        var zAtBlock = session.ErikaPosition.Z;

        timeline.Step(session, 300, _ => Move(back: true));

        Assert.False(session.IsMovementBlocked);
        Assert.True(session.ErikaPosition.Z > zAtBlock + 0.5f, "did not move away from the hearth");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void CameraOrbitWhileBlockedReleasesAndAccelerates()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked);

        // Rotate the camera ~180 degrees while holding W, so the requested
        // travel direction swings around to face away from the hearth. Track
        // that the original hearth block releases and Erika accelerates.
        var lookInput = new InputState(
            MoveForward: true, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
            LookLeft: false, LookRight: true, LookUp: false, LookDown: false,
            ExitRequested: false, MouseDelta: default, Sprint: false);
        var released = false;
        var maxSpeed = 0f;
        for (var i = 0; i < 120; i++)
        {
            timeline.Step(session, 1, _ => lookInput);
            if (!session.IsMovementBlocked && session.CurrentMoveSpeedMetersPerSecond > 0.1f)
            {
                released = true;
            }
            maxSpeed = MathF.Max(maxSpeed, session.CurrentMoveSpeedMetersPerSecond);
        }

        Assert.True(released, "camera orbit should release the hearth block");
        Assert.True(maxSpeed > 0.5f, $"should accelerate after release: maxSpeed={maxSpeed:F3}");
    }

    [Fact]
    public void WalkToRunWhileBlockedUsesRunTargetOnRelease()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked);
        Assert.Equal(0f, session.TargetMoveSpeedMetersPerSecond, precision: 5);

        timeline.Step(session, 30, _ => Move(left: true, sprint: true));

        Assert.False(session.IsMovementBlocked);
        Assert.Equal(session.RunAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
    }

    [Fact]
    public void RunToWalkWhileBlockedUsesWalkTargetOnRelease()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 600, _ => Move(forward: true, sprint: true));
        Assert.True(session.IsMovementBlocked);

        timeline.Step(session, 30, _ => Move(left: true));

        Assert.False(session.IsMovementBlocked);
        Assert.Equal(session.WalkAuthoredSpeedMetersPerSecond, session.TargetMoveSpeedMetersPerSecond, precision: 4);
    }

    [Fact]
    public void RecoveryHasNoSpeedJump()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked);

        float previousSpeed = 0f;
        var maxStep = MovementSpeedEnvelope.DefaultAccelerationMetersPerSecondSquared * (1.0 / 60.0) + 1e-3f;
        for (var i = 0; i < 30; i++)
        {
            timeline.Step(session, 1, _ => Move(left: true));
            var speed = session.CurrentMoveSpeedMetersPerSecond;
            Assert.True(speed <= previousSpeed + maxStep,
                $"speed jumped: {previousSpeed:F4} -> {speed:F4}");
            previousSpeed = speed;
        }
    }

    // --- wall slide --------------------------------------------------------

    [Fact]
    public void TangentialSlideStaysInLocomotion()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 400, _ => Move(forward: true));
        Assert.True(session.IsMovementBlocked, "should be blocked at the hearth");

        // Redirect along the hearth front; the latch releases as the yaw turns
        // and the tangential slide continues without a permanent false block.
        var zBefore = session.ErikaPosition.Z;
        for (var i = 0; i < 400; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
        }

        Assert.True(session.ErikaPosition.Z < zBefore - 1f, "did not slide along the hearth");
        Assert.False(session.IsMovementBlocked, "should not be blocked after sliding");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Fact]
    public void NearlyHeadOnSlideEventuallyLatches()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 400, _ => Move(forward: true));

        var latched = false;
        for (var i = 0; i < 600; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            latched |= session.IsMovementBlocked;
        }

        Assert.True(latched, "a sustained near-head-on push should eventually latch");
    }

    [Fact]
    public void CornerContactDoesNotFlicker()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 400, _ => Move(forward: true));

        var latchCount = 0;
        var wasBlocked = false;
        for (var i = 0; i < 400; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            if (session.IsMovementBlocked && !wasBlocked)
            {
                latchCount++;
            }
            wasBlocked = session.IsMovementBlocked;
        }

        Assert.True(latchCount <= 1, $"latch flickered {latchCount} times");
    }

    // --- interior navigation ----------------------------------------------

    [Fact]
    public void DoorwayTraversalDoesNotFalseBlock()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();

        for (var i = 1; i <= 400; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true));
            if (session.ErikaPosition.Z < LonghouseLayout.FrontZ)
            {
                break;
            }
            Assert.False(session.IsMovementBlocked, $"false block in the doorway at frame {i}");
        }

        Assert.True(session.ErikaPosition.Z < LonghouseLayout.FrontZ, "did not enter the longhouse");
    }

    [Fact]
    public void LeftHearthBypassDoesNotFalseBlock()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 200, _ => Move(forward: true));

        for (var i = 0; i < 600; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            Assert.False(session.IsMovementBlocked, $"false block in the left bypass at frame {i}");
        }

        Assert.True(session.ErikaPosition.Z < -3.5f, $"did not get behind the hearth: z={session.ErikaPosition.Z:F3}");
    }

    [Fact]
    public void RightHearthBypassDoesNotFalseBlock()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 200, _ => Move(forward: true));

        for (var i = 0; i < 600; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, right: true));
            Assert.False(session.IsMovementBlocked, $"false block in the right bypass at frame {i}");
        }

        Assert.True(session.ErikaPosition.Z < -3.5f, $"did not get behind the hearth: z={session.ErikaPosition.Z:F3}");
    }

    [Fact]
    public void DiagonalWeaveDoesNotFalseBlock()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 330, _ => Move(forward: true));

        for (var i = 0; i < 300; i++)
        {
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            Assert.False(session.IsMovementBlocked, $"false block in the diagonal weave at frame {i}");
        }

        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    // --- frame-rate validation -------------------------------------------

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void WalkIntoHearthIsFrameRateIndependent(int fps)
    {
        var dt = 1.0 / fps;
        var session = SessionWithClips();
        var latchFrame = -1;
        var zeroSpeedFrame = -1;
        var idleFrame = -1;

        for (var i = 1; i <= fps * 15; i++)
        {
            session.Update(new FrameTime(i * dt, dt), Move(forward: true));
            if (latchFrame < 0 && session.IsMovementBlocked)
            {
                latchFrame = i;
            }
            if (zeroSpeedFrame < 0 && session.CurrentMoveSpeedMetersPerSecond <= MovementSpeedEnvelope.ZeroSpeedThresholdMetersPerSecond)
            {
                zeroSpeedFrame = i;
            }
            if (idleFrame < 0 && session.ActiveClipName == ErikaFigure.IdleClipName)
            {
                idleFrame = i;
            }
        }

        Assert.True(latchFrame >= 0, $"never latched at {fps} Hz");
        Assert.True(zeroSpeedFrame >= 0, $"never stopped at {fps} Hz");
        Assert.True(idleFrame >= 0, $"never idled at {fps} Hz");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RunIntoHearthIsFrameRateIndependent(int fps)
    {
        var dt = 1.0 / fps;
        var session = SessionWithClips();
        var latchFrame = -1;

        for (var i = 1; i <= fps * 10; i++)
        {
            session.Update(new FrameTime(i * dt, dt), Move(forward: true, sprint: true));
            if (latchFrame < 0 && session.IsMovementBlocked)
            {
                latchFrame = i;
            }
        }

        Assert.True(latchFrame >= 0, $"never latched at {fps} Hz");
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void ValidWallSlideNeverFalseBlocksAtAnyRate(int fps)
    {
        var dt = 1.0 / fps;
        var session = SessionWithClips();

        for (var i = 1; i <= fps * 8; i++)
        {
            session.Update(new FrameTime(i * dt, dt), Move(forward: true));
        }

        var zBefore = session.ErikaPosition.Z;
        for (var i = 0; i < fps * 6; i++)
        {
            session.Update(new FrameTime((fps * 8 + i) * dt, dt), Move(forward: true, left: true));
        }

        Assert.True(session.ErikaPosition.Z < zBefore - 1f, $"did not slide at {fps} Hz");
        Assert.False(IsPenetrating(session.PlayerCollisions, new Vector2(session.ErikaPosition.X, session.ErikaPosition.Z)));
    }

    // --- clear-space A/B ---------------------------------------------------

    private static void AssertClearSpaceIdentical(GameSession enabled, GameSession disabled)
    {
        Assert.Equal(disabled.ErikaPosition.X, enabled.ErikaPosition.X, precision: 5);
        Assert.Equal(disabled.ErikaPosition.Z, enabled.ErikaPosition.Z, precision: 5);
        Assert.Equal(disabled.ErikaYawRadians, enabled.ErikaYawRadians, precision: 6);
        Assert.Equal(disabled.CurrentMoveSpeedMetersPerSecond, enabled.CurrentMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(disabled.TargetMoveSpeedMetersPerSecond, enabled.TargetMoveSpeedMetersPerSecond, precision: 5);
        Assert.Equal(disabled.RootMotionGain, enabled.RootMotionGain, precision: 5);
        Assert.Equal(disabled.VisualPlaybackRate, enabled.VisualPlaybackRate, precision: 5);
        Assert.Equal(disabled.ActiveClipName, enabled.ActiveClipName);
        Assert.Equal(disabled.IsTurningInPlace, enabled.IsTurningInPlace);
        Assert.Equal(disabled.RequestedPlayerDisplacement, enabled.RequestedPlayerDisplacement);
        Assert.Equal(disabled.AcceptedPlayerDisplacement, enabled.AcceptedPlayerDisplacement);
        Assert.Equal(disabled.VisualPoseElapsedSeconds, enabled.VisualPoseElapsedSeconds, precision: 9);
        Assert.Equal(disabled.Transition is null, enabled.Transition is null);
        Assert.False(enabled.IsMovementBlocked);
    }

    private static (GameSession Enabled, GameSession Disabled) BlockedResponsePair()
    {
        var enabled = SessionWithClips();
        var disabled = SessionWithClips();
        disabled.BlockedMovementResponseEnabled = false;
        return (enabled, disabled);
    }

    private static void RunClearSpaceIdentical(GameSession enabled, GameSession disabled, int frames, Func<int, InputState> input)
    {
        for (var i = 1; i <= frames; i++)
        {
            var time = new FrameTime(i * 0.016, 0.016);
            var state = input(i);
            enabled.Update(time, state);
            disabled.Update(time, state);
            AssertClearSpaceIdentical(enabled, disabled);
        }
    }

    [Fact]
    public void ClearSpaceWalkIsIdenticalWithAndWithoutBlockedResponse()
    {
        var (enabled, disabled) = BlockedResponsePair();
        RunClearSpaceIdentical(enabled, disabled, 300, _ => Move(forward: true));
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void ClearSpaceRunIsIdenticalWithAndWithoutBlockedResponse()
    {
        var (enabled, disabled) = BlockedResponsePair();
        RunClearSpaceIdentical(enabled, disabled, 60, _ => Move(forward: true, sprint: true));
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    [Fact]
    public void ClearSpaceWalkRunStopIsIdenticalWithAndWithoutBlockedResponse()
    {
        var (enabled, disabled) = BlockedResponsePair();
        RunClearSpaceIdentical(enabled, disabled, 40, _ => Move(forward: true));
        RunClearSpaceIdentical(enabled, disabled, 40, _ => Move(forward: true, sprint: true));
        RunClearSpaceIdentical(enabled, disabled, 60, _ => NoInput());
        Assert.False(enabled.WasPlayerCollisionConstrained);
    }

    // --- world-motion invariant -------------------------------------------

    [Fact]
    public void PositionChangesOnlyByAcceptedDisplacement()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 300, _ => Move(forward: true));

        for (var i = 0; i < 300; i++)
        {
            var before = session.ErikaPosition;
            timeline.Step(session, 1, _ => Move(forward: true, left: true));
            var expected = before + session.AcceptedPlayerDisplacement;
            Assert.Equal(expected.X, session.ErikaPosition.X, precision: 5);
            Assert.Equal(expected.Z, session.ErikaPosition.Z, precision: 5);
        }
    }

    // --- turn-in-place interaction ----------------------------------------

    [Fact]
    public void TurnInPlaceDoesNotEnterBlocked()
    {
        var session = SessionWithClips();
        var timeline = new Timeline();
        timeline.Step(session, 900, _ => Move(forward: true));
        timeline.Step(session, 240, _ => NoInput());
        Assert.Equal(0f, session.CurrentMoveSpeedMetersPerSecond, precision: 6);

        var turned = false;
        for (var i = 0; i < 30; i++)
        {
            timeline.Step(session, 1, _ => Move(back: true));
            turned |= session.IsTurningInPlace;
            Assert.False(session.IsMovementBlocked, "turn-in-place must not classify as blocked");
        }

        Assert.True(turned, "test premise: a stationary reversal should gate translation");
    }
}
