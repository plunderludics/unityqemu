using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
[CustomEditor(typeof(DiskAsset))]
public class DiskAssetEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        var disk = (DiskAsset)target;

        // One snap scan shared by header + tree.
        var snapsByDisk = UqsnapAsset.BuildIndexByDisk();
        DrawKindHeader(disk, snapsByDisk);

        bool prevEnabled = GUI.enabled;
        GUI.enabled = true;
        SnapshotTreeGUI.Draw(disk, snapsByDisk);
        GUI.enabled = prevEnabled;

        DrawDefaultInspector();
    }

    public static void DrawKindHeader(
        DiskAsset disk,
        Dictionary<DiskAsset, List<UqsnapAsset>> snapsByDisk = null)
    {
        snapsByDisk ??= UqsnapAsset.BuildIndexByDisk();
        snapsByDisk.TryGetValue(disk, out var snaps);
        int count = snaps != null ? snaps.Count : 0;
        string detail = count == 0
            ? "Disk image — boots fresh when assigned on a VirtualMachine with no Snapshot."
            : count == 1
                ? $"Disk tip for snapshot '{snaps[0].DisplayLabel}'."
                : $"Disk tip linked from {count} snapshots.";

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.7f, 0.95f, 1f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUI.backgroundColor = prev;
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            EditorGUILayout.LabelField("▣  Disk", titleStyle);
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.Space(2);
    }
}
}
