using System;
using UnityEditor;
using UnityEngine;
using UnityQemu;

namespace UnityQemu.Editor {
/// <summary>
/// Modal picker for peripheral hotplug actions (CD / floppy / vvfat).
/// Opened by Ctrl+Shift+Alt+A. When multiple VMs are running, pick one at the top
/// (nothing selected until the user chooses). Actions run on the chosen
/// <see cref="PeripheralsUI"/> after the dialog closes.
/// </summary>
class PeripheralsDialog : EditorWindow
{
    public enum Choice
    {
        Cancelled,
        InsertCd,
        EjectCd,
        InsertFloppy,
        EjectFloppy,
        AttachVvfat,
        DetachVvfat,
    }

    public readonly struct Result
    {
        public Result(Choice choice, PeripheralsUI target, bool alsoAddToLaunchConfig)
        {
            Choice = choice;
            Target = target;
            AlsoAddToLaunchConfig = alsoAddToLaunchConfig;
        }

        public Choice Choice { get; }
        public PeripheralsUI Target { get; }
        public bool AlsoAddToLaunchConfig { get; }
    }

    Choice _choice = Choice.Cancelled;
    PeripheralsUI[] _targets = Array.Empty<PeripheralsUI>();
    string[] _vmLabels = Array.Empty<string>();
    int _selectedIndex = -1;
    bool _alsoAddToLaunchConfig = true;
    Vector2 _scroll;

    bool HasSelection =>
        _selectedIndex >= 0 && _selectedIndex < _targets.Length;

    public static Result Prompt(PeripheralsUI[] targets)
    {
        if (targets == null || targets.Length == 0)
            return new Result(Choice.Cancelled, null, true);

        var window = CreateInstance<PeripheralsDialog>();
        window.titleContent = new GUIContent("Peripherals");
        window._targets = targets;
        window._vmLabels = BuildVmLabels(targets);
        // Single target: auto-select. Multiple: force an explicit choice.
        window._selectedIndex = targets.Length == 1 ? 0 : -1;
        if (window.HasSelection)
            window.LoadOptionsFrom(targets[0]);

        // Tall enough for VM picker + options + three action rows + cancel.
        float height = targets.Length > 1 ? 340f : 310f;
        window.minSize = new Vector2(400, 240f);
        window.maxSize = new Vector2(720, 720f);
        QemuEditorDialogs.CenterOnMainWindow(window, 460, height);
        window.ShowModalUtility();

        PeripheralsUI target = window._choice == Choice.Cancelled || !window.HasSelection
            ? null
            : window._targets[window._selectedIndex];
        return new Result(window._choice, target, window._alsoAddToLaunchConfig);
    }

    static string[] BuildVmLabels(PeripheralsUI[] targets)
    {
        var machines = new VirtualMachine[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            machines[i] = targets[i] != null ? targets[i].virtualMachine : null;
        return QemuEditorDialogs.BuildDisambiguatedVmLabels(machines);
    }

    void LoadOptionsFrom(PeripheralsUI ui)
    {
        if (ui == null)
            return;
        _alsoAddToLaunchConfig = ui.alsoAddToLaunchConfig;
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

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        try
        {
            EditorGUILayout.Space(10);

            if (QemuEditorDialogs.DrawVmPopupIfMultiple(ref _selectedIndex, _vmLabels))
            {
                if (HasSelection)
                    LoadOptionsFrom(_targets[_selectedIndex]);
            }

            if (_targets.Length > 1)
                EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(!HasSelection))
            {
                EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
                _alsoAddToLaunchConfig = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Also add to launch config",
                        "When inserting a project CdRomAsset / FloppyAsset, also update EffectiveLaunchConfig."),
                    _alsoAddToLaunchConfig);

                EditorGUILayout.Space(12);
                EditorGUILayout.LabelField("CD-ROM", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ActionButton("Insert CD", Choice.InsertCd))
                        return;
                    if (ActionButton("Eject CD", Choice.EjectCd))
                        return;
                }

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Floppy", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ActionButton("Insert floppy", Choice.InsertFloppy))
                        return;
                    if (ActionButton("Eject floppy", Choice.EjectFloppy))
                        return;
                }

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("USB vvfat", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ActionButton("Attach vvfat", Choice.AttachVvfat))
                        return;
                    if (ActionButton("Detach vvfat", Choice.DetachVvfat))
                        return;
                }
            }

            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                {
                    _choice = Choice.Cancelled;
                    Close();
                }
            }

            EditorGUILayout.Space(8);
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    bool ActionButton(string label, Choice choice)
    {
        if (!GUILayout.Button(label, GUILayout.Height(26)))
            return false;
        if (!HasSelection)
            return false;
        _choice = choice;
        Close();
        return true;
    }
}
}
