using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Immutable qcow2 image handle — main object of an imported <c>.qcow2</c> or <c>.uqsnap</c>.
/// Overlay parents are always another <see cref="DiskAsset"/> via <see cref="backingDisk"/>.
/// Durable snapshots set <see cref="hasUqsnapMetadata"/>; plain disks leave it clear.
/// QEMU must never write the asset file — only ephemeral Library work images.
/// </summary>
[CreateAssetMenu(fileName = "Disk", menuName = "UnityQemu/Disk Asset", order = 10)]
public class DiskAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation for this image.")]
    public string note;

    [Tooltip("Project-relative path to the image (e.g. Assets/Qemu/win95.qcow2 or …/snap.uqsnap).")]
    public string projectRelativeQcow2Path;

    [Tooltip("Immediate backing image (qcow2 header). Empty for a standalone/base image.")]
    public DiskAsset backingDisk;

    [Tooltip("True for durable snapshots (.uqsnap). Plain disks leave this clear.")]
    public bool hasUqsnapMetadata;

    [Tooltip("Launch config and version metadata for durable snapshots. Used when hasUqsnapMetadata is set.")]
    public UqsnapMetadata uqsnapMetadata;

    /// <summary>True when this asset is a durable snapshot.</summary>
    public bool HasVmState => hasUqsnapMetadata;

    /// <summary>Suffix appended to the .uqsnap path for the D4 compressed vmstate sidecar.</summary>
    public const string VmStateSidecarSuffix = ".vmstate";

    /// <summary>Filesystem path of the D4 vmstate sidecar (whether or not it exists).</summary>
    public string GetVmStateSidecarPath()
    {
        string imagePath = GetQcow2FilesystemPath();
        return string.IsNullOrEmpty(imagePath) ? null : imagePath + VmStateSidecarSuffix;
    }

    /// <summary>
    /// True when a D4 vmstate sidecar exists on disk. D4 snapshots boot as a thin
    /// overlay + incoming migration; sidecar-less .uqsnaps use the legacy
    /// byte-copy + loadvm path (embedded savevm).
    /// </summary>
    public bool HasVmStateSidecar
    {
        get
        {
            string sidecar = GetVmStateSidecarPath();
            return !string.IsNullOrEmpty(sidecar) && File.Exists(sidecar);
        }
    }

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

    public string DisplayLabel =>
        !string.IsNullOrEmpty(label) ? label : name;

    public static bool IsQemuImageAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;
        return assetPath.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase) ||
               assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase);
    }

#if UNITY_EDITOR
    /// <summary>Disk assets that list <paramref name="parent"/> as <see cref="backingDisk"/>.</summary>
    public static List<DiskAsset> GetChildDisks(DiskAsset parent)
    {
        var children = new List<DiskAsset>();
        if (parent == null)
            return children;
        foreach (string guid in AssetDatabase.FindAssets("t:DiskAsset"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            DiskAsset candidate = AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
            if (candidate != null && candidate.backingDisk == parent)
                children.Add(candidate);
        }
        children.Sort((a, b) => string.Compare(
            a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));
        return children;
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

    /// <summary>
    /// Backing chain from ultimate base down to this disk (inclusive), root first.
    /// Used for tree visualization only.
    /// </summary>
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

    /// <summary>
    /// Find a DiskAsset whose image matches <paramref name="filesystemPath"/>.
    /// Tries exact path, then remaps a foreign <c>…/Assets/…</c> absolute path into this project
    /// (common when qcow2 headers retain paths from another checkout).
    /// </summary>
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

        // Stale absolute path from another project/machine: …/Assets/qemu/… → this project's Assets/…
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
            // PathsEqual also matches Windows junctions / same file via different roots.
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

    /// <summary>
    /// If <paramref name="absolutePath"/> contains an <c>Assets</c> segment, rewrite it under
    /// this project's root (keeps the relative path from Assets onward).
    /// </summary>
    static string RemapForeignAssetsPath(string absolutePath, string rootPrefix)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return null;
        string normalized = absolutePath.Replace('\\', '/');
        int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetsIdx < 0)
        {
            // Windows path may start with Assets\ without a leading slash after drive quirks
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                assetsIdx = -1; // already project-relative shape
            else
                return null;
        }

        string fromAssets = assetsIdx >= 0
            ? normalized.Substring(assetsIdx + 1) // "Assets/…"
            : normalized;
        string remapped = Path.GetFullPath(Path.Combine(
            rootPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            fromAssets.Replace('/', Path.DirectorySeparatorChar)));
        if (string.Equals(remapped, Path.GetFullPath(absolutePath), StringComparison.OrdinalIgnoreCase))
            return null; // already in this project
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

    void OnValidate()
    {
        if (string.IsNullOrEmpty(label))
            label = name;
#if UNITY_EDITOR
        // AssetDatabase is main-thread only; OnValidate can run on the scene loading thread.
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
