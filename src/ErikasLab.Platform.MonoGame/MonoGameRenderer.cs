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
    private readonly SamplerState _wrapSampler;
    private readonly AnimatedModelRenderer? _modelRenderer;
    private readonly Dictionary<MeshData, GpuMesh> _meshCache = new();
    private readonly Dictionary<UvVariantKey, MeshData> _uvVariantCache = new();
    private readonly Dictionary<string, Texture2D> _textureBindings = new();
    private readonly TextureCatalog _textureCatalog;
    private bool _disposed;
    private RendererInfo _info;

    public MonoGameRenderer(
        GraphicsDevice graphicsDevice,
        AnimatedModelRenderer? modelRenderer = null,
        TextureCatalog? textureCatalog = null,
        EnvironmentLighting? lighting = null)
    {
        _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
        _modelRenderer = modelRenderer;
        _textureCatalog = textureCatalog ?? new TextureCatalog();
        Lighting = lighting ?? EnvironmentLighting.FimbulWinter;

        _effect = new BasicEffect(graphicsDevice)
        {
            LightingEnabled = true,
            VertexColorEnabled = false,
            TextureEnabled = false,
        };
        ApplyLighting(Lighting);

        _rasterizerState = new RasterizerState
        {
            CullMode = CullMode.CullCounterClockwiseFace,
            FillMode = FillMode.Solid,
        };

        _wrapSampler = new SamplerState
        {
            Filter = TextureFilter.Linear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
        };

        BackendName = "MonoGame WindowsDX";
        Resize(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
    }

    public string BackendName { get; }

    public RendererInfo Info => _info;

    /// <summary>Centralized environment-lighting policy currently applied to the static-world effect.</summary>
    public EnvironmentLighting Lighting { get; }

    /// <summary>Portable texture registry consulted before every texture bind.</summary>
    public TextureCatalog Textures => _textureCatalog;

    /// <summary>
    /// Register real GPU pixel data for a texture id. Binding is keyed by the
    /// same ids the portable <see cref="TextureCatalog"/> knows, so a material
    /// with a matching <see cref="StaticMaterial.TextureId"/> renders
    /// textured; unregistered ids fall back to base color.
    /// </summary>
    public void RegisterTexture(string id, Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(texture);
        _textureBindings[id] = texture;
        _textureCatalog.Register(id);
    }

    /// <summary>Number of draws that requested a texture but fell back to base color.</summary>
    public int TextureFallbackCount { get; private set; }

    /// <summary>Number of draws that bound a registered texture.</summary>
    public int TextureBindCount { get; private set; }

    /// <summary>Static object count of the most recent render.</summary>
    public int LastSceneObjectCount { get; private set; }

    /// <summary>Distinct material count of the most recent render.</summary>
    public int LastDistinctMaterialCount { get; private set; }

    /// <summary>Materials requesting a texture in the most recent render.</summary>
    public int LastTexturedMaterialCount { get; private set; }

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
        _graphicsDevice.SamplerStates[0] = _wrapSampler;

        _effect.View = ToMonoGameMatrix(camera.ViewMatrix);
        _effect.Projection = ToMonoGameMatrix(camera.ProjectionMatrix);

        var materials = new HashSet<StaticMaterial>();
        var texturedMaterials = 0;

        foreach (var sceneObject in scene.Objects)
        {
            var material = sceneObject.Material;
            var mesh = sceneObject.Mesh;
            var textureBound = false;
            if (material.TextureEnabled
                && _textureCatalog.Contains(material.TextureId!)
                && _textureBindings.TryGetValue(material.TextureId!, out var texture))
            {
                // World-repeat UVs: the shared primitive's baked UVs are scaled
                // by this instance's transform scale and the material texture
                // scale, cached per distinct combination (created once, never
                // per frame). The base color stays bound as the BasicEffect
                // diffuse tint around the texture.
                mesh = GetOrCreateVariantMesh(sceneObject, material);
                _effect.Texture = texture;
                _effect.TextureEnabled = true;
                textureBound = true;
                TextureBindCount++;
            }

            var gpuMesh = GetOrCreateMesh(mesh);
            _graphicsDevice.SetVertexBuffer(gpuMesh.VertexBuffer);
            _graphicsDevice.Indices = gpuMesh.IndexBuffer;
            _effect.World = ToMonoGameMatrix(sceneObject.Transform.WorldMatrix);

            materials.Add(material);
            if (material.TextureEnabled)
            {
                texturedMaterials++;
            }

            _effect.DiffuseColor = ToVector3(material.BaseColor);
            _effect.EmissiveColor = ToVector3(material.EmissiveColor);
            _effect.TextureEnabled = textureBound;
            if (material.TextureEnabled && !textureBound)
            {
                TextureFallbackCount++;
            }

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, gpuMesh.PrimitiveCount);
            }
        }

        LastSceneObjectCount = scene.Objects.Count;
        LastDistinctMaterialCount = materials.Count;
        LastTexturedMaterialCount = texturedMaterials;

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

        foreach (var texture in _textureBindings.Values)
        {
            texture.Dispose();
        }

        _meshCache.Clear();
        _uvVariantCache.Clear();
        _textureBindings.Clear();
        _rasterizerState.Dispose();
        _wrapSampler.Dispose();
        _effect.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Phase 2S world-repeat UV variant lookup. The returned mesh shares the
    /// primitive's positions/normals/colors/indices but carries UVs scaled to
    /// world repeats for this instance's transform scale and material texture
    /// scale. Keyed by (base mesh, world scale, texture scale) so identical
    /// instances share one variant; created on first use only (the scene is
    /// static), never per frame, and the GPU buffer cache deduplicates the
    /// underlying vertex/index buffers per variant.
    /// </summary>
    private MeshData GetOrCreateVariantMesh(SceneObject sceneObject, StaticMaterial material)
    {
        var key = new UvVariantKey(sceneObject.Mesh, sceneObject.Transform.Scale, material.TextureScale);
        if (_uvVariantCache.TryGetValue(key, out var variant))
        {
            return variant;
        }

        variant = MeshFactory.ApplyUvScale(sceneObject.Mesh, key.WorldScale, key.TextureScale);
        _uvVariantCache.Add(key, variant);
        return variant;
    }

    private void ApplyLighting(EnvironmentLighting lighting)
    {
        _effect.AmbientLightColor = lighting.AmbientLightColor;
        ApplyDirectional(_effect.DirectionalLight0, lighting.Directional0);
        ApplyDirectional(_effect.DirectionalLight1, lighting.Directional1);
        ApplyDirectional(_effect.DirectionalLight2, lighting.Directional2);
    }

    private static void ApplyDirectional(DirectionalLight light, DirectionalLightPolicy policy)
    {
        light.Enabled = policy.Enabled;
        light.Direction = policy.Enabled ? Vector3.Normalize(policy.Direction) : Microsoft.Xna.Framework.Vector3.Zero;
        light.DiffuseColor = policy.DiffuseColor;
        light.SpecularColor = policy.SpecularColor;
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
                new Microsoft.Xna.Framework.Vector2(vertex.TexCoord.X, vertex.TexCoord.Y)))
            .ToArray();
        var indices = mesh.Indices.ToArray();

        gpuMesh = new GpuMesh(_graphicsDevice, vertices, indices);
        _meshCache.Add(mesh, gpuMesh);
        return gpuMesh;
    }

    private static Microsoft.Xna.Framework.Vector3 ToMonoGameVector(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Vector3 ToVector3(ColorRgba color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static Matrix ToMonoGameMatrix(Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);

    /// <summary>Phase 2S UV-variant cache key: base mesh (reference identity) + the instance world scale + material texture scale.</summary>
    private readonly record struct UvVariantKey(MeshData Mesh, Numerics.Vector3 WorldScale, Numerics.Vector2 TextureScale);

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
