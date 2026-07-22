using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Immutable disk handle. Usually the main object of an imported <c>.qcow2</c>.
/// Overlay images reference their underlying image through <see cref="backingDisk"/>.
/// QEMU must never write this file — only ephemeral work overlays.
/// </summary>
[CreateAssetMenu(fileName = "Disk", menuName = "UnityQemu/Disk Asset", order = 10)]
public class DiskAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [Tooltip("Optional RAM hint for VirtualMachine (-m)")]
    public int recommendedRamMiB = 64;

    [Tooltip("Project-relative path to the .qcow2 (e.g. Assets/Qemu/win95.qcow2). Set by the importer.")]
    public string projectRelativeQcow2Path;

    [Tooltip("Disk asset referenced by this qcow2's backing-file header. Leave empty for a standalone/base image.")]
    public DiskAsset backingDisk;

    /// <summary>Filesystem path to the immutable base qcow2.</summary>
    public string GetQcow2FilesystemPath()
    {
        string rel = projectRelativeQcow2Path;
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(rel))
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(assetPath) &&
                assetPath.EndsWith(".qcow2", System.StringComparison.OrdinalIgnoreCase))
                rel = assetPath;
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

#if UNITY_EDITOR
    /// <summary>Find an existing Unity disk asset whose qcow2 resolves to this filesystem path.</summary>
    public static DiskAsset FindByFilesystemPath(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath))
            return null;

        string wanted = Path.GetFullPath(filesystemPath);
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string rootPrefix = projectRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (wanted.StartsWith(rootPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            string projectPath = wanted.Substring(rootPrefix.Length).Replace('\\', '/');
            DiskAsset direct = AssetDatabase.LoadAssetAtPath<DiskAsset>(projectPath);
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
            if (!string.IsNullOrEmpty(candidatePath) &&
                string.Equals(
                    Path.GetFullPath(candidatePath),
                    wanted,
                    System.StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    public void SaveInferredBackingDisk()
    {
        EditorUtility.SetDirty(this);
        string assetPath = AssetDatabase.GetAssetPath(this);
        if (string.Equals(Path.GetExtension(assetPath), ".asset", System.StringComparison.OrdinalIgnoreCase))
            AssetDatabase.SaveAssetIfDirty(this);
    }
#endif

    void OnValidate()
    {
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(projectRelativeQcow2Path))
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(assetPath) &&
                assetPath.EndsWith(".qcow2", System.StringComparison.OrdinalIgnoreCase))
                projectRelativeQcow2Path = assetPath;
        }
#endif
        if (string.IsNullOrEmpty(label))
            label = name;
    }
}
}
