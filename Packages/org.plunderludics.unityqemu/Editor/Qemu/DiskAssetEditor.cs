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
        SnapshotTreeGUI.Draw(disk);
        DrawDefaultInspector();
    }

    public static void DrawKindHeader(DiskAsset disk)
    {
        bool isSnap = disk.HasVmState;
        string title = isSnap ? "Snapshot" : "Disk";
        string detail = isSnap
            ? "Durable .uqsnap — embedded savevm + launch metadata."
            : "Raw disk image — no uqsnapMetadata / savevm.";

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
