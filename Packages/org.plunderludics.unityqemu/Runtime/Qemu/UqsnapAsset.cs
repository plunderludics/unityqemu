using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable snapshot: QEMU migration-stream bytes on a <c>.uqsnap</c> file, plus a
/// reference to the coupled disk tip (<see cref="disk"/>).
/// </summary>
[Icon("Packages/org.plunderludics.unityqemu/Editor/Icons/UqsnapAssetIcon.png")]
public class UqsnapAsset : BootableAsset
{
    [Tooltip("Project-relative path to the .uqsnap migration stream (set by the importer).")]
    public string projectRelativeUqsnapPath;

    [Tooltip("Disk tip this machine state was captured against (thin .qcow2 delta or base).")]
    public DiskAsset disk;

    [Tooltip("Launch config and version metadata recorded at save time.")]
    public UqsnapMetadata metadata;

    [Tooltip("Optional preview from a sibling .png (same basename as the .uqsnap).")]
    public Texture2D screenshot;

    public override DiskAsset DiskTip => disk;

    /// <summary>Project-relative path of the sibling preview PNG for this .uqsnap.</summary>
    public static string SiblingScreenshotProjectPath(string uqsnapProjectPath)
    {
        if (string.IsNullOrEmpty(uqsnapProjectPath))
            return null;
        return Path.ChangeExtension(uqsnapProjectPath.Replace('\\', '/'), ".png");
    }

    /// <summary>
    /// True when the <c>.uqsnap</c> holds a non-empty migration stream.
    /// Empty files are disk-only placeholders (e.g. save while writable vvfat blocked migrate).
    /// </summary>
    public bool HasMachineState
    {
        get
        {
            string path = GetMachineStateFilesystemPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            try
            {
                return new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public string GetMachineStateFilesystemPath()
    {
        string rel = projectRelativeUqsnapPath;
#if UNITY_EDITOR
        try
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (IsUqsnapAssetPath(assetPath))
                rel = assetPath;
        }
        catch (UnityException)
        {
            // AssetDatabase is main-thread only.
        }
#endif
        if (string.IsNullOrEmpty(rel))
            return null;
        return Paths.ResolveProjectRelativeFile(rel);
    }

    public bool MachineStateIsCompressed =>
        metadata == null || metadata.VmstateIsCompressed;

    public LaunchConfig GetStoredLaunchConfig() =>
        metadata != null ? metadata.launchConfig : null;

#if UNITY_EDITOR
    /// <summary>
    /// One project-wide scan: disk tip → snapshots that reference it.
    /// Prefer this for tree/inspector draws (one scan per paint).
    /// </summary>
    public static System.Collections.Generic.Dictionary<DiskAsset, System.Collections.Generic.List<UqsnapAsset>>
        BuildIndexByDisk()
    {
        var map = new System.Collections.Generic.Dictionary<DiskAsset, System.Collections.Generic.List<UqsnapAsset>>();
        foreach (string guid in AssetDatabase.FindAssets("t:UqsnapAsset"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var snap = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(path);
            if (snap == null || snap.disk == null)
                continue;
            if (!map.TryGetValue(snap.disk, out var list))
            {
                list = new System.Collections.Generic.List<UqsnapAsset>();
                map[snap.disk] = list;
            }
            list.Add(snap);
        }
        foreach (var list in map.Values)
        {
            list.Sort((a, b) => string.Compare(
                a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));
        }
        return map;
    }
#endif

    public static bool IsUqsnapAssetPath(string assetPath) =>
        !string.IsNullOrEmpty(assetPath) &&
        assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase);
}
}
