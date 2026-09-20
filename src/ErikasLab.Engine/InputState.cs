using System.Numerics;

namespace ErikasLab.Engine;

public readonly record struct InputState(
    bool MoveForward,
    bool MoveBackward,
    bool StrafeLeft,
    bool StrafeRight,
    bool LookLeft,
    bool LookRight,
    bool LookUp,
    bool LookDown,
    bool ExitRequested,
    Vector2 MouseDelta,
    bool SelectIdle = false,
    bool SelectWalk = false,
    bool SelectRun = false);

public interface IInputSource
{
    InputState ReadState();
}
