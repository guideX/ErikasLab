using System.Numerics;
using ErikasLab.Engine;

namespace ErikasLab.Game;

public sealed class GameSession
{
    public const string Version = "0.1.0-phase1";

    private readonly ThirdPersonCamera _thirdPersonCamera = new();

    /// <summary>
    /// Phase 2F centralized locomotion crossfade duration. One short blend for
    /// every idle/walk/run transition; chosen at 0.20 s (inside the 0.15-0.25 s
    /// range) so pose changes read as continuous without delaying movement.
    /// Elapsed-time based, therefore frame-rate independent.
    /// </summary>
    public const float LocomotionBlendDurationSeconds = 0.20f;

    /// <summary>
    /// Phase 2F centralized yaw turn speed: 4π rad/s (~720°/s). A 180° reversal
    /// takes 0.25 s and a 90° turn 0.125 s, roughly matching the crossfade
    /// window. Constant angular speed, shortest path, no overshoot.
    /// </summary>
    public const float TurnSpeedRadiansPerSecond = MathF.PI * 4f;

    private AnimationClip? _walkClip;
    private AnimationClip? _runClip;
    private int _hipsBoneIndex = -1;
    private double _previousClipElapsed;
    private bool _diagnosticLatch;

    public GameSession()
    {
        World = TestWorldFactory.Create();
        Camera = new CameraState(new Vector3(0, 3.2f, 9.5f));
        ErikaPosition = ErikaFigure.GroundPosition;
        ErikaYawRadians = ErikaFigure.FacingYawRadians;

        // Phase 2G: establish a settled third-person frame at spawn (snap, not
        // a cross-world fly-in). No collision yet; the camera may pass through
        // scene geometry.
        _thirdPersonCamera.SnapToTarget(Camera, ErikaPosition);
    }

    public Scene World { get; }

    public CameraState Camera { get; }

    /// <summary>
    /// Phase 2G third-person follow/orbit rig. Owns orbit angles, follow
    /// distance/look-at height, and the smoothed camera position. It observes
    /// Erika's world state and never owns gameplay movement or facing.
    /// </summary>
    public ThirdPersonCamera CameraRig => _thirdPersonCamera;

    public bool ExitRequested { get; private set; }

    /// <summary>
    /// Phase 2E locomotion selection: idle/walk/run via movement intent
    /// (WASD + Shift) with 1/2/3 retained as a diagnostic latch. Defaults to
    /// idle. Movement takes precedence over diagnostics; releasing movement
    /// returns to idle unless the last selection was diagnostic (which
    /// latches, preserving Phase 2D behavior for headless/diagnostic runs).
    /// Phase 2F layers a single short crossfade on top; the active clip is
    /// always the destination (root-motion authority).
    /// </summary>
    public string ActiveClipName { get; private set; } = ErikaFigure.IdleClipName;

    /// <summary>Absolute game-clock time the active clip started (loop origin).</summary>
    public double ClipStartSeconds { get; private set; }

    /// <summary>
    /// Phase 2F in-flight crossfade, or null when a single clip renders. At most
    /// one transition exists; interruptions replace it (no graph, no history).
    /// The renderer evaluates both poses and blends them, but world root motion
    /// is always driven by <see cref="ActiveClipName"/> alone.
    /// </summary>
    public AnimationTransition? Transition { get; private set; }

    /// <summary>World-space (meters) Erika origin. Y stays at ground (0).</summary>
    public Vector3 ErikaPosition { get; private set; } = ErikaFigure.GroundPosition;

    /// <summary>
    /// Yaw (radians, Y-up) Erika faces. Phase 2F smoothly turns toward movement
    /// heading at <see cref="TurnSpeedRadiansPerSecond"/>; stationary retains it.
    /// </summary>
    public float ErikaYawRadians { get; private set; } = ErikaFigure.FacingYawRadians;

    /// <summary>
    /// Provide portable animation data so locomotion can consume authored root
    /// motion. Without this (e.g. Phase 2D diagnostic tests) Update performs
    /// clip selection only and Erika stays in place. The platform host calls
    /// this once after loading the canonical sidecars.
    /// </summary>
    public void SetAnimationData(Skeleton skeleton, AnimationClip idleClip, AnimationClip walkClip, AnimationClip runClip)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(idleClip);
        ArgumentNullException.ThrowIfNull(walkClip);
        ArgumentNullException.ThrowIfNull(runClip);

