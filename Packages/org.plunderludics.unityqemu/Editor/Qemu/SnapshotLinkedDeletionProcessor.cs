using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// When a <see cref="DiskAsset"/> or <see cref="UqsnapAsset"/> is deleted, offer to also
/// remove assets linked by serialized refs (<see cref="UqsnapAsset.disk"/>,
/// <see cref="UqsnapAsset.screenshot"/>).
/// </summary>
/// <remarks>
/// Unity only discovers <c>OnWillDeleteAsset</c> (singular). Linked paths are captured
/// while the assets still exist; the confirm + cascade runs on <c>delayCall</c> after
/// Unity finishes the original delete batch.
/// </remarks>
public class SnapshotLinkedDeletionProcessor : AssetModificationProcessor
{
    static bool _insideCascade;
    static bool _batchPending;
    static RemoveAssetOptions _batchOptions;
    static readonly HashSet<string> _batchDeleting =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _batchExtras =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static int _batchTriggerCount;

    static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
    {
        if (_insideCascade)
            return AssetDeleteResult.DidNotDelete;

        string path = Normalize(assetPath);
        if (string.IsNullOrEmpty(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            return AssetDeleteResult.DidNotDelete;

        if (!_batchPending)
        {
            _batchPending = true;
            _batchOptions = options;
            _batchDeleting.Clear();
            _batchExtras.Clear();
            _batchTriggerCount = 0;
            EditorApplication.delayCall += ProcessBatch;
        }

        _batchDeleting.Add(path);
        CaptureLinks(path);
        return AssetDeleteResult.DidNotDelete;
    }

    static void CaptureLinks(string path)
    {
        var snap = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(path);
        if (snap != null)
        {
            _batchTriggerCount++;
            RememberRef(snap.disk);
            RememberRef(snap.screenshot);
            return;
        }

        var disk = AssetDatabase.LoadAssetAtPath<DiskAsset>(path);
        if (disk == null)
            return;

        // Scan while this disk asset is still loadable.
        var snapsByDisk = UqsnapAsset.BuildIndexByDisk();
        if (!snapsByDisk.TryGetValue(disk, out var snaps) || snaps == null || snaps.Count == 0)
            return;

        _batchTriggerCount++;
        foreach (var linked in snaps)
        {
            if (linked == null)
                continue;
            RememberRef(linked);
            RememberRef(linked.screenshot);
        }
    }

    static void RememberRef(UnityEngine.Object obj)
    {
        if (obj == null)
            return;
        string path = Normalize(AssetDatabase.GetAssetPath(obj));
        if (!string.IsNullOrEmpty(path))
            _batchExtras.Add(path);
    }

    static void ProcessBatch()
    {
        _batchPending = false;

        var extras = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _batchExtras)
        {
            if (_batchDeleting.Contains(path))
                continue;
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                continue;
            extras.Add(path);
        }

        _batchDeleting.Clear();
        _batchExtras.Clear();
        int triggerCount = _batchTriggerCount;
        _batchTriggerCount = 0;

        if (extras.Count == 0)
            return;

        if (!EditorUtility.DisplayDialog(
                "Delete associated snapshot files?",
                BuildMessage(extras, triggerCount),
                "Delete Associated",
                "Keep Associated"))
            return;

        _insideCascade = true;
        try
        {
            bool toTrash = (_batchOptions & RemoveAssetOptions.MoveAssetToTrash) != 0;
            foreach (string path in extras)
            {
                if (toTrash)
                    AssetDatabase.MoveAssetToTrash(path);
                else
                    AssetDatabase.DeleteAsset(path);
            }
        }
        finally
        {
            _insideCascade = false;
        }
    }

    static string BuildMessage(SortedSet<string> extras, int triggerCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("These assets are linked by UqsnapAsset references:");
        sb.AppendLine();
        foreach (string path in extras)
            sb.AppendLine("  • " + path);
        sb.AppendLine();
        sb.Append("Also delete them?");
        if (triggerCount > 1)
            sb.Append($" ({triggerCount} snapshot assets selected)");
        return sb.ToString();
    }

    static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
}
}
