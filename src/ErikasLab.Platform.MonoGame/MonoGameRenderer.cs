using ErikasLab.Engine;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Numerics = System.Numerics;

namespace ErikasLab.Platform.MonoGame;

internal sealed class MonoGameRenderer : IRenderer
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _rasterizerState;
    private readonly StaticModelRenderer? _modelRenderer;
    private readonly Dictionary<MeshData, GpuMesh> _meshCache = new();
    private bool _disposed;
    private RendererInfo _info;

    public MonoGameRenderer(GraphicsDevice graphicsDevice, StaticModelRenderer? modelRenderer = null)
    {
        _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
        _modelRenderer = modelRenderer;
        _effect = new BasicEffect(graphicsDevice)
        {
            LightingEnabled = true,
            VertexColorEnabled = false,
            TextureEnabled = false,
        };
        _effect.EnableDefaultLighting();
        _effect.DirectionalLight0.Direction = new Vector3(-0.45f, -1f, -0.35f);
        _effect.DirectionalLight0.DiffuseColor = new Vector3(1f, 0.94f, 0.83f);
        _effect.DirectionalLight0.SpecularColor = new Vector3(0.15f, 0.15f, 0.15f);
        _effect.AmbientLightColor = new Vector3(0.28f, 0.31f, 0.36f);
        _rasterizerState = new RasterizerState
        {
            CullMode = CullMode.CullCounterClockwiseFace,
            FillMode = FillMode.Solid,
        };

        BackendName = "MonoGame WindowsDX";
        Resize(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
    }

    public string BackendName { get; }

    public RendererInfo Info => _info;

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _info = new RendererInfo(BackendName, _graphicsDevice.Adapter.Description, width, height);
    }

    public void Render(Scene scene, CameraState camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        var viewport = _graphicsDevice.Viewport;
        Resize(viewport.Width, viewport.Height);
        camera.SetViewportSize(viewport.Width, viewport.Height);

        _graphicsDevice.Clear(new Color(22, 28, 38));
        _graphicsDevice.DepthStencilState = DepthStencilState.Default;
        _graphicsDevice.RasterizerState = _rasterizerState;
        _graphicsDevice.BlendState = BlendState.Opaque;

        _effect.View = ToMonoGameMatrix(camera.ViewMatrix);
        _effect.Projection = ToMonoGameMatrix(camera.ProjectionMatrix);

        foreach (var sceneObject in scene.Objects)
        {
            var gpuMesh = GetOrCreateMesh(sceneObject.Mesh);
            _graphicsDevice.SetVertexBuffer(gpuMesh.VertexBuffer);
            _graphicsDevice.Indices = gpuMesh.IndexBuffer;
            _effect.World = ToMonoGameMatrix(sceneObject.Transform.WorldMatrix);
            var meshColor = sceneObject.Mesh.Vertices[0].Color;
            _effect.DiffuseColor = new Vector3(meshColor.R / 255f, meshColor.G / 255f, meshColor.B / 255f);

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, gpuMesh.PrimitiveCount);
            }
        }

        _modelRenderer?.Draw(scene, _effect.View, _effect.Projection);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var mesh in _meshCache.Values)
        {
            mesh.Dispose();
        }

        _meshCache.Clear();
        _rasterizerState.Dispose();
        _effect.Dispose();
        _disposed = true;
    }

    private GpuMesh GetOrCreateMesh(MeshData mesh)
    {
        if (_meshCache.TryGetValue(mesh, out var gpuMesh))
        {
            return gpuMesh;
        }

        var vertices = mesh.Vertices
            .Select(vertex => new VertexPositionNormalTexture(
                ToMonoGameVector(vertex.Position),
                ToMonoGameVector(vertex.Normal),
                Microsoft.Xna.Framework.Vector2.Zero))
            .ToArray();
        var indices = mesh.Indices.ToArray();

        gpuMesh = new GpuMesh(_graphicsDevice, vertices, indices);
        _meshCache.Add(mesh, gpuMesh);
        return gpuMesh;
    }

    private static Microsoft.Xna.Framework.Vector3 ToMonoGameVector(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Matrix ToMonoGameMatrix(Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);

    private sealed class GpuMesh : IDisposable
    {
        public GpuMesh(GraphicsDevice graphicsDevice, VertexPositionNormalTexture[] vertices, int[] indices)
        {
            VertexBuffer = new VertexBuffer(graphicsDevice, VertexPositionNormalTexture.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);
            VertexBuffer.SetData(vertices);
            IndexBuffer = new IndexBuffer(graphicsDevice, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            IndexBuffer.SetData(indices);
            PrimitiveCount = indices.Length / 3;
        }

        public VertexBuffer VertexBuffer { get; }

        public IndexBuffer IndexBuffer { get; }

        public int PrimitiveCount { get; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }
}
