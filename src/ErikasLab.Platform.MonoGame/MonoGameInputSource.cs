using ErikasLab.Engine;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Numerics = System.Numerics;

namespace ErikasLab.Platform.MonoGame;

internal sealed class MonoGameInputSource : IInputSource
{
    private readonly GameWindow _window;
    private bool _centerMouseOnNextRead = true;

    public MonoGameInputSource(GameWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public InputState ReadState()
    {
        var keyboard = Keyboard.GetState();

        // TEMPORARY Phase 2C verification hook (revert before commit):
        // headless runs get nondeterministic first-frame mouse deltas.
        if (Environment.GetEnvironmentVariable("ERIKASLAB_NOMOUSE") == "1")
        {
            return new InputState(
                MoveForward: keyboard.IsKeyDown(Keys.W),
                MoveBackward: keyboard.IsKeyDown(Keys.S),
                StrafeLeft: keyboard.IsKeyDown(Keys.A),
                StrafeRight: keyboard.IsKeyDown(Keys.D),
                LookLeft: keyboard.IsKeyDown(Keys.Left),
                LookRight: keyboard.IsKeyDown(Keys.Right),
                LookUp: keyboard.IsKeyDown(Keys.Up),
                LookDown: keyboard.IsKeyDown(Keys.Down),
                ExitRequested: keyboard.IsKeyDown(Keys.Escape),
                MouseDelta: Numerics.Vector2.Zero);
        }

        var bounds = _window.ClientBounds;
        var centerX = Math.Max(1, bounds.Width) / 2;
        var centerY = Math.Max(1, bounds.Height) / 2;
        var mouse = Mouse.GetState();
        var mouseDelta = _centerMouseOnNextRead
            ? Numerics.Vector2.Zero
            : new Numerics.Vector2(mouse.X - centerX, mouse.Y - centerY);

        Mouse.SetPosition(centerX, centerY);
        _centerMouseOnNextRead = false;

        return new InputState(
            MoveForward: keyboard.IsKeyDown(Keys.W),
            MoveBackward: keyboard.IsKeyDown(Keys.S),
            StrafeLeft: keyboard.IsKeyDown(Keys.A),
            StrafeRight: keyboard.IsKeyDown(Keys.D),
            LookLeft: keyboard.IsKeyDown(Keys.Left),
            LookRight: keyboard.IsKeyDown(Keys.Right),
            LookUp: keyboard.IsKeyDown(Keys.Up),
            LookDown: keyboard.IsKeyDown(Keys.Down),
            ExitRequested: keyboard.IsKeyDown(Keys.Escape),
            MouseDelta: mouseDelta);
    }
}
