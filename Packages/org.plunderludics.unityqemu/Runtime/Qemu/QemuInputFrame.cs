using System.Collections.Generic;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// One frame of digital guest input (keys + optional mouse buttons/position).
/// Movement-only updates are represented as <see cref="HasMouse"/> with button flags.
/// </summary>
public sealed class QemuInputFrame
{
    public struct KeyEvent
    {
        public KeyCode key;
        public int keysym;
        public bool down;
        public bool isRawKeysym;

        public static KeyEvent FromKey(KeyCode key, bool down) => new KeyEvent
        {
            key = key,
            down = down,
            isRawKeysym = false,
        };

        public static KeyEvent FromKeysym(int keysym, bool down) => new KeyEvent
        {
            key = KeyCode.None,
            keysym = keysym,
            down = down,
            isRawKeysym = true,
        };
    }

    public readonly List<KeyEvent> Keys = new List<KeyEvent>(32);

    public bool HasMouse;
    public int MouseX;
    public int MouseY;
    public bool LeftButton;
    public bool MiddleButton;
    public bool RightButton;

    public void Clear()
    {
        Keys.Clear();
        HasMouse = false;
        // Keep last coordinates so key→mouse remaps can still place a click.
    }

    public void AddKey(KeyCode key, bool down) => Keys.Add(KeyEvent.FromKey(key, down));

    public void AddKeysym(int keysym, bool down) => Keys.Add(KeyEvent.FromKeysym(keysym, down));

    public void SetMouse(int x, int y, bool left, bool middle, bool right)
    {
        HasMouse = true;
        MouseX = x;
        MouseY = y;
        LeftButton = left;
        MiddleButton = middle;
        RightButton = right;
    }

    public void MergeFrom(QemuInputFrame other)
    {
        if (other == null)
            return;
        for (int i = 0; i < other.Keys.Count; i++)
            Keys.Add(other.Keys[i]);
        if (!other.HasMouse)
            return;
        HasMouse = true;
        MouseX = other.MouseX;
        MouseY = other.MouseY;
        LeftButton = other.LeftButton;
        MiddleButton = other.MiddleButton;
        RightButton = other.RightButton;
    }
}
}
