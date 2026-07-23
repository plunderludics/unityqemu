using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Handle for an imported <c>.iso</c> CD-ROM image. Thin wrapper so inspectors can take a typed
/// reference instead of a generic <see cref="UnityEngine.Object"/>.
/// </summary>
[CreateAssetMenu(fileName = "CdRom", menuName = "UnityQemu/CD-ROM Asset", order = 12)]
public class CdRomAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation for this ISO.")]
    public string note;

    [Tooltip("Project-relative path to the .iso (e.g. Assets/Qemu/game.iso). Set by the importer.")]
    public string projectRelativeIsoPath;

    /// <summary>Filesystem path to the ISO bytes.</summary>
    public string GetIsoFilesystemPath()
    {
        string rel = projectRelativeIsoPath;
#if UNITY_EDITOR
        // Importer-backed CdRomAsset lives on the .iso itself — use the live asset path so
        // moves/renames don't leave a stale projectRelativeIsoPath.
        // AssetDatabase is main-thread only (scene load can call this off-thread).
        try
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (IsIsoAssetPath(assetPath))
                rel = assetPath;
        }
        catch (UnityException)
        {
            // Fall back to serialized projectRelativeIsoPath.
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

    public static bool IsIsoAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;
        return assetPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
    }

#if UNITY_EDITOR
    /// <summary>Find an existing CdRomAsset whose ISO resolves to this filesystem path.</summary>
    public static CdRomAsset FindByFilesystemPath(string filesystemPath)
    {
        if (string.IsNullOrEmpty(filesystemPath))
            return null;

        string wanted = Path.GetFullPath(filesystemPath);
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string rootPrefix = projectRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (wanted.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string projectPath = wanted.Substring(rootPrefix.Length).Replace('\\', '/');
            CdRomAsset direct = AssetDatabase.LoadAssetAtPath<CdRomAsset>(projectPath);
            if (direct != null)
                return direct;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:CdRomAsset"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            CdRomAsset candidate = AssetDatabase.LoadAssetAtPath<CdRomAsset>(assetPath);
            if (candidate == null)
                continue;
            string candidatePath = candidate.GetIsoFilesystemPath();
            if (!string.IsNullOrEmpty(candidatePath) &&
                string.Equals(
                    Path.GetFullPath(candidatePath),
                    wanted,
                    StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }
#endif

    void OnValidate()
    {
        if (string.IsNullOrEmpty(label))
            label = name;
#if UNITY_EDITOR
        // AssetDatabase is main-thread only; OnValidate can run on the scene loading thread.
        EditorApplication.delayCall -= SyncProjectRelativeIsoPathDeferred;
        EditorApplication.delayCall += SyncProjectRelativeIsoPathDeferred;
#endif
    }

#if UNITY_EDITOR
    void SyncProjectRelativeIsoPathDeferred()
    {
        if (this == null)
            return;
        string assetPath = AssetDatabase.GetAssetPath(this);
        if (IsIsoAssetPath(assetPath) &&
            !string.Equals(projectRelativeIsoPath, assetPath, StringComparison.Ordinal))
            projectRelativeIsoPath = assetPath;
    }
#endif
}
}
