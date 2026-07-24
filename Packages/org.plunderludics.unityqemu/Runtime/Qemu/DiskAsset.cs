using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Immutable qcow2 image handle — main object of an imported <c>.qcow2</c>.
/// Overlay parents are always another <see cref="DiskAsset"/> via <see cref="backingDisk"/>.
/// QEMU must never write the asset file — only ephemeral Library work images.
/// Machine state lives on <see cref="UqsnapAsset"/>, which references a disk tip.
/// </summary>
[Icon("Packages/org.plunderludics.unityqemu/Editor/Icons/DiskAssetIcon.png")]
[CreateAssetMenu(fileName = "Disk", menuName = "UnityQemu/Disk Asset", order = 10)]
public class DiskAsset : BootableAsset
{
    [Tooltip("Project-relative path to the image (e.g. Assets/Qemu/win95.qcow2).")]
    public string projectRelativeQcow2Path;

    [Tooltip("Immediate backing image (qcow2 header). Empty for a standalone/base image.")]
    public DiskAsset backingDisk;

    public override DiskAsset DiskTip => this;

    /// <summary>Filesystem path to the immutable image bytes.</summary>
    public string GetQcow2FilesystemPath()
    {
        string rel = projectRelativeQcow2Path;
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(rel))
        {
            try
            {
                string assetPath = AssetDatabase.GetAssetPath(this);
                if (IsQemuImageAssetPath(assetPath))
                    rel = assetPath;
            }
            catch (UnityException)
            {
                // AssetDatabase is main-thread only.
            }
        }
#endif
        if (string.IsNullOrEmpty(rel))
            return null;
        if (Path.IsPathRooted(rel))
            return rel;
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
    }

    public static bool IsQemuImageAssetPath(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath) &&
               assetPath.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase);
    }

#if UNITY_EDITOR
    /// <summary>
    /// One project-wide scan: backing disk → child disks.
    /// Prefer this for tree draws instead of repeated <see cref="GetChildDisks"/>.
    /// </summary>
    public static Dictionary<DiskAsset, List<DiskAsset>> BuildChildrenIndex()
    {
        var map = new Dictionary<DiskAsset, List<DiskAsset>>();
        foreach (string guid in AssetDatabase.FindAssets("t:DiskAsset"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            DiskAsset candidate = AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
            if (candidate == null || candidate.backingDisk == null)
                continue;
            if (!map.TryGetValue(candidate.backingDisk, out var list))
            {
                list = new List<DiskAsset>();
                map[candidate.backingDisk] = list;
            }
            list.Add(candidate);
        }
        foreach (var list in map.Values)
        {
            list.Sort((a, b) => string.Compare(
                a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));
        }
        return map;
    }

    /// <summary>Disk assets that list <paramref name="parent"/> as <see cref="backingDisk"/>.</summary>
    public static List<DiskAsset> GetChildDisks(DiskAsset parent)
    {
        if (parent == null)
            return new List<DiskAsset>();
        if (BuildChildrenIndex().TryGetValue(parent, out var children))
            return new List<DiskAsset>(children);
        return new List<DiskAsset>();
    }

    public static bool HasChildDisks(DiskAsset parent) => GetChildDisks(parent).Count > 0;

    public static string[] GetChildDiskNames(DiskAsset parent)
    {
        List<DiskAsset> children = GetChildDisks(parent);
        var names = new string[children.Count];
        for (int i = 0; i < children.Count; i++)
            names[i] = children[i].DisplayLabel;
        return names;
    }

    public List<DiskAsset> GetChainFromRoot()
    {
        var stack = new Stack<DiskAsset>();
        DiskAsset current = this;
        var seen = new HashSet<DiskAsset>();
        while (current != null && seen.Add(current))
        {
            stack.Push(current);
            current = current.backingDisk;
        }
        return new List<DiskAsset>(stack);
    }

    public DiskAsset GetRootDisk()
    {
        List<DiskAsset> chain = GetChainFromRoot();
        return chain.Count > 0 ? chain[0] : this;
    }

    public static DiskAsset FindByFilesystemPath(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath))
            return null;

        string wanted = Path.GetFullPath(filesystemPath);
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string rootPrefix = projectRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        DiskAsset direct = TryLoadByProjectRelativePath(wanted, rootPrefix);
        if (direct != null)
            return direct;

        string remapped = RemapForeignAssetsPath(wanted, rootPrefix);
        if (!string.IsNullOrEmpty(remapped))
        {
            direct = TryLoadByProjectRelativePath(remapped, rootPrefix);
            if (direct != null)
                return direct;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:DiskAsset"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            DiskAsset candidate = AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
            if (candidate == null)
                continue;
            string candidatePath = candidate.GetQcow2FilesystemPath();
            if (string.IsNullOrEmpty(candidatePath))
                continue;
            if (DiskOverlay.PathsEqual(candidatePath, wanted))
                return candidate;
            if (!string.IsNullOrEmpty(remapped) && DiskOverlay.PathsEqual(candidatePath, remapped))
                return candidate;
        }
        return null;
    }

    static DiskAsset TryLoadByProjectRelativePath(string absoluteOrProjectPath, string rootPrefix)
    {
        string full = Path.IsPathRooted(absoluteOrProjectPath)
            ? Path.GetFullPath(absoluteOrProjectPath)
            : Path.GetFullPath(Path.Combine(
                rootPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                absoluteOrProjectPath));
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string projectPath = full.Substring(rootPrefix.Length).Replace('\\', '/');
        return AssetDatabase.LoadAssetAtPath<DiskAsset>(projectPath);
    }

    static string RemapForeignAssetsPath(string absolutePath, string rootPrefix)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return null;
        string normalized = absolutePath.Replace('\\', '/');
        int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetsIdx < 0)
        {
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                assetsIdx = -1;
            else
                return null;
        }

        string fromAssets = assetsIdx >= 0
            ? normalized.Substring(assetsIdx + 1)
            : normalized;
        string remapped = Path.GetFullPath(Path.Combine(
            rootPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            fromAssets.Replace('/', Path.DirectorySeparatorChar)));
        if (string.Equals(remapped, Path.GetFullPath(absolutePath), StringComparison.OrdinalIgnoreCase))
            return null;
        return remapped;
    }

    public void SaveInferredBackingDisk()
    {
        EditorUtility.SetDirty(this);
        string assetPath = AssetDatabase.GetAssetPath(this);
        if (string.Equals(Path.GetExtension(assetPath), ".asset", StringComparison.OrdinalIgnoreCase))
            AssetDatabase.SaveAssetIfDirty(this);
    }
#endif

    protected override void OnValidate()
    {
        base.OnValidate();
#if UNITY_EDITOR
        EditorApplication.delayCall -= SyncProjectRelativeQcow2PathDeferred;
        EditorApplication.delayCall += SyncProjectRelativeQcow2PathDeferred;
#endif
    }

#if UNITY_EDITOR
    void SyncProjectRelativeQcow2PathDeferred()
    {
        if (this == null)
            return;
        if (!string.IsNullOrEmpty(projectRelativeQcow2Path))
            return;
        string assetPath = AssetDatabase.GetAssetPath(this);
        if (IsQemuImageAssetPath(assetPath))
            projectRelativeQcow2Path = assetPath;
    }
#endif
}
}
