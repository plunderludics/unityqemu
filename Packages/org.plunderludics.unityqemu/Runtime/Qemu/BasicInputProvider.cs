using UnityEngine;

namespace UnityQemu {

/// <summary>
/// Default input provider. It forwards Unity's legacy keyboard and mouse input to QEMU.
/// </summary>
public class BasicInputProvider : InputProvider
{
    public bool passKeyboardInput = true;
    public bool passMouseInput = true;

    protected override void PollInput()
    {
        if (Emulator == null || Emulator.Texture == null)
            return;

        if (passMouseInput)
            PollMouse();

        if (passKeyboardInput)
            PollKeyboard();
    }

    void PollMouse()
    {
        int width = Emulator.Width;
        int height = Emulator.Height;
        if (width <= 0 || height <= 0 || Screen.width <= 0 || Screen.height <= 0)
            return;

        Vector3 mousePosition = Input.mousePosition;
        int x = Mathf.Clamp((int)(mousePosition.x * width / Screen.width), 0, width - 1);
        int y = Mathf.Clamp((int)(mousePosition.y * height / Screen.height), 0, height - 1);
        y = height - 1 - y;

        SetMouseState(
            x,
            y,
            Input.GetMouseButton(0),
            Input.GetMouseButton(2),
            Input.GetMouseButton(1));
    }

    void PollKeyboard()
    {
        foreach (KeyCode key in SpecialKeyCodes)
        {
            if (Input.GetKeyDown(key))
                AddKeyEvent(key, true);
            if (Input.GetKeyUp(key))
                AddKeyEvent(key, false);
        }

        // KeyCode provides hold and modifier semantics for letters, digits, and space.
        foreach (KeyCode key in LetterDigitSpaceKeyCodes)
        {
            if (Input.GetKeyDown(key))
                AddKeyEvent(key, true);
            if (Input.GetKeyUp(key))
                AddKeyEvent(key, false);
        }

        // inputString supplies layout-accurate punctuation.
        foreach (char character in Input.inputString)
        {
            if (character <= 0x1f || character == 0x7f)
                continue;
            if (character == ' ' || char.IsLetterOrDigit(character))
                continue;
            if (IsUsShiftedDigitChar(character))
                continue;

            int keysym = QemuEmulator.CharToVncKeysym(character);
            if (keysym == 0)
                continue;

            AddKeyEvent(keysym, true);
            AddKeyEvent(keysym, false);
        }
    }

    static bool IsUsShiftedDigitChar(char character) =>
        "!@#$%^&*()".IndexOf(character) >= 0;

    static readonly KeyCode[] SpecialKeyCodes =
    {
        KeyCode.LeftShift, KeyCode.RightShift,
        KeyCode.LeftControl, KeyCode.RightControl,
        KeyCode.LeftAlt, KeyCode.RightAlt,
        KeyCode.LeftCommand, KeyCode.RightCommand,
        KeyCode.CapsLock, KeyCode.Numlock,
        KeyCode.Escape, KeyCode.Backspace, KeyCode.Delete,
        KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Tab,
        KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
        KeyCode.Insert, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
        KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6,
        KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        KeyCode.Print, KeyCode.ScrollLock, KeyCode.Pause,
        KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4,
        KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
        KeyCode.KeypadPeriod, KeyCode.KeypadDivide, KeyCode.KeypadMinus, KeyCode.KeypadPlus,
        KeyCode.KeypadMultiply,
    };

    static readonly KeyCode[] LetterDigitSpaceKeyCodes =
    {
        KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G, KeyCode.H,
        KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N, KeyCode.O, KeyCode.P,
        KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U, KeyCode.V, KeyCode.W, KeyCode.X,
        KeyCode.Y, KeyCode.Z,
        KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
        KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        KeyCode.Space,
    };
}

}
