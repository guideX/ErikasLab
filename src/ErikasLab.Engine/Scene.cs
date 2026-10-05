namespace ErikasLab.Engine;

public sealed class Scene
{
    private readonly List<SceneObject> _objects = [];
    private readonly List<ModelInstance> _models = [];

    public IReadOnlyList<SceneObject> Objects => _objects;

    public IReadOnlyList<ModelInstance> Models => _models;

    public void Add(SceneObject sceneObject)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        _objects.Add(sceneObject);
    }

    public void AddModel(ModelInstance model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _models.Add(model);
    }
}

public sealed class SceneObject
{
    public SceneObject(string name, MeshData mesh, Transform transform, StaticMaterial? material = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A scene object needs a name.", nameof(name)) : name;
        Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        Transform = transform;
        Material = material ?? StaticMaterial.Default;
    }

    public string Name { get; }

    public MeshData Mesh { get; }

    public Transform Transform { get; set; }

    /// <summary>
    /// Phase 2R material description for this object. Shared immutable value
    /// (many objects reference the same category material); separate from
    /// <see cref="Mesh"/> identity so materials can be swapped without
    /// rebuilding geometry.
    /// </summary>
    public StaticMaterial Material { get; set; }
}
