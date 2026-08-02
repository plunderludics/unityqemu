using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Base class for supplying keyboard and pointer input to a <see cref="VirtualMachine"/>.
/// Subclasses implement <see cref="PollInput"/> and enqueue with
/// <see cref="AddKeyEvent"/> / <see cref="SetMouseState"/> (or write <paramref name="frame"/> directly).
/// </summary>
public abstract class InputProvider : MonoBehaviour
{
    readonly QemuInputFrame _processFrame = new QemuInputFrame();
    QemuInputFrame _currentFrame;

    int _lastMouseX;
    int _lastMouseY;
    bool _hasLastMousePos;

    protected VirtualMachine Machine { get; private set; }

    /// <summary>
    /// Fill <paramref name="frame"/> for this provider. Prefer
    /// <see cref="AddKeyEvent"/> / <see cref="SetMouseState"/>, which write to this frame.
    /// </summary>
    protected abstract void PollInput(QemuInputFrame frame);

    /// <summary>
    /// Optional post-collect transform (e.g. key remapping in a downstream package).
    /// Default is identity.
    /// </summary>
    protected virtual void TransformFrame(QemuInputFrame frame) { }

    /// <summary>Queue a Unity key press or release for the current collect.</summary>
    public void AddKeyEvent(KeyCode key, bool down)
    {
        EnsureCurrentFrame().AddKey(key, down);
    }

    /// <summary>Queue a raw VNC/X11 keysym press or release for the current collect.</summary>
    public void AddKeyEvent(int keysym, bool down)
    {
        EnsureCurrentFrame().AddKeysym(keysym, down);
    }

    /// <summary>
    /// Set the pointer position in guest framebuffer pixels, measured from the top-left.
    /// The current button state is preserved.
    /// </summary>
    public void SetMousePosition(int x, int y)
    {
        var f = EnsureCurrentFrame();
        bool left = f.HasMouse && f.LeftButton;
        bool middle = f.HasMouse && f.MiddleButton;
        bool right = f.HasMouse && f.RightButton;
        SetMouseState(x, y, left, middle, right);
    }

    /// <summary>Set pointer buttons while preserving the current guest position.</summary>
    public void SetMouseButtons(bool leftButton, bool middleButton, bool rightButton)
    {
        var f = EnsureCurrentFrame();
        int x = f.HasMouse ? f.MouseX : _lastMouseX;
        int y = f.HasMouse ? f.MouseY : _lastMouseY;
        SetMouseState(x, y, leftButton, middleButton, rightButton);
    }

    /// <summary>
    /// Queue a complete pointer state in guest framebuffer pixels, measured from the top-left.
    /// Multiple calls in one collect are coalesced to the latest state.
    /// </summary>
    public void SetMouseState(
        int x,
        int y,
        bool leftButton = false,
        bool middleButton = false,
        bool rightButton = false)
    {
        EnsureCurrentFrame().SetMouse(x, y, leftButton, middleButton, rightButton);
        _lastMouseX = x;
        _lastMouseY = y;
        _hasLastMousePos = true;
    }

    /// <summary>
    /// Poll into <paramref name="frame"/> without transforming or sending.
    /// Used by composites to aggregate child providers.
    /// </summary>
    public void CollectInput(VirtualMachine machine, QemuInputFrame frame)
    {
        if (!isActiveAndEnabled || machine == null || machine.Texture == null || frame == null)
            return;

        Machine = machine;
        var previous = _currentFrame;
        _currentFrame = frame;
        try
        {
            PollInput(frame);
        }
        finally
        {
            _currentFrame = previous;
        }
    }

    /// <summary>
    /// Poll, run <see cref="TransformFrame"/>, and send to <paramref name="machine"/>.
    /// </summary>
    public void ProcessInput(VirtualMachine machine)
    {
        if (!isActiveAndEnabled || machine == null || machine.Texture == null)
            return;

        _processFrame.Clear();
        if (_hasLastMousePos)
        {
            _processFrame.MouseX = _lastMouseX;
            _processFrame.MouseY = _lastMouseY;
        }

        CollectInput(machine, _processFrame);
        TransformFrame(_processFrame);
        Flush(_processFrame, machine);
    }

    QemuInputFrame EnsureCurrentFrame()
    {
        if (_currentFrame == null)
            throw new System.InvalidOperationException(
                "AddKeyEvent/SetMouseState require an active CollectInput/ProcessInput.");
        return _currentFrame;
    }

    protected static void Flush(QemuInputFrame frame, VirtualMachine machine)
    {
        for (int i = 0; i < frame.Keys.Count; i++)
        {
            var kev = frame.Keys[i];
            if (kev.isRawKeysym)
                machine.SendKeyEvent(kev.keysym, kev.down);
            else
                machine.SendKeyEvent(kev.key, kev.down);
        }

        if (!frame.HasMouse)
            return;

        machine.SendMouseEvent(
            frame.MouseX,
            frame.MouseY,
            frame.LeftButton,
            frame.MiddleButton,
            frame.RightButton);
    }
}
}
