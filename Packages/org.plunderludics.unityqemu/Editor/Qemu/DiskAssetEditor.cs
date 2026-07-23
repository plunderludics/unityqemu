using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
[CustomEditor(typeof(DiskAsset))]
public class DiskAssetEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        var disk = (DiskAsset)target;

        DrawKindHeader(disk);

        // Importer-backed DiskAssets are drawn with GUI.enabled=false (read-only asset).
        // Re-enable for the tree so click / context-menu still work.
        bool prevEnabled = GUI.enabled;
        GUI.enabled = true;
        SnapshotTreeGUI.Draw(disk);
        GUI.enabled = prevEnabled;

        DrawDefaultInspector();
    }

    public static void DrawKindHeader(DiskAsset disk)
    {
        bool isSnap = disk.HasVmState;
        string title = isSnap ? "Snapshot" : "Disk";
        string detail;
        if (!isSnap)
            detail = "Disk image — boots fresh, no saved machine state.";
        else if (disk.HasVmStateSidecar)
            detail = "Saved machine state plus disk changes since its parent.";
        else
            detail = "Older snapshot format. Save it again, or use " +
                     "Tools → UnityQemu → Convert Legacy Snapshots, to upgrade.";

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = isSnap
            ? new Color(0.55f, 0.85f, 0.55f, 1f)
            : new Color(0.55f, 0.7f, 0.95f, 1f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUI.backgroundColor = prev;
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            EditorGUILayout.LabelField(isSnap ? "○  " + title : "▣  " + title, titleStyle);
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.Space(2);
    }
}
}
