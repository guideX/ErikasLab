using System.Numerics;

namespace ErikasLab.Engine;

public sealed class CameraController
{
    public float MovementSpeed { get; init; } = 8f;

    public float KeyboardLookSpeed { get; init; } = 1.7f;

    public float MouseLookSensitivity { get; init; } = 0.0025f;

    public void Update(CameraState camera, InputState input, FrameTime frameTime)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var deltaSeconds = Math.Max(0, frameTime.DeltaSeconds);
        var movement = Vector3.Zero;
        var forward = camera.Forward with { Y = 0 };
        var right = camera.Right with { Y = 0 };

        if (forward.LengthSquared() > 0)
        {
            forward = Vector3.Normalize(forward);
        }

        if (right.LengthSquared() > 0)
        {
            right = Vector3.Normalize(right);
        }

        if (input.MoveForward)
        {
            movement += forward;
        }

        if (input.MoveBackward)
        {
            movement -= forward;
        }

        if (input.StrafeRight)
        {
            movement += right;
        }

        if (input.StrafeLeft)
        {
            movement -= right;
        }

        if (movement.LengthSquared() > 0)
        {
            camera.Position += Vector3.Normalize(movement) * MovementSpeed * (float)deltaSeconds;
        }

        var keyboardYaw = (input.LookRight ? 1 : 0) - (input.LookLeft ? 1 : 0);
        var keyboardPitch = (input.LookUp ? 1 : 0) - (input.LookDown ? 1 : 0);
        var yawDelta = input.MouseDelta.X * MouseLookSensitivity + keyboardYaw * KeyboardLookSpeed * (float)deltaSeconds;
        var pitchDelta = -input.MouseDelta.Y * MouseLookSensitivity + keyboardPitch * KeyboardLookSpeed * (float)deltaSeconds;
        camera.Rotate(yawDelta, pitchDelta);
    }
}
