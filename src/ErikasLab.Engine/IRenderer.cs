namespace ErikasLab.Engine;

public interface IRenderer : IDisposable
{
    string BackendName { get; }

    RendererInfo Info { get; }

    void Resize(int width, int height);

    void Render(Scene scene, CameraState camera);
}

public readonly record struct RendererInfo(string BackendName, string DeviceName, int BackbufferWidth, int BackbufferHeight);
