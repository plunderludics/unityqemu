using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Parent/child tree for disk inspectors — DiskAsset overlay chains (.qcow2).
/// State column shows linked <see cref="UqsnapAsset"/> size when present.
/// </summary>
public static class SnapshotTreeGUI
{
    const float RowHeight = 22f;
    const float HeaderRowHeight = 16f;
    const float Indent = 18f;
    const float ConnectorWidth = 14f;
    const float SizeColWidth = 48f;
    const float SizeColsGap = 4f;

    static GUIStyle _headerLabel;
    static GUIStyle _columnHeader;
    static GUIStyle _diskLabel;
    static GUIStyle _snapLabel;
    static GUIStyle _selectedLabel;
    static GUIStyle _diskSizeBadge;
    static GUIStyle _stateSizeBadge;

    public static void Draw(DiskAsset focus) =>
        Draw(focus, snapsByDisk: null);

    public static void Draw(
        DiskAsset focus,
        Dictionary<DiskAsset, List<UqsnapAsset>> snapsByDisk)
    {
        if (focus == null)
            return;

        EnsureStyles();

        // One scan each per inspector paint — not once per tree row.
        snapsByDisk ??= UqsnapAsset.BuildIndexByDisk();
        var childrenByParent = DiskAsset.BuildChildrenIndex();

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Disk tree", _headerLabel);
            DrawColumnHeaders();

            DiskAsset root = focus.GetRootDisk();
            var ancestorLast = new List<bool>();
            DrawBranch(root, focus, depth: 0, isLast: true, ancestorLast, snapsByDisk, childrenByParent);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                "▣ disk   ★ this asset — click to select. State size is from linked .uqsnap(s).",
                EditorStyles.miniLabel);
        }
        EditorGUILayout.Space(4);
    }

    static void DrawColumnHeaders()
    {
        Rect row = GUILayoutUtility.GetRect(0, HeaderRowHeight, GUILayout.ExpandWidth(true));
        GetSizeColumnRects(row, out Rect diskRect, out Rect stateRect);

        GUI.Label(diskRect, new GUIContent("disk", "Disk changes since parent"), _columnHeader);
        GUI.Label(stateRect, new GUIContent("state", "Saved machine state (RAM/CPU)"), _columnHeader);
    }

    static void GetSizeColumnRects(Rect row, out Rect diskRect, out Rect stateRect)
    {
        float colsRight = row.xMax - 4;
        stateRect = new Rect(colsRight - SizeColWidth, row.y, SizeColWidth, row.height);
        diskRect = new Rect(stateRect.x - SizeColsGap - SizeColWidth, row.y, SizeColWidth, row.height);
    }

    static void DrawBranch(
        DiskAsset node,
        DiskAsset focus,
        int depth,
        bool isLast,
        List<bool> ancestorLast,
        Dictionary<DiskAsset, List<UqsnapAsset>> snapsByDisk,
        Dictionary<DiskAsset, List<DiskAsset>> childrenByParent)
    {
        ancestorLast.Add(isLast);
        GetNodeSizes(node, snapsByDisk, out string diskLabel, out string stateLabel);
        bool hasSnap = snapsByDisk.TryGetValue(node, out var snaps) && snaps.Count > 0;
        DrawNodeRow(
            kind: hasSnap ? NodeKind.Snapshot : NodeKind.Disk,
            label: node.DisplayLabel,
            tooltip: AssetDatabase.GetAssetPath(node),
            diskSizeLabel: diskLabel,
            stateSizeLabel: stateLabel,
            depth: depth,
            isLast: isLast,
            ancestorLast: ancestorLast,
            selected: node == focus,
            asset: node);

        if (!childrenByParent.TryGetValue(node, out List<DiskAsset> children))
            children = s_EmptyDisks;
        for (int i = 0; i < children.Count; i++)
        {
            bool childLast = i == children.Count - 1;
            DrawBranch(
                children[i], focus, depth + 1, childLast, ancestorLast,
                snapsByDisk, childrenByParent);
        }
        ancestorLast.RemoveAt(ancestorLast.Count - 1);
    }

    static readonly List<DiskAsset> s_EmptyDisks = new List<DiskAsset>();

    enum NodeKind { Disk, Snapshot }

    static void DrawNodeRow(
        NodeKind kind,
        string label,
        string tooltip,
        string diskSizeLabel,
        string stateSizeLabel,
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

        GetSizeColumnRects(row, out Rect diskRect, out Rect stateRect);
        float rightPad = row.xMax - diskRect.x + 4;
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

        GUI.Label(
            diskRect,
            new GUIContent(diskSizeLabel, "Disk changes since parent"),
            _diskSizeBadge);
        GUI.Label(
            stateRect,
            new GUIContent(stateSizeLabel, "Saved machine state (RAM/CPU)"),
            _stateSizeBadge);
    }

    static void GetNodeSizes(
        DiskAsset node,
        Dictionary<DiskAsset, List<UqsnapAsset>> snapsByDisk,
        out string diskLabel,
        out string stateLabel)
    {
        diskLabel = "—";
        stateLabel = "—";
        string imagePath = node.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            return;
        try
        {
            diskLabel = FormatBytesCompact(new FileInfo(imagePath).Length);
            long stateBytes = 0;
            bool any = false;
            if (snapsByDisk.TryGetValue(node, out List<UqsnapAsset> snaps))
            {
                foreach (UqsnapAsset snap in snaps)
                {
                    string statePath = snap.GetMachineStateFilesystemPath();
                    if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
                        continue;
                    stateBytes += new FileInfo(statePath).Length;
                    any = true;
                }
            }
            if (any)
                stateLabel = FormatBytesCompact(stateBytes);
        }
        catch
        {
            diskLabel = "—";
            stateLabel = "—";
        }
    }

    /// <summary>Short form for tight tree badges: <c>7M</c>, <c>1.2G</c>.</summary>
    static string FormatBytesCompact(long bytes)
    {
        if (bytes < 1024)
            return bytes + "B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return kb.ToString("0.#", CultureInfo.InvariantCulture) + "K";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return mb.ToString("0.#", CultureInfo.InvariantCulture) + "M";
        double gb = mb / 1024.0;
        return gb.ToString("0.##", CultureInfo.InvariantCulture) + "G";
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

        _columnHeader = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Bold,
            fontSize = 9,
        };
        _columnHeader.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.65f, 0.65f, 0.65f, 0.9f)
            : new Color(0.4f, 0.4f, 0.4f, 0.9f);

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

        // Cool blue — disk overlay size
        _diskSizeBadge = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };
        _diskSizeBadge.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(0.55f, 0.78f, 1f, 0.95f)
            : new Color(0.15f, 0.4f, 0.7f, 0.95f);

        // Warm amber — machine state size
        _stateSizeBadge = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
        };
        _stateSizeBadge.normal.textColor = EditorGUIUtility.isProSkin
            ? new Color(1f, 0.78f, 0.45f, 0.95f)
            : new Color(0.65f, 0.4f, 0.08f, 0.95f);
    }
}
}
