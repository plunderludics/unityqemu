using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable D2 snapshot: ScriptableObject metadata + reference to an immutable overlay qcow2
/// that contains the <c>__unityqemu_state</c> savevm tag.
/// </summary>
[CreateAssetMenu(fileName = "QemuSnapshot", menuName = "UnityQemu/Snapshot Asset", order = 11)]
public class QemuSnapshotAsset : ScriptableObject
{
    [Tooltip("Base disk this snapshot's overlay is backed by")]
    public QemuDiskAsset disk;

    [Tooltip("Immutable snapshot .qcow2 (usually imported as another QemuDiskAsset)")]
    public QemuDiskAsset image;

    [TextArea(2, 4)]
    public string note;

    [Tooltip("ISO-8601 timestamp when the snapshot was created")]
    public string createdAt;

    /// <summary>Filesystem path to the snapshot qcow2 image.</summary>
    public string GetImageFilesystemPath()
    {
        if (image != null)
            return image.GetQcow2FilesystemPath();
        return null;
    }

    public void StampCreatedNow()
    {
        createdAt = DateTime.UtcNow.ToString("o");
    }

#if UNITY_EDITOR
    /// <summary>Resolve or create a QemuDiskAsset for a .qcow2 under Assets.</summary>
    public static QemuDiskAsset FindDiskAssetAt(string projectRelativeQcow2Path)
    {
        if (string.IsNullOrEmpty(projectRelativeQcow2Path))
            return null;
        return AssetDatabase.LoadAssetAtPath<QemuDiskAsset>(projectRelativeQcow2Path);
    }
#endif
}
}
