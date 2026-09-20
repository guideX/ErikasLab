using System.Numerics;
using ErikasLab.Engine;

namespace ErikasLab.Game;

public sealed class GameSession
{
    public const string Version = "0.1.0-phase1";

    private readonly CameraController _cameraController = new();

    private AnimationClip? _walkClip;
    private AnimationClip? _runClip;
    private int _hipsBoneIndex = -1;
    private double _previousClipElapsed;
    private bool _diagnosticLatch;

    public GameSession()
    {
        World = TestWorldFactory.Create();
        Camera = new CameraState(new Vector3(0, 3.2f, 9.5f), pitchRadians: -0.08f);
        ErikaPosition = ErikaFigure.GroundPosition;
        ErikaYawRadians = ErikaFigure.FacingYawRadians;
    }

    public Scene World { get; }

    public CameraState Camera { get; }

    public bool ExitRequested { get; private set; }

    /// <summary>
    /// Phase 2E locomotion selection: idle/walk/run via movement intent
    /// (WASD + Shift) with 1/2/3 retained as a diagnostic latch. Hard switch
    /// only; no blending/state machine. Defaults to idle.
    /// Movement takes precedence over diagnostics; releasing movement returns
    /// to idle unless the last selection was diagnostic (which latches,
    /// preserving Phase 2D behavior for headless/diagnostic runs).
    /// </summary>
    public string ActiveClipName { get; private set; } = ErikaFigure.IdleClipName;

    /// <summary>Absolute game-clock time the active clip started (loop origin).</summary>
    public double ClipStartSeconds { get; private set; }

    /// <summary>World-space (meters) Erika origin. Y stays at ground (0).</summary>
    public Vector3 ErikaPosition { get; private set; } = ErikaFigure.GroundPosition;

    /// <summary>Yaw (radians, Y-up) Erika faces. Snapped to movement heading.</summary>
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
    }

    /// <summary>
    /// Camera-relative movement intent from WASD: horizontal camera forward /
    /// right basis (existing camera architecture, no camera redesign),
    /// normalized so diagonals are unit length (W+D is not faster than W).
    /// Returns zero when there is no movement input (including opposite keys
    /// canceling, e.g. W+S).
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

        // Phase 2E control routing: WASD now drives Erika (movement intent).
        // The camera keeps look (mouse/arrows) but no longer translates with
        // WASD; this is a control reassignment, not a camera-system redesign.
        var cameraInput = input with
        {
            MoveForward = false,
            MoveBackward = false,
            StrafeLeft = false,
            StrafeRight = false,
        };
        _cameraController.Update(Camera, cameraInput, frameTime);

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
            // Hard switch: new loop origin, reset root-motion bookkeeping so no
            // stale previous-clip displacement leaks into the new clip.
            // Position/yaw otherwise untouched (no teleport).
            ActiveClipName = requested;
            ClipStartSeconds = frameTime.TotalSeconds;
            _previousClipElapsed = 0;
            _diagnosticLatch = requestedViaDiagnostic;
            if (hasMovement)
            {
                var yaw = MathF.Atan2(intent.X, intent.Z);
                if (float.IsFinite(yaw))
                {
                    ErikaYawRadians = yaw;
                }
            }

            SyncErikaInstance();
            return;
        }

        // Same clip: never restart the loop origin.
        if (hasMovement)
        {
            _diagnosticLatch = false;
            var yaw = MathF.Atan2(intent.X, intent.Z);
            if (float.IsFinite(yaw))
            {
                ErikaYawRadians = yaw;
            }
        }
        else if (diagnostic is not null)
        {
            _diagnosticLatch = true;
        }

        var currentElapsed = frameTime.TotalSeconds - ClipStartSeconds;
        if (currentElapsed < 0)
        {
            currentElapsed = 0;
        }

        // Authored root motion is the locomotion authority (no magic speed).
        // Idle never moves (its small Hips drift stays skeletal only).
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
