using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Parent/child tree for disk inspectors — unified DiskAsset overlay chains
/// (.qcow2 and .uqsnap). File sizes are read from disk when drawing.
/// </summary>
public static class SnapshotTreeGUI
{
    const float RowHeight = 22f;
    const float Indent = 18f;
    const float ConnectorWidth = 14f;
    const float KindBadgeWidth = 36f;
    const float SizeBadgeWidth = 64f;

    static GUIStyle _headerLabel;
    static GUIStyle _diskLabel;
    static GUIStyle _snapLabel;
    static GUIStyle _selectedLabel;
    static GUIStyle _sizeBadge;
    static GUIStyle _diskKindBadge;
    static GUIStyle _snapKindBadge;

    public static void Draw(DiskAsset focus)
    {
        if (focus == null)
            return;

        EnsureStyles();

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Disk tree", _headerLabel);
            EditorGUILayout.Space(2);

            DiskAsset root = focus.GetRootDisk();
            var ancestorLast = new List<bool>();
            DrawBranch(root, focus, depth: 0, isLast: true, ancestorLast);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "▣ disk   ○ snapshot   ★ this asset — click: select, right-click: Project menu.",
                EditorStyles.miniLabel);
        }
        EditorGUILayout.Space(4);
    }

    static void DrawBranch(
        DiskAsset node,
        DiskAsset focus,
        int depth,
        bool isLast,
        List<bool> ancestorLast)
    {
        ancestorLast.Add(isLast);
        bool isSnap = node.HasVmState;
        DrawNodeRow(
            kind: isSnap ? NodeKind.Snapshot : NodeKind.Disk,
            label: node.DisplayLabel,
            tooltip: AssetDatabase.GetAssetPath(node),
            sizeLabel: FormatFileSize(node.GetQcow2FilesystemPath()),
            depth: depth,
            isLast: isLast,
            ancestorLast: ancestorLast,
            selected: node == focus,
            asset: node);

        List<DiskAsset> children = DiskAsset.GetChildDisks(node);
        for (int i = 0; i < children.Count; i++)
        {
            bool childLast = i == children.Count - 1;
            DrawBranch(children[i], focus, depth + 1, childLast, ancestorLast);
        }
        ancestorLast.RemoveAt(ancestorLast.Count - 1);
    }

    enum NodeKind { Disk, Snapshot }

    static void DrawNodeRow(
        NodeKind kind,
        string label,
        string tooltip,
        string sizeLabel,
        int depth,
        bool isLast,
        List<bool> ancestorLast,
        bool selected,
        DiskAsset asset)
    {
        Rect row = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
        bool hover = !selected && row.Contains(Event.current.mousePosition);

        if (selected)
            EditorGUI.DrawRect(Pad(row, 1), new Color(0.95f, 0.72f, 0.25f, 0.35f));
        else if (hover)
            EditorGUI.DrawRect(row, kind == NodeKind.Disk
                ? new Color(0.35f, 0.55f, 0.85f, 0.12f)
                : new Color(0.45f, 0.7f, 0.45f, 0.12f));

        if (ancestorLast != null)
        {
            for (int d = 0; d < depth; d++)
            {
                float guideX = row.x + 6 + d * Indent + ConnectorWidth * 0.5f;
                if (d < ancestorLast.Count - 1 && !ancestorLast[d])
                {
                    Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.55f);
                    Handles.DrawLine(
                        new Vector3(guideX, row.y),
                        new Vector3(guideX, row.yMax));
                }
            }
        }

        float x;
        if (depth > 0)
        {
            float branchX = row.x + 6 + (depth - 1) * Indent + ConnectorWidth * 0.5f;
            float midY = row.y + row.height * 0.5f;
            Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.7f);
            Handles.DrawLine(new Vector3(branchX, row.y), new Vector3(branchX, midY));
            Handles.DrawLine(
                new Vector3(branchX, midY),
                new Vector3(branchX + ConnectorWidth - 2, midY));
            if (!isLast)
                Handles.DrawLine(new Vector3(branchX, midY), new Vector3(branchX, row.yMax));
            x = row.x + 6 + depth * Indent;
        }
        else
        {
            x = row.x + 6;
        }

        string marker;
        GUIStyle style;
        if (kind == NodeKind.Disk)
        {
            marker = "▣";
            style = _diskLabel;
        }
        else if (selected)
        {
            marker = "★";
            style = _selectedLabel;
        }
        else
        {
            marker = "○";
            style = _snapLabel;
        }

        float rightPad = KindBadgeWidth + SizeBadgeWidth + 8;
        var labelRect = new Rect(x, row.y, Mathf.Max(40, row.xMax - x - rightPad), row.height);
        EditorGUIUtility.AddCursorRect(labelRect, MouseCursor.Link);

        Event e = Event.current;
        if (e.type == EventType.ContextClick && labelRect.Contains(e.mousePosition))
        {
            ShowNodeContextMenu(asset);
            e.Use();
        }

        if (GUI.Button(labelRect, new GUIContent($"{marker}  {label}", tooltip), style))
            Select(asset);

        var sizeRect = new Rect(row.xMax - KindBadgeWidth - SizeBadgeWidth - 4, row.y, SizeBadgeWidth, row.height);
        var kindRect = new Rect(row.xMax - KindBadgeWidth - 2, row.y, KindBadgeWidth, row.height);
        GUI.Label(sizeRect, sizeLabel, _sizeBadge);
        GUI.Label(
            kindRect,
            kind == NodeKind.Disk ? "disk" : "snap",
            kind == NodeKind.Disk ? _diskKindBadge : _snapKindBadge);
    }

    public static string FormatFileSize(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath) || !File.Exists(filesystemPath))
            return "—";
        long bytes;
        try
        {
            bytes = new FileInfo(filesystemPath).Length;
        }
        catch
        {
            return "—";
        }
        return FormatBytes(bytes);
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        double gb = mb / 1024.0;
        return gb.ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }

    static void Select(Object obj)
    {
        EditorGUIUtility.PingObject(obj);
        Selection.activeObject = obj;
    }

    static void ShowNodeContextMenu(DiskAsset asset)
    {
        if (asset == null)
            return;

        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Select in Project"), false, () => RevealInProjectWindow(asset));
        menu.AddItem(new GUIContent("Select"), false, () => Select(asset));
        menu.ShowAsContext();
    }

    static void RevealInProjectWindow(DiskAsset asset)
    {
        if (asset == null)
            return;
        Selection.activeObject = asset;
        EditorUtility.FocusProjectWindow();
        EditorGUIUtility.PingObject(asset);
    }

    static Rect Pad(Rect r, float pad) =>
        new Rect(r.x + pad, r.y + pad, r.width - pad * 2, r.height - pad * 2);

    static void EnsureStyles()
    {
        if (_headerLabel != null)
            return;

        _headerLabel = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleLeft,
        };

        _diskLabel = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(2, 2, 0, 0),
        };
        _diskLabel.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.65f, 0.82f, 1f)
            : new Color(0.15f, 0.35f, 0.65f);

        _snapLabel = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(2, 2, 0, 0),
        };
        _snapLabel.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.75f, 0.92f, 0.75f)
            : new Color(0.12f, 0.42f, 0.18f);

        _selectedLabel = new GUIStyle(_snapLabel)
        {
            fontStyle = FontStyle.Bold,
        };
        _selectedLabel.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(1f, 0.92f, 0.65f)
            : new Color(0.45f, 0.28f, 0.02f);

        _sizeBadge = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Normal,
        };
        _sizeBadge.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.7f, 0.7f, 0.7f, 0.95f)
            : new Color(0.35f, 0.35f, 0.35f, 0.95f);

        _diskKindBadge = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Italic,
        };
        _diskKindBadge.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.55f, 0.72f, 0.95f, 0.85f)
            : new Color(0.2f, 0.4f, 0.7f, 0.85f);

        _snapKindBadge = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Italic,
        };
        _snapKindBadge.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.65f, 0.85f, 0.65f, 0.85f)
            : new Color(0.2f, 0.5f, 0.25f, 0.85f);
    }
}
}
