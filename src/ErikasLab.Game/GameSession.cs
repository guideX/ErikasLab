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

    private readonly MovementSpeedEnvelope _moveSpeed = new();
    private AnimationClip? _walkClip;
    private AnimationClip? _runClip;
    private int _hipsBoneIndex = -1;
    private double _previousClipElapsed;
    private bool _diagnosticLatch;
    private float _walkAuthoredSpeed;
    private float _runAuthoredSpeed;
    private float _targetMoveSpeed;
    private float _rootMotionGain;

    public GameSession()
    {
        World = TestWorldFactory.Create();
        Camera = new CameraState(new Vector3(0, 3.2f, 9.5f));
        ErikaPosition = ErikaFigure.GroundPosition;

        // Phase 2H: Erika starts facing the exact horizontal direction initial
        // W will request (the camera's orbit/control forward), so the game does
        // not open on the front of a model that immediately turns 180 degrees.
        // This is derived from the camera convention, not a hard-coded offset.
        ErikaYawRadians = _thirdPersonCamera.InitialFacingYawRadians;

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
    /// Yaw (radians, Y-up) Erika faces. Phase 2H spawns it aligned with the
    /// initial camera-control forward; Phase 2F then smoothly turns toward the
    /// movement heading at <see cref="TurnSpeedRadiansPerSecond"/>; stationary
    /// retains it.
    /// </summary>
    public float ErikaYawRadians { get; private set; }

    /// <summary>
    /// Phase 2I current filtered movement speed (m/s), finite and >= 0. This is
    /// the speed envelope that scales authored root displacement; it is not a
    /// position or velocity vector.
    /// </summary>
    public float CurrentMoveSpeedMetersPerSecond => _moveSpeed.CurrentMetersPerSecond;

    /// <summary>Phase 2I target movement speed (m/s) requested this frame.</summary>
    public float TargetMoveSpeedMetersPerSecond => _targetMoveSpeed;

    /// <summary>
    /// Phase 2I authored steady-state speed (m/s) of the active clip (0 for
    /// idle). Derived from animation root displacement, duration, and world
    /// scale; never a literal constant.
    /// </summary>
    public float ActiveClipAuthoredSpeedMetersPerSecond => AuthoredSpeedFor(ActiveClipName);

    /// <summary>
    /// Phase 2I root-motion gain applied this frame
    /// (<c>current speed / active-clip authored speed</c>). Normally 1 while
    /// walking/running steadily, ramps from 0 on start, and may temporarily
    /// exceed 1 during run-to-walk deceleration (bounded by the run/walk speed
    /// ratio).
    /// </summary>
    public float RootMotionGain => _rootMotionGain;

    /// <summary>Phase 2I authored walk speed (m/s) derived from the walk clip.</summary>
    public float WalkAuthoredSpeedMetersPerSecond => _walkAuthoredSpeed;

    /// <summary>Phase 2I authored run speed (m/s) derived from the run clip.</summary>
    public float RunAuthoredSpeedMetersPerSecond => _runAuthoredSpeed;

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

        // Phase 2I: derive the authoritative steady-state speeds from the
        // authored root displacement (no literal walk/run magic numbers) and
        // start the movement-speed envelope at rest.
        _walkAuthoredSpeed = RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(
            walkClip, _hipsBoneIndex, ErikaFigure.Scale);
        _runAuthoredSpeed = RootMotionEvaluator.ComputeHorizontalSpeedMetersPerSecond(
            runClip, _hipsBoneIndex, ErikaFigure.Scale);
        _moveSpeed.Reset(0f);
        _targetMoveSpeed = 0f;
        _rootMotionGain = 0f;
    }

    /// <summary>
    /// Camera-relative movement intent from WASD, using the rig's horizontal
    /// orbit/control basis (Phase 2H; Y=0 so pitch never adds vertical travel).
    /// This basis is derived from orbit yaw only and is independent of the
    /// rendered look direction and of positional follow lag, so the smoothed
    /// camera position can never bend player intent. Diagonals normalize; returns
    /// zero when there is no movement input (including opposite keys canceling).
    /// World mapping at default camera yaw (forward -Z, right +X):
    /// W=(0,0,-1), S=(0,0,+1), A=(-1,0,0), D=(+1,0,0), diagonals normalized.
    /// </summary>
    public Vector3 ComputeMovementIntent(InputState input)
    {
        var forward = _thirdPersonCamera.ControlForward;
        var right = _thirdPersonCamera.ControlRight;

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

        // Phase 2G/2H: apply look input to the orbit first so this frame's WASD
        // uses the fresh orbit/control basis. The camera is followed *after*
        // root motion below, so its rendered look-at targets Erika's current
        // world position rather than last frame's. WASD still drives Erika,
        // never the camera.
        _thirdPersonCamera.UpdateOrbit(input, frameTime);

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

        // Phase 2I: resolve the requested locomotion clip, whether it is a
        // diagnostic request, and this frame's movement-speed target. The target
        // is authored-clip-derived (never a literal speed). With no intent and no
        // diagnostic, the active walk/run clip is *held* as the single
        // root-motion authority until the speed envelope reaches zero; only then
        // does idle become the request, so stopping decelerates through the
        // authored animation rather than halting instantly.
        string requested;
        bool requestedViaDiagnostic;
        float targetSpeed;
        if (hasMovement)
        {
            requested = input.Sprint ? ErikaFigure.RunClipName : ErikaFigure.WalkClipName;
            requestedViaDiagnostic = false;
            targetSpeed = AuthoredSpeedFor(requested);
        }
        else if (diagnostic is not null)
        {
            requested = diagnostic;
            requestedViaDiagnostic = true;
            targetSpeed = AuthoredSpeedFor(diagnostic);
        }
        else if (_diagnosticLatch)
        {
            requested = ActiveClipName;
            requestedViaDiagnostic = true;
            targetSpeed = AuthoredSpeedFor(ActiveClipName);
        }
        else
        {
            requestedViaDiagnostic = false;
            targetSpeed = 0f;
            requested = IsLocomotionClip(ActiveClipName)
                && _moveSpeed.CurrentMetersPerSecond > MovementSpeedEnvelope.ZeroSpeedThresholdMetersPerSecond
                    ? ActiveClipName
                    : ErikaFigure.IdleClipName;
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

        // Phase 2I: integrate the movement-speed envelope toward this frame's
        // target. Acceleration/deceleration are elapsed-time based, so the same
        // total time yields the same speed regardless of frame rate, and the
        // result never overshoots or goes negative.
        _targetMoveSpeed = targetSpeed;
        _moveSpeed.Advance(
            targetSpeed,
            frameTime.DeltaSeconds,
            MovementSpeedEnvelope.DefaultAccelerationMetersPerSecondSquared,
            MovementSpeedEnvelope.DefaultDecelerationMetersPerSecondSquared);

        // Phase 2I stop hand-off: once released movement has decelerated to the
        // zero threshold, clamp exactly to zero and begin the idle crossfade. The
        // residual stopping travel above already came from the held clip's root
        // motion, so no separate inertia/velocity system exists.
        if (targetSpeed == 0f
            && !hasMovement
            && diagnostic is null
            && !_diagnosticLatch
            && IsLocomotionClip(ActiveClipName)
            && _moveSpeed.CurrentMetersPerSecond <= MovementSpeedEnvelope.ZeroSpeedThresholdMetersPerSecond)
        {
            _moveSpeed.ClampToZero();
            StartTransition(ErikaFigure.IdleClipName, frameTime.TotalSeconds);
        }

        var currentElapsed = frameTime.TotalSeconds - ClipStartSeconds;
        if (currentElapsed < 0)
        {
            currentElapsed = 0;
        }

        // Authored root motion remains the single locomotion authority (no magic
        // speed, no second translation system). Only the destination
        // (ActiveClipName) clip contributes; a transition's source pose never
        // adds a second root delta. Idle never moves (its small Hips drift stays
        // skeletal only). Phase 2I scales the authored horizontal world delta by
        // the speed gain (current / authored active-clip speed), so starts ramp
        // in, stops ramp out, and walk<->run keep world speed continuous while
        // the path and direction still come entirely from the animation.
        _rootMotionGain = 0f;
        if (HasRootMotionData() && IsLocomotionClip(ActiveClipName))
        {
            var isRun = string.Equals(ActiveClipName, ErikaFigure.RunClipName, StringComparison.Ordinal);
            var clip = isRun ? _runClip! : _walkClip!;
            var authoredSpeed = isRun ? _runAuthoredSpeed : _walkAuthoredSpeed;
            var gain = authoredSpeed > 1e-6f ? _moveSpeed.CurrentMetersPerSecond / authoredSpeed : 0f;
            if (!float.IsFinite(gain) || gain < 0f)
            {
                gain = 0f;
            }

            _rootMotionGain = gain;

            var deltaNative = RootMotionEvaluator.ComputeDelta(clip, _hipsBoneIndex, _previousClipElapsed, currentElapsed);
            if (float.IsFinite(deltaNative.X) && float.IsFinite(deltaNative.Y) && float.IsFinite(deltaNative.Z))
            {
                var horizontalMeters = new Vector3(deltaNative.X, 0f, deltaNative.Z) * (ErikaFigure.Scale * gain);
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

        // Phase 2H: follow with the current (post-movement) Erika position so the
        // rendered camera looks exactly at her now; only the camera position
        // trails, never its orientation.
        _thirdPersonCamera.Follow(Camera, ErikaPosition, frameTime);

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

    private float AuthoredSpeedFor(string clipName)
    {
        if (string.Equals(clipName, ErikaFigure.RunClipName, StringComparison.Ordinal))
        {
            return _runAuthoredSpeed;
        }

        if (string.Equals(clipName, ErikaFigure.WalkClipName, StringComparison.Ordinal))
        {
            return _walkAuthoredSpeed;
        }

        return 0f;
    }

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
