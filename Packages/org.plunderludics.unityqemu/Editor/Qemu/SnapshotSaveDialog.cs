using System;
using UnityEditor;
using UnityEngine;
using UnityQemu;

namespace UnityQemu.Editor {
/// <summary>
/// Modal picker for durable snapshot save: child / sibling / overwrite.
/// Opened by Ctrl+Shift+Alt+C. When multiple VMs are running, pick one at the top
/// (nothing selected until the user chooses). Also exposes include-machine-state /
/// screenshot / compression toggles for this save.
/// </summary>
class SnapshotSaveDialog : EditorWindow
{
    public enum Choice
    {
        Cancelled,
        SaveChild,
        SaveSibling,
        Overwrite,
    }

    public readonly struct Result
    {
        public Result(
            Choice choice,
            VirtualMachine target,
            bool includeMachineState,
            bool captureScreenshot,
            bool compressMachineState)
        {
            Choice = choice;
            Target = target;
            IncludeMachineState = includeMachineState;
            CaptureScreenshot = captureScreenshot;
            CompressMachineState = compressMachineState;
        }

        public Choice Choice { get; }
        public VirtualMachine Target { get; }
        public bool IncludeMachineState { get; }
        public bool CaptureScreenshot { get; }
        public bool CompressMachineState { get; }
    }

    Choice _choice = Choice.Cancelled;
    VirtualMachine[] _targets = Array.Empty<VirtualMachine>();
    string[] _vmLabels = Array.Empty<string>();
    int _selectedIndex = -1;
    BootableAsset _current;
    string _detail;
    bool _frozen;
    bool _canChild;
    bool _canSibling;
    bool _canOverwrite;
    bool _includeMachineState = true;
    bool _captureScreenshot = true;
    bool _compressMachineState = true;

    bool HasSelection =>
        _selectedIndex >= 0 && _selectedIndex < _targets.Length;

    public static Result Prompt(VirtualMachine[] targets)
    {
        if (targets == null || targets.Length == 0)
            return new Result(Choice.Cancelled, null, true, true, true);

        var window = CreateInstance<SnapshotSaveDialog>();
        window.titleContent = new GUIContent("Save Snapshot");
        window._targets = targets;
        window._vmLabels = QemuEditorDialogs.BuildDisambiguatedVmLabels(targets);
        // Single target: auto-select. Multiple: force an explicit choice.
        window._selectedIndex = targets.Length == 1 ? 0 : -1;
        if (window.HasSelection)
        {
            window.LoadOptionsFrom(window._targets[0]);
            window.RefreshFromTarget(window._targets[0]);
        }
        else
            window.RefreshFromTarget(null);

        float height = 248f;
        if (targets.Length > 1)
            height += 28f;
        if (window._current is UqsnapAsset snap && snap.screenshot != null)
            height = Mathf.Max(height, targets.Length > 1 ? 396f : 368f);

        window.minSize = new Vector2(400, height);
        window.maxSize = new Vector2(560, height + 40f);
        QemuEditorDialogs.CenterOnMainWindow(window, 440, height);
        window.ShowModalUtility();
        VirtualMachine target = window._choice == Choice.Cancelled || !window.HasSelection
            ? null
            : window._targets[window._selectedIndex];
        return new Result(
            window._choice,
            target,
            window._includeMachineState,
            window._captureScreenshot,
            window._compressMachineState);
    }

    void LoadOptionsFrom(VirtualMachine vm)
    {
        if (vm == null)
            return;
        SnapshotUI ui = vm.GetComponent<SnapshotUI>();
        if (ui == null)
            return;
        _includeMachineState = ui.includeMachineState;
        _captureScreenshot = ui.captureScreenshot;
        _compressMachineState = ui.compressMachineState;
    }

