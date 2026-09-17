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
    private readonly Dictionary<string, Model> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public ModelLibrary(ContentManager content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public Model Get(ModelAssetId asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cache.TryGetValue(asset.Name, out var model))
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
            throw new ErikaContentException(
                $"Could not load model asset '{asset.Name}' " +
                $"for {ErikasLab.Game.ErikaFigure.SourceFile}. " +
                $"Build the content first: restore the ignored local erika/ source directory, " +
                $"run 'python tools/extract_erika_textures.py' once per machine, then " +
                $"'dotnet build ErikasLab.sln'. See README.md ('Erika content').",
                ex);
        }

        _cache.Add(asset.Name, model);
        return model;
    }

    public void Dispose()
    {
        _cache.Clear();
        _disposed = true;
    }
}

/// <summary>Model content is missing or unreadable; message tells how to restore it.</summary>
internal sealed class ErikaContentException(string message, Exception innerException)
    : Exception(message, innerException);
