#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityQemu {
/// <summary>Tiny modal text prompt for snapshot names (shared by SnapshotUI / DurableSnapshotUI).</summary>
class SnapshotNameDialog : EditorWindow
{
    string _value;
    bool _accepted;
    bool _focusPending = true;

    public static string Prompt(string defaultName, string title = "Save Snapshot")
    {
        var window = CreateInstance<SnapshotNameDialog>();
        window.titleContent = new GUIContent(title);
        window._value = defaultName ?? "snap1";
        window.minSize = new Vector2(320, 80);
        window.maxSize = new Vector2(480, 80);
        CenterOnMainWindow(window, 360, 80);
        window.ShowModalUtility();
        return window._accepted ? window._value : null;
    }

    static void CenterOnMainWindow(EditorWindow window, float width, float height)
    {
        Rect main = EditorGUIUtility.GetMainWindowPosition();
        window.position = new Rect(
            main.x + (main.width - width) * 0.5f,
            main.y + (main.height - height) * 0.5f,
            width, height);
    }

    void OnGUI()
    {
        // Capture Return/Escape before the TextField draws — a single-line IMGUI
        // TextField consumes the Enter KeyDown while focused, so checking after it
        // (as the buttons do) would never see it.
        bool submit = false;
        bool cancel = false;
        Event e = Event.current;
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                submit = true;
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                cancel = true;
                e.Use();
            }
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Snapshot name");
        GUI.SetNextControlName("SnapshotNameField");
        _value = EditorGUILayout.TextField(_value);
        if (_focusPending)
        {
            EditorGUI.FocusTextInControl("SnapshotNameField");
            _focusPending = false;
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            cancel |= GUILayout.Button("Cancel");
            GUILayout.FlexibleSpace();
            submit |= GUILayout.Button("Save");
        }

        if (cancel)
        {
            _accepted = false;
            Close();
            return;
        }

        // Evaluate submit after the field draws so _value reflects the latest text.
        if (submit && !string.IsNullOrWhiteSpace(_value))
        {
            _accepted = true;
            Close();
        }
    }
}
}
#endif