    void RefreshFromTarget(VirtualMachine vm)
    {
        if (vm == null)
        {
            _current = null;
            _detail = "";
            _frozen = false;
            _canChild = false;
            _canSibling = false;
            _canOverwrite = false;
            return;
        }

        _current = vm.sessionCurrent;
        DiskAsset tip = vm.SessionDiskTip;
        _frozen = tip != null && DiskAsset.HasChildDisks(tip);
        _canChild = vm.CanSaveChildDurable;
        _canSibling = vm.CanSaveSiblingDurable;
        _canOverwrite = vm.CanOverwriteDurable;

        string detail = "";
        if (tip != null)
        {
            detail = tip.backingDisk != null
                ? $"Disk tip '{tip.DisplayLabel}' → parent '{tip.backingDisk.DisplayLabel}'"
                : $"Disk tip '{tip.DisplayLabel}' (base)";
        }

        if (_targets.Length > 1 && HasSelection)
        {
            string vmLabel = _vmLabels[_selectedIndex];
            detail = string.IsNullOrEmpty(detail)
                ? vmLabel
                : $"{vmLabel} — {detail}";
        }
        _detail = detail;
    }

    void OnGUI()
    {
        Event e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            _choice = Choice.Cancelled;
            e.Use();
            Close();
            return;
        }

        EditorGUILayout.Space(10);

        if (QemuEditorDialogs.DrawVmPopupIfMultiple(ref _selectedIndex, _vmLabels))
        {
            if (HasSelection)
            {
                LoadOptionsFrom(_targets[_selectedIndex]);
                RefreshFromTarget(_targets[_selectedIndex]);
            }
            else
                RefreshFromTarget(null);
        }

        if (_targets.Length > 1)
            EditorGUILayout.Space(8);

        using (new EditorGUI.DisabledScope(!HasSelection))
        {
            string currentLabel = _frozen ? "Current ❄" : "Current";
            EditorGUILayout.LabelField(currentLabel, EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(_current, typeof(BootableAsset), false);
            }

            if (_current is UqsnapAsset snap && snap.screenshot != null)
            {
                EditorGUILayout.Space(4);
                float w = EditorGUIUtility.currentViewWidth - 24f;
                float aspect = (float)snap.screenshot.height / Mathf.Max(1, snap.screenshot.width);
                float h = Mathf.Clamp(w * aspect, 48f, 140f);
                Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(true));
                EditorGUI.DrawPreviewTexture(r, snap.screenshot, null, ScaleMode.ScaleToFit);
            }

            if (!string.IsNullOrEmpty(_detail))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(_detail, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            _includeMachineState = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Include machine state (.uqsnap)",
                    "Off = save a cold-bootable DiskAsset tip only."),
                _includeMachineState);
            using (new EditorGUI.DisabledScope(!_includeMachineState))
            {
                _captureScreenshot = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Capture screenshot",
                        "Write a sibling .png from the live VNC frame."),
                    _captureScreenshot);
                _compressMachineState = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Compress machine state",
                        "Gzip the .uqsnap (smaller; slightly slower save/load)."),
                    _compressMachineState);
            }

            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = HasSelection && _canChild;
                if (GUILayout.Button("Save child", GUILayout.Height(28)))
                {
                    _choice = Choice.SaveChild;
                    Close();
                    return;
                }

                GUI.enabled = HasSelection && _canSibling;
                if (GUILayout.Button("Save sibling", GUILayout.Height(28)))
                {
                    _choice = Choice.SaveSibling;
                    Close();
                    return;
                }

                GUI.enabled = HasSelection && _canOverwrite;
                Color prevBg = GUI.backgroundColor;
                GUI.backgroundColor = _frozen
                    ? new Color(1f, 0.55f, 0.55f)
                    : new Color(1f, 0.92f, 0.55f);
                if (GUILayout.Button("Overwrite", GUILayout.Height(28)))
                {
                    GUI.backgroundColor = prevBg;
                    _choice = Choice.Overwrite;
                    Close();
                    return;
                }
                GUI.backgroundColor = prevBg;

                GUI.enabled = true;
            }
        }

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _choice = Choice.Cancelled;
                Close();
            }
        }
    }
}
}
