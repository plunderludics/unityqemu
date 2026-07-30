using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Shared caches for snapshot/disk tree inspectors. Rebuilds are expensive
/// (<see cref="AssetDatabase.FindAssets"/> + file stats) and must not run on every
/// Layout/Repaint while the mouse moves over the inspector.
/// Invalidated when disk / uqsnap assets change (see <see cref="SnapshotTreeCachePostprocessor"/>).
/// </summary>
static class SnapshotTreeCache
{
    static Dictionary<DiskAsset, List<UqsnapAsset>> _snapsByDisk;
    static Dictionary<DiskAsset, List<DiskAsset>> _childrenByParent;
    static readonly Dictionary<string, long> _lengths = new Dictionary<string, long>();

    public static void Invalidate()
    {
        _snapsByDisk = null;
        _childrenByParent = null;
        _lengths.Clear();
    }

    public static Dictionary<DiskAsset, List<UqsnapAsset>> SnapsByDisk()
    {
        if (_snapsByDisk == null)
            _snapsByDisk = UqsnapAsset.BuildIndexByDisk();
        return _snapsByDisk;
    }

    public static Dictionary<DiskAsset, List<DiskAsset>> ChildrenByParent()
    {
        if (_childrenByParent == null)
            _childrenByParent = DiskAsset.BuildChildrenIndex();
        return _childrenByParent;
    }

    /// <summary>
    /// Cached <see cref="FileInfo.Length"/>; returns null if missing/unreadable.
    /// </summary>
    public static long? GetFileLength(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath))
            return null;

        if (_lengths.TryGetValue(filesystemPath, out long cached))
            return cached;

        try
        {
            if (!File.Exists(filesystemPath))
                return null;

            long length = new FileInfo(filesystemPath).Length;
            _lengths[filesystemPath] = length;
            return length;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>Clears <see cref="SnapshotTreeCache"/> when disk or snapshot assets change.</summary>
class SnapshotTreeCachePostprocessor : AssetPostprocessor
{
    static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        if (AffectsDiskOrSnapTree(importedAssets) ||
            AffectsDiskOrSnapTree(deletedAssets) ||
            AffectsDiskOrSnapTree(movedAssets) ||
            AffectsDiskOrSnapTree(movedFromAssetPaths))
            SnapshotTreeCache.Invalidate();
    }

    static bool AffectsDiskOrSnapTree(string[] paths)
    {
        if (paths == null)
            return false;
        for (int i = 0; i < paths.Length; i++)
        {
            string p = paths[i];
            if (string.IsNullOrEmpty(p))
                continue;
            if (p.EndsWith(".qcow2", System.StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".uqsnap", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
}
