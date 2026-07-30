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

        // Shared cache — same indexes/sizes as the tree below.
        var snapsByDisk = SnapshotTreeCache.SnapsByDisk();
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
        snapsByDisk ??= SnapshotTreeCache.SnapsByDisk();
        snapsByDisk.TryGetValue(disk, out var snaps);
        int count = snaps != null ? snaps.Count : 0;
        string detail = count == 0
            ? "Disk image — boots fresh when assigned on a VirtualMachine with no Snapshot."
            : count == 1
                ? $"Disk tip for snapshot '{snaps[0].DisplayLabel}'."
                : $"Disk tip linked from {count} snapshots.";

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.62f, 0.62f, 0.64f, 1f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUI.backgroundColor = prev;
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            titleStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.78f, 0.78f, 0.8f)
                : new Color(0.28f, 0.28f, 0.3f);
            EditorGUILayout.LabelField("▣  Disk", titleStyle);
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.Space(2);
    }
}
}
