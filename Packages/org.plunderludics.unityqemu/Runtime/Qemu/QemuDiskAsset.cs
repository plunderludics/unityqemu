using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Immutable base disk handle for D2 snapshots. Usually the main object of an imported <c>.qcow2</c>.
/// QEMU must never write this file — only ephemeral work overlays.
/// </summary>
[CreateAssetMenu(fileName = "QemuDisk", menuName = "UnityQemu/Disk Asset", order = 10)]
public class QemuDiskAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [Tooltip("Optional RAM hint for VirtualMachine (-m)")]
    public int recommendedRamMiB = 64;

    [Tooltip("Project-relative path to the .qcow2 (e.g. Assets/Qemu/win95.qcow2). Set by the importer.")]
    public string projectRelativeQcow2Path;

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
