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
    /// window. Constant angular speed, shortest path, no overshoot. Phase 2J
    /// reuses this rate for stationary turn-in-place.
    /// </summary>
    public const float TurnSpeedRadiansPerSecond = MathF.PI * 4f;

    /// <summary>
    /// Phase 2J centralized stationary-turn speed threshold (m/s). At or below
    /// this speed Erika counts as stationary for turn-in-place entry. Slightly
    /// above the Phase 2I zero-speed clamp (0.01 m/s) so a barely-creeping
    /// character can still turn, yet far below any readable walk pace
    /// (walk ≈ 1.69 m/s), so coasting/decelerating motion always wins.
    /// </summary>
    public const float StationaryTurnSpeedThresholdMetersPerSecond = 0.1f;

    /// <summary>
    /// Phase 2J centralized turn-in-place entry angle (radians): 45°. A
    /// stationary requested heading at least this far from the current yaw
    /// enters a stationary turn-in-place. Smaller corrections keep the ordinary
    /// Phase 2F yaw smoothing while travel begins. Inclusive boundary.
    /// </summary>
    public const float TurnInPlaceEnterAngleRadians = MathF.PI / 4f;

    /// <summary>
    /// Phase 2J centralized turn-in-place release angle (radians): 15°. Once
    /// the heading error falls to or below this smaller angle, the turn state
    /// ends and the normal Phase 2I acceleration envelope begins toward the
    /// authored walk/run speed. The gap to the enter angle is hysteresis, so
    /// input hovering near the boundary cannot flicker the state.
    /// </summary>
    public const float TurnInPlaceReleaseAngleRadians = MathF.PI / 12f;

    private readonly MovementSpeedEnvelope _moveSpeed = new();

    /// <summary>
    /// Phase 2K active-clip visual pose clock. Advances with the speed-derived
    /// playback rate and is sampled by the renderer for the visible stride.
    /// Completely independent of the authoritative root-motion clock
    /// (<see cref="ClipStartSeconds"/> + absolute game time), so it can never
    /// influence world displacement.
    /// </summary>
    private AnimationPlaybackClock _poseClock = new();

    /// <summary>
    /// Phase 2K outgoing-clip visual pose clock while a crossfade is in flight
    /// (null otherwise). Each side of the blend owns its own phase and rate.
    /// </summary>
    private AnimationPlaybackClock? _sourcePoseClock;

    private AnimationClip? _idleClip;
    private AnimationClip? _walkClip;
    private AnimationClip? _runClip;
    private int _hipsBoneIndex = -1;
    private double _previousClipElapsed;
    private bool _diagnosticLatch;
    private float _walkAuthoredSpeed;
    private float _runAuthoredSpeed;
    private float _targetMoveSpeed;
    private float _rootMotionGain;
    private float _visualPlaybackRate = 1f;
    private float _rawVisualPlaybackRate = 1f;
    private bool _turnInPlaceActive;
    private bool _hasRequestedHeading;
    private float _requestedHeadingRadians;

    public GameSession()
    {
        World = EnvironmentFactory.Create();

        // Phase 2M: the environment owns the static obstruction descriptions;
        // hand them to the camera rig, which owns the constrained position.
        CameraObstructions = EnvironmentFactory.CreateCameraObstructions();
        _thirdPersonCamera.Obstructions = CameraObstructions;

        // Phase 2N: the environment also owns the flat-XZ player-collision set;
        // GameSession constrains the requested root-motion displacement against
        // it. Player collision and camera obstruction are independent sets.
        PlayerCollisions = EnvironmentFactory.CreatePlayerCollisionSet();

        // GameSession owns Erika: the environment builds static geometry only and
        // this instance's transform is synced from her authoritative state.
        World.AddModel(ErikaFigure.CreateInstance());

        Camera = new CameraState(new Vector3(0, 3.2f, 9.5f));
        ErikaPosition = ErikaFigure.GroundPosition;

        // Phase 2H: Erika starts facing the exact horizontal direction initial
        // W will request (the camera's orbit/control forward), so the game does
        // not open on the front of a model that immediately turns 180 degrees.
        // This is derived from the camera convention, not a hard-coded offset.
        ErikaYawRadians = _thirdPersonCamera.InitialFacingYawRadians;

        // Phase 2G: establish a settled third-person frame at spawn (snap, not
        // a cross-world fly-in). Phase 2M: the spawn frame is outside the
        // longhouse and unobstructed; player collision still does not exist.
        _thirdPersonCamera.SnapToTarget(Camera, ErikaPosition);
    }

    public Scene World { get; }

    /// <summary>
    /// Phase 2M static camera-obstruction set owned by the environment and
    /// consumed by <see cref="CameraRig"/>. The rig clamps its follow position
    /// against these boxes; player movement is never affected.
    /// </summary>
    public CameraObstructionSet CameraObstructions { get; }

    /// <summary>
    /// Phase 2N static player-collision set owned by the environment and
    /// consumed by the sweep-and-slide resolver. Distinct from
    /// <see cref="CameraObstructions"/> (which also contains roof/lintel
    /// geometry irrelevant to the player's feet) and never shared with the
    /// camera rig.
    /// </summary>
    public PlayerCollisionSet PlayerCollisions { get; }

    /// <summary>
    /// Phase 2N player-collision toggle. True (default) constrains the
    /// root-motion-requested displacement against <see cref="PlayerCollisions"/>;
    /// false applies the Phase 2M displacement verbatim. This is a validation
    /// seam for the clear-space A/B regression test, exactly like
    /// <see cref="VisualPlaybackSynchronizationEnabled"/> for Phase 2K.
    /// </summary>
    public bool PlayerCollisionEnabled { get; set; } = true;

    /// <summary>
    /// Phase 2N displacement (meters) the authored root-motion stack requested
    /// this frame before environmental constraint. Y is always 0 (flat XZ).
    /// </summary>
    public Vector3 RequestedPlayerDisplacement { get; private set; }

    /// <summary>
    /// Phase 2N displacement (meters) accepted after collision constraint.
    /// Equals <see cref="RequestedPlayerDisplacement"/> when nothing is hit.
    /// </summary>
    public Vector3 AcceptedPlayerDisplacement { get; private set; }

    /// <summary>Phase 2N true when collision removed or corrected any requested displacement this frame.</summary>
    public bool WasPlayerCollisionConstrained { get; private set; }

    /// <summary>Phase 2N number of sweep contacts resolved this frame.</summary>
    public int PlayerCollisionHitCount { get; private set; }

    /// <summary>Phase 2N name of the last blocker contacted this frame, or null.</summary>
    public string? LastPlayerCollisionName { get; private set; }

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
    /// Phase 2K visual playback synchronization toggle. True (default) derives
    /// the visible locomotion cadence from the Phase 2I speed envelope; false
    /// forces nominal 1x visual playback for every clip (the Phase 2I baseline).
    /// This is a validation seam: gameplay world motion is identical either way,
    /// which is exactly the invariant the root-motion independence tests assert.
    /// Idle is always 1x regardless.
    /// </summary>
    public bool VisualPlaybackSynchronizationEnabled { get; set; } = true;

    /// <summary>
    /// Phase 2K applied visual playback rate for the active clip this frame.
    /// 1 for idle, <c>speed / authoredSpeed</c> clamped to
    /// <c>[LocomotionPlaybackRates.MinimumRate, MaximumRate]</c> for walk/run.
    /// Always finite and non-negative; never used for world displacement.
    /// </summary>
    public float VisualPlaybackRate => _visualPlaybackRate;

    /// <summary>
    /// Phase 2K raw (unbounded) visual playback rate for the active clip before
    /// the centralized cap. Equal to <see cref="RootMotionGain"/> for
    /// uncapped locomotion and may exceed <see cref="VisualPlaybackRate"/> during
    /// a run-to-walk deceleration.
    /// </summary>
    public float RawVisualPlaybackRate => _rawVisualPlaybackRate;

    /// <summary>
    /// Phase 2K active-clip visual pose phase in <c>[0, clip duration)</c>.
    /// Sampled by the renderer; independent of the root-motion clock.
    /// </summary>
    public double VisualPoseElapsedSeconds => _poseClock.ElapsedSeconds;

    /// <summary>Phase 2K outgoing-clip visual pose phase during a crossfade (0 otherwise).</summary>
    public double TransitionSourcePoseElapsedSeconds => _sourcePoseClock?.ElapsedSeconds ?? 0.0;

    /// <summary>Phase 2K incoming-clip visual pose phase during a crossfade.</summary>
    public double TransitionDestinationPoseElapsedSeconds => _poseClock.ElapsedSeconds;

    /// <summary>
    /// Phase 2J true while a stationary turn-in-place is active. While active,
    /// the turn gates translation: idle stays the visual clip and the target
    /// movement speed stays zero until the heading error reaches
    /// <see cref="TurnInPlaceReleaseAngleRadians"/>.
    /// </summary>
    public bool IsTurningInPlace => _turnInPlaceActive;

    /// <summary>
    /// Phase 2J true while the stationary turn gates translational
    /// acceleration (identical to <see cref="IsTurningInPlace"/>; the gate is
    /// active exactly while the turn state is active).
    /// </summary>
    public bool TurnGatingActive => _turnInPlaceActive;

    /// <summary>
    /// Phase 2J true while directional movement intent requests a heading.
    /// </summary>
    public bool HasRequestedHeading => _hasRequestedHeading;

    /// <summary>
    /// Phase 2J current requested movement heading (radians, camera-relative).
    /// Only meaningful while <see cref="HasRequestedHeading"/> is true.
    /// </summary>
    public float RequestedHeadingRadians => _requestedHeadingRadians;

    /// <summary>
    /// Phase 2J absolute wrapped heading error (radians) between the current
    /// yaw and the requested heading; 0 when no heading is requested.
    /// </summary>
    public float HeadingErrorRadians =>
        _hasRequestedHeading
            ? MathF.Abs(YawSmoothing.WrapToPi(_requestedHeadingRadians - ErikaYawRadians))
            : 0f;

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
        _idleClip = idleClip;
        _walkClip = walkClip;
        _runClip = runClip;
        _previousClipElapsed = 0;
        Transition = null;

        // Phase 2K: start the visual pose clock at the idle loop origin at 1x.
        // The root-motion clock is untouched; the two remain independent.
        _poseClock = new AnimationPlaybackClock();
        _poseClock.Reset(0.0, 1f);
        _sourcePoseClock = null;
        _visualPlaybackRate = 1f;
        _rawVisualPlaybackRate = 1f;

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

        // Phase 2J: resolve the stationary turn-in-place state from this frame's
        // requested heading *before* clip selection, so the gate can hold idle
        // and zero the translational target while a large heading error is
        // resolved. Runs before yaw smoothing so the entry decision uses the
        // start-of-frame yaw (frame-rate independent at the exact threshold).
        var requestedHeading = hasMovement ? MathF.Atan2(intent.X, intent.Z) : 0f;
        UpdateTurnInPlace(hasMovement, requestedHeading);

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
        if (_turnInPlaceActive)
        {
            // Phase 2J translational gate: while the stationary turn is active,
            // idle stays the visual clip (no walk/run root authority, so no world
            // translation is consumed) and the target speed stays zero. The
            // normal Phase 2I acceleration envelope begins on the release frame.
            requested = ErikaFigure.IdleClipName;
            requestedViaDiagnostic = false;
            targetSpeed = 0f;
        }
        else if (hasMovement)
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

        // Phase 2K: advance the independent visual pose clock(s) from this
        // frame's speed envelope. This never feeds root motion below; it only
        // decides which skeletal pose the renderer samples. The active clock
        // drives ActiveClipName; during a crossfade the outgoing clip advances
        // on its own clock and rate.
        AdvancePoseClocks(frameTime.DeltaSeconds);

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
        var requestedDisplacement = Vector3.Zero;
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
                        requestedDisplacement = new Vector3(worldDelta.X, 0f, worldDelta.Z);
                    }
                }
            }
        }

        // Phase 2N: constrain the root-motion-requested displacement against the
        // static environment. Collision is a constraint only; it never adds a
        // second velocity/position authority, never rotates Erika, and never
        // touches the Phase 2I speed envelope or the Phase 2K playback clock.
        ApplyPlayerCollision(requestedDisplacement);

        // Phase 2H: follow with the current (post-movement) Erika position so the
        // rendered camera looks exactly at her now; only the camera position
        // trails, never its orientation.
        _thirdPersonCamera.Follow(Camera, ErikaPosition, frameTime);

        _previousClipElapsed = currentElapsed;
        SyncErikaInstance();
    }

    /// <summary>
    /// Phase 2J stationary turn-in-place state resolution, run once per frame
    /// before clip selection and yaw smoothing.
    ///
    /// Entry requires all of: directional intent, an effectively stationary
    /// Phase 2I movement speed (coasting/decelerating motion never enters), and
    /// a wrapped heading error of at least
    /// <see cref="TurnInPlaceEnterAngleRadians"/>. Once active, the newest
    /// requested heading is tracked every frame (shortest-path yaw integration
    /// is handled by the existing Phase 2F smoother; no turns are queued) and
    /// the state releases when the error falls to
    /// <see cref="TurnInPlaceReleaseAngleRadians"/> or below, after which the
    /// normal acceleration envelope takes over. Losing directional intent
    /// cancels the turn immediately and retains the current facing.
    /// </summary>
    private void UpdateTurnInPlace(bool hasRequestedHeading, float requestedHeading)
    {
        if (!hasRequestedHeading || !float.IsFinite(requestedHeading))
        {
            _hasRequestedHeading = false;
            _turnInPlaceActive = false;
            return;
        }

        _hasRequestedHeading = true;
        _requestedHeadingRadians = requestedHeading;

        var stationary =
            _moveSpeed.CurrentMetersPerSecond <= StationaryTurnSpeedThresholdMetersPerSecond;
        if (!stationary)
        {
            // Phase 2I: residual/coasting motion means Erika is still moving;
            // the existing coast-facing policy applies and a stationary turn
            // must not start (or resume) until the speed reaches the threshold.
            _turnInPlaceActive = false;
            return;
        }

        var error = MathF.Abs(YawSmoothing.WrapToPi(requestedHeading - ErikaYawRadians));
        if (!_turnInPlaceActive)
        {
            if (error >= TurnInPlaceEnterAngleRadians)
            {
                _turnInPlaceActive = true;
            }
        }
        else if (error <= TurnInPlaceReleaseAngleRadians)
        {
            _turnInPlaceActive = false;
        }
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
        var reversal = false;

        if (Transition is { } current)
        {
            if (string.Equals(requested, current.SourceClipName, StringComparison.Ordinal))
            {
                // Reverse: continue from the same visible pose with swapped ends.
                reversal = true;
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

        // Phase 2K pose-clock policy (each side owns its own phase and rate):
        // - the outgoing pose continues from whichever clip was most recently
        //   the authority, which is the current active clock;
        // - the incoming pose resumes the old source phase on a reversal (so an
        //   interrupted blend never snaps its pose), or restarts at the loop
        //   origin on a hard/chained switch (the established Phase 2F policy).
        // The fresh destination clock is seeded with the new clip's current rate
        // so its first trapezoidal step starts from the right cadence.
        var outgoingClock = _poseClock;
        var resumedClock = reversal ? _sourcePoseClock : null;
        _sourcePoseClock = outgoingClock;
        _poseClock = resumedClock ?? new AnimationPlaybackClock();
        if (!reversal)
        {
            _poseClock.Reset(0.0, VisualRateFor(requested));
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

    /// <summary>Loop duration (seconds) of a stable clip id; 0 when unavailable.</summary>
    private double ClipDurationFor(string clipName)
    {
        if (string.Equals(clipName, ErikaFigure.RunClipName, StringComparison.Ordinal))
        {
            return _runClip?.DurationSeconds ?? 0.0;
        }

        if (string.Equals(clipName, ErikaFigure.WalkClipName, StringComparison.Ordinal))
        {
            return _walkClip?.DurationSeconds ?? 0.0;
        }

        return _idleClip?.DurationSeconds ?? 0.0;
    }

    /// <summary>
    /// Phase 2K raw (uncapped) visual rate for a clip. Idle always plays at 1x
    /// (never tied to translational speed); locomotion uses the centralized
    /// <see cref="LocomotionPlaybackRates.RawRate"/> derivation.
    /// </summary>
    private float RawVisualRateFor(string clipName) =>
        IsLocomotionClip(clipName)
            ? LocomotionPlaybackRates.RawRate(_moveSpeed.CurrentMetersPerSecond, AuthoredSpeedFor(clipName))
            : 1f;

    /// <summary>
    /// Phase 2K applied (bounded) visual rate for a clip. Idle is 1x; locomotion
    /// is <c>speed / authoredSpeed</c> clamped to the centralized bounds. When
    /// <see cref="VisualPlaybackSynchronizationEnabled"/> is false every clip
    /// plays at nominal 1x (the Phase 2I baseline) without touching root motion.
    /// </summary>
    private float VisualRateFor(string clipName)
    {
        if (!VisualPlaybackSynchronizationEnabled || !IsLocomotionClip(clipName))
        {
            return 1f;
        }

        return LocomotionPlaybackRates.AppliedRate(_moveSpeed.CurrentMetersPerSecond, AuthoredSpeedFor(clipName));
    }

    /// <summary>
    /// Phase 2K advance the visual pose clock(s) from this frame's speed
    /// envelope. Purely visual: the authoritative root-motion clock below is
    /// never derived from these clocks. The active clock drives
    /// <see cref="ActiveClipName"/>; during a crossfade the outgoing clip keeps
    /// its own clock and rate.
    /// </summary>
    private void AdvancePoseClocks(double deltaSeconds)
    {
        _rawVisualPlaybackRate = RawVisualRateFor(ActiveClipName);
        _visualPlaybackRate = VisualRateFor(ActiveClipName);
        _poseClock.Advance(deltaSeconds, _visualPlaybackRate, ClipDurationFor(ActiveClipName));

        if (Transition is { } transition && _sourcePoseClock is not null)
        {
            var sourceRate = VisualRateFor(transition.SourceClipName);
            _sourcePoseClock.Advance(deltaSeconds, sourceRate, ClipDurationFor(transition.SourceClipName));
        }
    }

    /// <summary>
    /// Phase 2N apply the requested root-motion displacement to Erika's world
    /// position, constrained by the static player-collision set. The collision
    /// resolver only filters the requested translation; Erika's Y (flat ground)
    /// and yaw are untouched. When collision is disabled the displacement is
    /// applied verbatim, which is the Phase 2M behavior and the A/B seam.
    /// </summary>
    private void ApplyPlayerCollision(Vector3 requestedDisplacement)
    {
        RequestedPlayerDisplacement = requestedDisplacement;

        if (!PlayerCollisionEnabled)
        {
            ErikaPosition += requestedDisplacement;
            AcceptedPlayerDisplacement = requestedDisplacement;
            WasPlayerCollisionConstrained = false;
            PlayerCollisionHitCount = 0;
            LastPlayerCollisionName = null;
            return;
        }

        var resolved = PlayerCollisionResolver.Resolve(
            PlayerCollisions,
            new Vector2(ErikaPosition.X, ErikaPosition.Z),
            new Vector2(requestedDisplacement.X, requestedDisplacement.Z),
            PlayerCollisionPolicy.PlayerCollisionRadiusMeters);

        ErikaPosition = new Vector3(resolved.Position.X, ErikaPosition.Y, resolved.Position.Y);
        AcceptedPlayerDisplacement = new Vector3(resolved.Delta.X, 0f, resolved.Delta.Y);
        WasPlayerCollisionConstrained = resolved.Constrained;
        PlayerCollisionHitCount = resolved.HitCount;
        LastPlayerCollisionName = resolved.LastHitName;
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
