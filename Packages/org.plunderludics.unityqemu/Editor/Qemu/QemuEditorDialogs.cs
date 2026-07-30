using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityQemu;

namespace UnityQemu.Editor {
/// <summary>
/// Shared helpers for editor modal dialogs that target one of several VirtualMachines.
/// </summary>
static class QemuEditorDialogs
{
    public static void CenterOnMainWindow(EditorWindow window, float width, float height)
    {
        Rect main = EditorGUIUtility.GetMainWindowPosition();
        window.position = new Rect(
            main.x + (main.width - width) * 0.5f,
            main.y + (main.height - height) * 0.5f,
            width, height);
    }

    /// <summary>
    /// Dropdown labels for VMs: object name, escalated to hierarchy path on name
    /// collisions, then instance id if the path still collides.
    /// </summary>
    public static string[] BuildDisambiguatedVmLabels(VirtualMachine[] machines)
    {
        int n = machines?.Length ?? 0;
        var labels = new string[n];
        for (int i = 0; i < n; i++)
        {
            VirtualMachine vm = machines[i];
            labels[i] = vm != null ? vm.name : $"VM {i + 1}";
        }

        EscalateWhereDuplicated(labels, i =>
        {
            VirtualMachine vm = machines[i];
            return vm != null ? HierarchyPath(vm.transform) : labels[i];
        });

        EscalateWhereDuplicated(labels, i =>
        {
            VirtualMachine vm = machines[i];
            return vm != null ? $"{labels[i]} ({vm.GetInstanceID()})" : labels[i];
        });

        return labels;
    }

    /// <summary>
    /// Draws a "Virtual machine" popup when there is more than one label.
    /// <paramref name="selectedIndex"/> may be -1 (none selected); the popup
    /// shows a "(Select…)" placeholder in that case.
    /// Returns true if the selection changed.
    /// </summary>
    public static bool DrawVmPopupIfMultiple(ref int selectedIndex, string[] labels)
    {
        if (labels == null || labels.Length <= 1)
            return false;

        EditorGUILayout.LabelField("Virtual machine", EditorStyles.boldLabel);

        var options = new string[labels.Length + 1];
        options[0] = "(Select…)";
        Array.Copy(labels, 0, options, 1, labels.Length);

        int displayIndex = selectedIndex < 0 ? 0 : selectedIndex + 1;
        EditorGUI.BeginChangeCheck();
        int nextDisplay = EditorGUILayout.Popup(displayIndex, options);
        if (!EditorGUI.EndChangeCheck())
            return false;

        int next = nextDisplay <= 0 ? -1 : nextDisplay - 1;
        if (next == selectedIndex)
            return false;

        selectedIndex = next;
        return true;
    }

    public static string HierarchyPath(Transform transform)
    {
        if (transform == null)
            return "(missing)";

        var parts = new List<string>(8);
        for (Transform t = transform; t != null; t = t.parent)
            parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    static void EscalateWhereDuplicated(string[] labels, Func<int, string> upgrade)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < labels.Length; i++)
        {
            string label = labels[i] ?? "";
            counts.TryGetValue(label, out int count);
            counts[label] = count + 1;
        }

        for (int i = 0; i < labels.Length; i++)
        {
            string label = labels[i] ?? "";
            if (counts.TryGetValue(label, out int count) && count > 1)
                labels[i] = upgrade(i);
        }
    }
}
}
