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
