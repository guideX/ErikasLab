using ErikasLab.Engine;
using ErikasLab.Game;
using Xunit;

namespace ErikasLab.Engine.Tests;

/// <summary>
/// Phase 2D validation selector: 1/2/3 switch idle/walk/run with a
/// deterministic loop reset. Hard switch only; no blending/state machine.
/// </summary>
public sealed class ClipSelectionTests
{
    private static InputState IdleInput(double total = 10.0) => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        SelectIdle: true, SelectWalk: false, SelectRun: false);

    private static InputState WalkInput(double total = 10.0) => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        SelectIdle: false, SelectWalk: true, SelectRun: false);

    private static InputState RunInput(double total = 10.0) => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default,
        SelectIdle: false, SelectWalk: false, SelectRun: true);

    private static InputState NoSelectInput() => new(
        MoveForward: false, MoveBackward: false, StrafeLeft: false, StrafeRight: false,
        LookLeft: false, LookRight: false, LookUp: false, LookDown: false,
        ExitRequested: false, MouseDelta: default);

    [Fact]
    public void DefaultsToIdle()
    {
        var session = new GameSession();
        Assert.Equal(ErikaFigure.IdleClipName, session.ActiveClipName);
        Assert.Equal(0.0, session.ClipStartSeconds);
    }

    [Fact]
    public void WalkKeySwitchesAndResetsClock()
    {
        var session = new GameSession();
        session.Update(new FrameTime(10.0, 0.016), WalkInput());
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(10.0, session.ClipStartSeconds);
    }

    [Fact]
    public void RunKeySwitchesAndResetsClock()
    {
        var session = new GameSession();
        session.Update(new FrameTime(7.5, 0.016), RunInput());
        Assert.Equal(ErikaFigure.RunClipName, session.ActiveClipName);
        Assert.Equal(7.5, session.ClipStartSeconds);
    }

    [Fact]
    public void HoldingKeyDoesNotContinuouslyReset()
    {
        var session = new GameSession();
        session.Update(new FrameTime(10.0, 0.016), WalkInput());
        session.Update(new FrameTime(10.5, 0.016), WalkInput());
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(10.0, session.ClipStartSeconds);
    }

    [Fact]
    public void NoInputKeepsActiveClip()
    {
        var session = new GameSession();
        session.Update(new FrameTime(10.0, 0.016), WalkInput());
        session.Update(new FrameTime(11.0, 0.016), NoSelectInput());
        Assert.Equal(ErikaFigure.WalkClipName, session.ActiveClipName);
        Assert.Equal(10.0, session.ClipStartSeconds);
    }

    [Fact]
    public void ClipAssetResolutionCoversAllThree()
    {
        Assert.Equal(ErikaFigure.IdleClipAssetId, ErikaFigure.ClipAssetFor(ErikaFigure.IdleClipName));
        Assert.Equal(ErikaFigure.WalkClipAssetId, ErikaFigure.ClipAssetFor(ErikaFigure.WalkClipName));
        Assert.Equal(ErikaFigure.RunClipAssetId, ErikaFigure.ClipAssetFor(ErikaFigure.RunClipName));
    }
}
