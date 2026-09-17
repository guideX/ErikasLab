using ErikasLab.Engine;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace ErikasLab.Platform.MonoGame;

/// <summary>
/// Loads built content models by portable <see cref="ModelAssetId"/> and caches
/// them. Load failures are wrapped in <see cref="ErikaContentException"/> with
/// actionable guidance instead of a bare ContentLoadException.
/// </summary>
internal sealed class ModelLibrary : IDisposable
{
    private readonly ContentManager _content;
    private readonly Dictionary<string, Model> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkeletalAnimation> _clips = new(StringComparer.Ordinal);
    private bool _disposed;

    public ModelLibrary(ContentManager content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public Model Get(ModelAssetId asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_models.TryGetValue(asset.Name, out var model))
        {
            return model;
        }

        try
        {
            model = _content.Load<Model>(asset.Name);
        }
        catch (Exception ex) when (ex is ContentLoadException
            or FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw NewMissingAssetException(asset, ex);
        }

        _models.Add(asset.Name, model);
        return model;
    }

    /// <summary>
    /// Loads a project-owned animation sidecar (<c>.bin</c>, see
    /// <c>ErikaModelProcessor</c>) by asset id and decodes it with the
    /// portable codec. No ContentManager involvement.
    /// </summary>
    public SkeletalAnimation GetClip(ModelAssetId asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_clips.TryGetValue(asset.Name, out var animation))
        {
            return animation;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Content", asset.Name + ".bin");
        try
        {
            using var stream = File.OpenRead(path);
            var (skeleton, clip) = ErikaClipCodec.Read(stream);
            animation = new SkeletalAnimation(skeleton, clip);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidDataException)
        {
            throw NewMissingAssetException(asset, ex);
        }

        _clips.Add(asset.Name, animation);
        return animation;
    }

    public void Dispose()
    {
        _models.Clear();
        _clips.Clear();
        _disposed = true;
    }

    private static ErikaContentException NewMissingAssetException(ModelAssetId asset, Exception inner) =>
        new(
            $"Could not load model asset '{asset.Name}' " +
            $"for {ErikasLab.Game.ErikaFigure.SourceFile}. " +
            $"Build the content first: restore the ignored local erika/ source directory, " +
            $"run 'python tools/extract_erika_textures.py' once per machine, then " +
            $"'dotnet build ErikasLab.sln'. See README.md ('Erika content').",
            inner);
}

/// <summary>Model content is missing or unreadable; message tells how to restore it.</summary>
internal sealed class ErikaContentException(string message, Exception innerException)
    : Exception(message, innerException);
