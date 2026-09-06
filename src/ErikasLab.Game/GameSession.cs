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

    public void Update(FrameTime frameTime, InputState input)
    {
        ExitRequested |= input.ExitRequested;
        _cameraController.Update(Camera, input, frameTime);
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
