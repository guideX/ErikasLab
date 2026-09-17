using System.Runtime.InteropServices;
using ErikasLab.Engine;
using ErikasLab.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ErikasLab.Platform.MonoGame;

internal sealed class ErikasLabHost : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _graphics;
    private GameSession? _gameSession;
    private MonoGameInputSource? _inputSource;
    private MonoGameRenderer? _renderer;
    private ModelLibrary? _modelLibrary;
    private AnimatedModelRenderer? _modelRenderer;

    public ErikasLabHost()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.HiDef,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = false;
        Window.Title = "Erika's Lab - Phase 1";
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += OnClientSizeChanged;
    }

    protected override void Initialize()
    {
        Console.WriteLine("Erika's Lab | Phase 1 | initializing");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _modelLibrary = new ModelLibrary(Content);
        _modelRenderer = new AnimatedModelRenderer(_modelLibrary, GraphicsDevice);
        _renderer = new MonoGameRenderer(GraphicsDevice, _modelRenderer);
        _inputSource = new MonoGameInputSource(Window);
        _gameSession = new GameSession();
        _gameSession.Resize(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

        var rendererInfo = _renderer.Info;
        Console.WriteLine($"Graphics adapter: {rendererInfo.DeviceName}");
        Console.WriteLine($"Backbuffer: {rendererInfo.BackbufferWidth}x{rendererInfo.BackbufferHeight}");
        Console.WriteLine($"Renderer backend: {rendererInfo.BackendName}");
        Console.WriteLine("Engine initialization: successful");
        Console.WriteLine($"Scene creation: successful ({_gameSession.World.Objects.Count} objects, {_gameSession.World.Models.Count} models)");

        try
        {
            foreach (var line in _modelRenderer.Diagnostics(_gameSession.World))
            {
                Console.WriteLine(line);
            }
        }
        catch (ErikaContentException ex)
        {
            Console.WriteLine($"Erika content failed: {ex.Message}");
            throw;
        }

        Console.WriteLine("Controls: W/A/S/D move, mouse or arrow keys look, Escape exits");
    }

    protected override void Update(GameTime gameTime)
    {
        if (_gameSession is null || _inputSource is null)
        {
            return;
        }

        var frameTime = new FrameTime(
            gameTime.TotalGameTime.TotalSeconds,
            gameTime.ElapsedGameTime.TotalSeconds);
        _gameSession.Update(frameTime, _inputSource.ReadState());
        _modelRenderer?.Update(frameTime);

        if (_gameSession.ExitRequested)
        {
            Exit();
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_gameSession is not null && _renderer is not null)
        {
            _gameSession.Render(_renderer);
        }

        base.Draw(gameTime);

        // TEMPORARY Phase 2C verification hook (revert before commit):
        // ERIKASLAB_SHOTS="<path>:<frames>,..." saves screenshots after the
        // given rendered-frame counts and exits after the last one. Split on
        // the last colon of each entry so Windows drive letters survive.
        var shots = Environment.GetEnvironmentVariable("ERIKASLAB_SHOTS");
        if (shots is not null)
        {
            _shotFrames++;
            foreach (var entry in shots.Split(','))
            {
                var separator = entry.LastIndexOf(':');
                if (separator > 0
                    && int.TryParse(entry[(separator + 1)..], out var frames)
                    && _shotFrames == frames)
                {
                    SaveShot(entry[..separator]);
                }
            }

            if (_shotFrames >= shots.Split(',').Select(entry =>
                int.TryParse(entry[(entry.LastIndexOf(':') + 1)..], out var frames) ? frames : 0).Max())
            {
                Exit();
            }
        }
    }

    // TEMPORARY Phase 2C verification hook (revert before commit).
    private int _shotFrames;

    private void SaveShot(string path)
    {
        var width = GraphicsDevice.Viewport.Width;
        var height = GraphicsDevice.Viewport.Height;
        var pixels = new int[width * height];
        GraphicsDevice.GetBackBufferData(pixels);
        using var texture = new Texture2D(GraphicsDevice, width, height);
        texture.SetData(pixels);
        using var stream = File.OpenWrite(path);
        texture.SaveAsPng(stream, width, height);
        Console.WriteLine($"Screenshot saved: {path}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer?.Dispose();
            _modelLibrary?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_renderer is null || _gameSession is null)
        {
            return;
        }

        var viewport = GraphicsDevice.Viewport;
        _renderer.Resize(viewport.Width, viewport.Height);
        _gameSession.Resize(viewport.Width, viewport.Height);
    }
}
