using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable snapshot: main object of an imported <c>.uqsnap</c> file (qcow2 bytes + embedded
/// <c>__unityqemu_state</c> savevm tag). Metadata lives on the importer / this object; QEMU never
/// writes the asset file — boots always copy it to a Library work image first.
/// </summary>
public class SnapshotAsset : ScriptableObject
{
    [Tooltip("Underlying disk this snapshot overlay is backed by.")]
    public DiskAsset backingDisk;

    [TextArea(2, 4)]
    public string note;

    [Tooltip("ISO-8601 timestamp when the snapshot was created")]
    public string createdAt;

    [Tooltip("Project-relative path to this .uqsnap (set by the importer).")]
    public string projectRelativeUqsnapPath;

    /// <summary>Filesystem path to the immutable .uqsnap (qcow2) bytes.</summary>
    public string GetImageFilesystemPath()
    {
        string rel = projectRelativeUqsnapPath;
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(rel))
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(assetPath) &&
                assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase))
                rel = assetPath;
        }
#endif
        if (string.IsNullOrEmpty(rel))
            return null;
        if (Path.IsPathRooted(rel))
            return rel;
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
    }

    public void StampCreatedNow()
    {
        createdAt = DateTime.UtcNow.ToString("o");
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(projectRelativeUqsnapPath))
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (!string.IsNullOrEmpty(assetPath) &&
                assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase))
                projectRelativeUqsnapPath = assetPath;
        }
#endif
    }
}
}
