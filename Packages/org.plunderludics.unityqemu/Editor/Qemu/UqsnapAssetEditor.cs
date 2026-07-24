using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
[CustomEditor(typeof(UqsnapAsset))]
public class UqsnapAssetEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        var snap = (UqsnapAsset)target;
        var prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f, 1f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUI.backgroundColor = prev;
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            EditorGUILayout.LabelField("○  Snapshot", titleStyle);
            EditorGUILayout.LabelField(
                "Machine state + linked disk tip. Assign this on a VirtualMachine Snapshot slot.",
                EditorStyles.wordWrappedMiniLabel);
        }
        EditorGUILayout.Space(2);

        if (snap.screenshot != null)
        {
            float w = EditorGUIUtility.currentViewWidth - 40f;
            float aspect = (float)snap.screenshot.height / Mathf.Max(1, snap.screenshot.width);
            float h = Mathf.Clamp(w * aspect, 40f, 180f);
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(r, snap.screenshot, null, ScaleMode.ScaleToFit);
            EditorGUILayout.Space(4);
        }

        bool prevEnabled = GUI.enabled;
        GUI.enabled = true;
        if (snap.disk != null)
            SnapshotTreeGUI.Draw(snap.disk);
        GUI.enabled = prevEnabled;

        DrawDefaultInspector();
    }
}
}
