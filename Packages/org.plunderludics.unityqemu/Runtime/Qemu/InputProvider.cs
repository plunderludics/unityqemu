using System.Collections.Generic;
using UnityEngine;

namespace UnityQemu {

/// <summary>
/// Base class for supplying keyboard and pointer input to a <see cref="QemuEmulator"/>.
/// Subclasses implement <see cref="PollInput"/> and enqueue events with the public input API.
/// </summary>
public abstract class InputProvider : MonoBehaviour
{
    readonly List<KeyInputEvent> _keyEvents = new();
    MouseInputEvent _mouseEvent;
    bool _hasMouseEvent;

    protected QemuEmulator Emulator { get; private set; }

    /// <summary>
    /// Called once per emulator frame before queued input is sent.
    /// Override this to poll a custom input source and call AddKeyEvent or SetMouseState.
    /// </summary>
    protected abstract void PollInput();

    /// <summary>Queue a Unity key press or release for the next emulator input update.</summary>
    public void AddKeyEvent(KeyCode key, bool down)
    {
        _keyEvents.Add(new KeyInputEvent(key, down));
    }

    /// <summary>Queue a raw VNC/X11 keysym press or release for the next input update.</summary>
    public void AddKeyEvent(int keysym, bool down)
    {
        _keyEvents.Add(new KeyInputEvent(keysym, down));
    }

    /// <summary>
    /// Set the pointer position in guest framebuffer pixels, measured from the top-left.
    /// The current button state is preserved.
    /// </summary>
    public void SetMousePosition(int x, int y)
    {
        SetMouseState(x, y, _mouseEvent.leftButton, _mouseEvent.middleButton, _mouseEvent.rightButton);
    }

    /// <summary>Set pointer buttons while preserving the current guest position.</summary>
    public void SetMouseButtons(bool leftButton, bool middleButton, bool rightButton)
    {
        SetMouseState(_mouseEvent.x, _mouseEvent.y, leftButton, middleButton, rightButton);
    }

    /// <summary>
    /// Queue a complete pointer state in guest framebuffer pixels, measured from the top-left.
    /// Multiple calls in one frame are coalesced to the latest state.
    /// </summary>
    public void SetMouseState(
        int x,
        int y,
        bool leftButton = false,
        bool middleButton = false,
        bool rightButton = false)
    {
        _mouseEvent = new MouseInputEvent(x, y, leftButton, middleButton, rightButton);
        _hasMouseEvent = true;
    }

    internal void ProcessInput(QemuEmulator emulator)
    {
        if (!isActiveAndEnabled || emulator.Texture == null)
            return;

        Emulator = emulator;
        PollInput();

        foreach (var keyEvent in _keyEvents)
        {
            if (keyEvent.isRawKeysym)
                emulator.SendKeyEvent(keyEvent.keysym, keyEvent.down);
            else
                emulator.SendKeyEvent(keyEvent.key, keyEvent.down);
        }
        _keyEvents.Clear();

        if (_hasMouseEvent)
        {
            emulator.SendMouseEvent(
                _mouseEvent.x,
                _mouseEvent.y,
                _mouseEvent.leftButton,
                _mouseEvent.middleButton,
                _mouseEvent.rightButton);
            _hasMouseEvent = false;
        }
    }

    readonly struct KeyInputEvent
    {
        public readonly KeyCode key;
        public readonly int keysym;
        public readonly bool down;
        public readonly bool isRawKeysym;

        public KeyInputEvent(KeyCode key, bool down)
        {
            this.key = key;
            this.down = down;
            keysym = 0;
            isRawKeysym = false;
        }

        public KeyInputEvent(int keysym, bool down)
        {
            key = KeyCode.None;
            this.keysym = keysym;
            this.down = down;
            isRawKeysym = true;
        }
    }

    readonly struct MouseInputEvent
    {
        public readonly int x;
        public readonly int y;
        public readonly bool leftButton;
        public readonly bool middleButton;
        public readonly bool rightButton;

        public MouseInputEvent(
            int x,
            int y,
            bool leftButton,
            bool middleButton,
            bool rightButton)
        {
            this.x = x;
            this.y = y;
            this.leftButton = leftButton;
            this.middleButton = middleButton;
            this.rightButton = rightButton;
        }
    }
}

}