        _hipsBoneIndex = skeleton.TryGetBoneIndex(ErikaFigure.HipsBoneName, out var index) ? index : -1;
        _walkClip = walkClip;
        _runClip = runClip;
        _previousClipElapsed = 0;
        Transition = null;
    }

    /// <summary>
    /// Camera-relative movement intent from WASD: the camera's horizontal
    /// forward / right basis (Phase 2G third-person orbit; Y dropped so pitch
    /// never adds vertical travel), normalized so diagonals are unit length
    /// (W+D is not faster than W). Returns zero when there is no movement input
    /// (including opposite keys canceling, e.g. W+S).
    /// World mapping at default camera yaw (forward -Z, right +X):
    /// W=(0,0,-1), S=(0,0,+1), A=(-1,0,0), D=(+1,0,0), diagonals normalized.
    /// </summary>
    public Vector3 ComputeMovementIntent(InputState input)
    {
        var forward = Camera.Forward;
        forward.Y = 0;
        var right = Camera.Right;
        right.Y = 0;

        if (forward.LengthSquared() < 1e-8f)
        {
            forward = new Vector3(0, 0, -1);
        }
        else
        {
            forward = Vector3.Normalize(forward);
        }

        if (right.LengthSquared() < 1e-8f)
        {
            right = new Vector3(1, 0, 0);
        }
        else
        {
            right = Vector3.Normalize(right);
        }

        var forwardAmount = (input.MoveForward ? 1 : 0) - (input.MoveBackward ? 1 : 0);
        var rightAmount = (input.StrafeRight ? 1 : 0) - (input.StrafeLeft ? 1 : 0);
        var direction = forward * forwardAmount + right * rightAmount;
        if (direction.LengthSquared() < 1e-8f)
        {
            return Vector3.Zero;
        }

        return Vector3.Normalize(direction);
    }

    public void Update(FrameTime frameTime, InputState input)
    {
        ExitRequested |= input.ExitRequested;

        // Phase 2G: orbit from look input (mouse/arrows) and follow Erika's
        // authoritative world position. Orientation is written before intent so
        // this frame's WASD uses the current camera basis; position follows with
        // a small smoothed lag. WASD still drives Erika, never the camera.
        _thirdPersonCamera.Update(Camera, ErikaPosition, input, frameTime);

        var intent = ComputeMovementIntent(input);
        var hasMovement = intent.LengthSquared() > 1e-8f;

        string? diagnostic = null;
        if (input.SelectIdle)
        {
            diagnostic = ErikaFigure.IdleClipName;
        }
        else if (input.SelectWalk)
        {
            diagnostic = ErikaFigure.WalkClipName;
        }
        else if (input.SelectRun)
        {
            diagnostic = ErikaFigure.RunClipName;
        }

        string requested;
        bool requestedViaDiagnostic;
        if (hasMovement)
        {
            requested = input.Sprint ? ErikaFigure.RunClipName : ErikaFigure.WalkClipName;
            requestedViaDiagnostic = false;
        }
        else if (diagnostic is not null)
        {
            requested = diagnostic;
            requestedViaDiagnostic = true;
        }
        else if (_diagnosticLatch)
        {
            requested = ActiveClipName;
            requestedViaDiagnostic = true;
        }
        else
        {
            requested = ErikaFigure.IdleClipName;
            requestedViaDiagnostic = false;
        }

        if (!string.Equals(requested, ActiveClipName, StringComparison.Ordinal))
        {
            // Phase 2F: start/replace a single short crossfade. The destination
            // becomes the root-motion authority immediately; the source is kept
            // only as a visual pose contributor. Position/yaw stay untouched
            // (no teleport) and StartTransition seeds _previousClipElapsed so no
            // stale previous-clip displacement leaks in.
            StartTransition(requested, frameTime.TotalSeconds);
            _diagnosticLatch = requestedViaDiagnostic;
        }
        else if (hasMovement)
        {
            _diagnosticLatch = false;
        }
        else if (diagnostic is not null)
        {
            _diagnosticLatch = true;
        }

        // Phase 2F smooth yaw: turn toward movement intent at a constant angular
        // speed (shortest path, no overshoot). Root displacement below therefore
        // follows the *smoothed* facing, producing curved travel while turning.
        // Stationary Erika keeps her current facing.
        if (hasMovement)
        {
            var targetYaw = MathF.Atan2(intent.X, intent.Z);
            if (float.IsFinite(targetYaw))
            {
                ErikaYawRadians = YawSmoothing.StepTowards(
                    ErikaYawRadians, targetYaw, TurnSpeedRadiansPerSecond * (float)frameTime.DeltaSeconds);
            }
        }

        // Retire a finished crossfade so normal single-clip rendering resumes.
        if (Transition is { } active && active.IsCompleteAt(frameTime.TotalSeconds))
        {
            Transition = null;
        }

        var currentElapsed = frameTime.TotalSeconds - ClipStartSeconds;
        if (currentElapsed < 0)
        {
            currentElapsed = 0;
        }

        // Authored root motion is the locomotion authority (no magic speed).
        // Only the destination (ActiveClipName) clip contributes; a transition's
        // source pose never adds a second root delta. Idle never moves (its
        // small Hips drift stays skeletal only).
        if (HasRootMotionData() && IsLocomotionClip(ActiveClipName))
        {
            var clip = string.Equals(ActiveClipName, ErikaFigure.RunClipName, StringComparison.Ordinal)
                ? _runClip!
                : _walkClip!;
            var deltaNative = RootMotionEvaluator.ComputeDelta(clip, _hipsBoneIndex, _previousClipElapsed, currentElapsed);
            if (float.IsFinite(deltaNative.X) && float.IsFinite(deltaNative.Y) && float.IsFinite(deltaNative.Z))
            {
                var horizontalMeters = new Vector3(deltaNative.X, 0f, deltaNative.Z) * ErikaFigure.Scale;
                if (float.IsFinite(horizontalMeters.X) && float.IsFinite(horizontalMeters.Z))
                {
                    var heading = Quaternion.CreateFromYawPitchRoll(ErikaYawRadians, 0f, 0f);
                    var worldDelta = Vector3.Transform(horizontalMeters, heading);
                    if (float.IsFinite(worldDelta.X) && float.IsFinite(worldDelta.Z))
                    {
                        ErikaPosition += new Vector3(worldDelta.X, 0f, worldDelta.Z);
                    }
                }
            }
        }

        _previousClipElapsed = currentElapsed;
        SyncErikaInstance();
    }

    /// <summary>
    /// Begin (or replace) the single in-flight crossfade to
    /// <paramref name="requested"/> and make it the root-motion authority.
    ///
    /// Interruption policy (bounded, at most one transition):
    /// - returning to the current source (e.g. releasing mid idle→walk) reverses
    ///   the blend: source and destination swap and the progress is remapped to
    ///   1 - progress, so the visible pose is continuous and no pop occurs;
    /// - any other change chains from the previous destination (the currently
    ///   authoritative clip) with a fresh blend, keeping state bounded.
    ///
    /// <c>_previousClipElapsed</c> is seeded to the destination's current phase
    /// so the switch frame applies zero root delta and resuming a mid-loop clip
    /// never replays historical travel.
    /// </summary>
    private void StartTransition(string requested, double now)
    {
        var sourceName = ActiveClipName;
        var sourceStart = ClipStartSeconds;
        var destinationStart = now;
        var transitionStart = now;

        if (Transition is { } current)
        {
            if (string.Equals(requested, current.SourceClipName, StringComparison.Ordinal))
            {
                // Reverse: continue from the same visible pose with swapped ends.
                sourceName = current.DestinationClipName;
                sourceStart = current.DestinationClipStartSeconds;
                destinationStart = current.SourceClipStartSeconds;
                var alpha = current.ProgressAt(now);
                transitionStart = now - (1.0 - alpha) * LocomotionBlendDurationSeconds;
            }
            else
            {
                sourceName = current.DestinationClipName;
                sourceStart = current.DestinationClipStartSeconds;
            }
        }

        ActiveClipName = requested;
        ClipStartSeconds = destinationStart;
        Transition = new AnimationTransition(
            sourceName,
            sourceStart,
            requested,
            destinationStart,
            transitionStart,
            LocomotionBlendDurationSeconds);

        var elapsed = now - destinationStart;
        _previousClipElapsed = elapsed < 0 ? 0 : elapsed;
    }

    public void Resize(int width, int height)
    {
        Camera.SetViewportSize(width, height);
    }

    public void Render(IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        renderer.Render(World, Camera);
    }

    private bool HasRootMotionData() =>
        _hipsBoneIndex >= 0 && _walkClip is not null && _runClip is not null;

    private static bool IsLocomotionClip(string clipName) =>
        string.Equals(clipName, ErikaFigure.WalkClipName, StringComparison.Ordinal) ||
        string.Equals(clipName, ErikaFigure.RunClipName, StringComparison.Ordinal);

    private void SyncErikaInstance()
    {
        foreach (var model in World.Models)
        {
            if (string.Equals(model.Asset.Name, ErikaFigure.AssetId.Name, StringComparison.Ordinal))
            {
                var rotation = Quaternion.CreateFromYawPitchRoll(ErikaYawRadians, 0f, 0f);
                model.Transform = new Transform(ErikaPosition, rotation, Vector3.One);
            }
        }
    }
}
