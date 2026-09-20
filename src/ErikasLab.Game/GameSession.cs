using ErikasLab.Engine;

namespace ErikasLab.Game;

public sealed class GameSession
{
    public const string Version = "0.1.0-phase1";

    private readonly CameraController _cameraController = new();

    public GameSession()
    {
        World = TestWorldFactory.Create();
        Camera = new CameraState(new System.Numerics.Vector3(0, 3.2f, 9.5f), pitchRadians: -0.08f);
    }

    public Scene World { get; }

    public CameraState Camera { get; }

    public bool ExitRequested { get; private set; }

    /// <summary>
    /// Phase 2D validation selector: which of the three clips the skeletal
    /// renderer should play. Hard switch only; no blending/state machine.
    /// Defaults to idle; 1/2/3 select idle/walk/run and reset playback
    /// deterministically to the loop start.
    /// </summary>
    public string ActiveClipName { get; private set; } = ErikaFigure.IdleClipName;

    /// <summary>Absolute game-clock time the active clip started (loop origin).</summary>
    public double ClipStartSeconds { get; private set; }

    public void Update(FrameTime frameTime, InputState input)
    {
        ExitRequested |= input.ExitRequested;
        _cameraController.Update(Camera, input, frameTime);

        string? requested = null;
        if (input.SelectIdle)
        {
            requested = ErikaFigure.IdleClipName;
        }
        else if (input.SelectWalk)
        {
            requested = ErikaFigure.WalkClipName;
        }
        else if (input.SelectRun)
        {
            requested = ErikaFigure.RunClipName;
        }

        if (requested is not null
            && !string.Equals(requested, ActiveClipName, StringComparison.Ordinal))
        {
            ActiveClipName = requested;
            ClipStartSeconds = frameTime.TotalSeconds;
        }
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
}
