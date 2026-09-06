namespace ErikasLab.Engine;

public sealed class Scene
{
    private readonly List<SceneObject> _objects = [];

    public IReadOnlyList<SceneObject> Objects => _objects;

    public void Add(SceneObject sceneObject)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        _objects.Add(sceneObject);
    }
}

public sealed class SceneObject
{
    public SceneObject(string name, MeshData mesh, Transform transform)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A scene object needs a name.", nameof(name)) : name;
        Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        Transform = transform;
    }

    public string Name { get; }

    public MeshData Mesh { get; }

    public Transform Transform { get; set; }
}
