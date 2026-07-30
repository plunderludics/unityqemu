using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Handle for an imported <c>.img</c>/<c>.ima</c> floppy image. Thin wrapper so inspectors
/// can take a typed reference instead of a generic <see cref="UnityEngine.Object"/>.
/// </summary>
[Icon("Packages/org.plunderludics.unityqemu/Editor/Icons/FloppyAssetIcon.png")]
public class FloppyAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation for this floppy image.")]
    public string note;

    [Tooltip("Project-relative path to the image (e.g. Assets/qemu/boot.img). Set by the importer.")]
    public string projectRelativeImgPath;

    /// <summary>Filesystem path to the floppy image bytes.</summary>
    public string GetImgFilesystemPath()
    {
        string rel = projectRelativeImgPath;
#if UNITY_EDITOR
        try
        {
            string assetPath = AssetDatabase.GetAssetPath(this);
            if (IsFloppyImageAssetPath(assetPath))
                rel = assetPath;
        }
        catch (UnityException)
        {
            // Fall back to serialized projectRelativeImgPath.
        }
#endif
        if (string.IsNullOrEmpty(rel))
            return null;
        return Paths.ResolveProjectRelativeFile(rel);
    }

    public string DisplayLabel =>
        !string.IsNullOrEmpty(label) ? label : name;

    public static bool IsFloppyImageAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;
        return assetPath.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
               assetPath.EndsWith(".ima", StringComparison.OrdinalIgnoreCase);
    }

#if UNITY_EDITOR
    /// <summary>Find an existing FloppyAsset whose image resolves to this filesystem path.</summary>
    public static FloppyAsset FindByFilesystemPath(string filesystemPath)
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
            FloppyAsset direct = AssetDatabase.LoadAssetAtPath<FloppyAsset>(projectPath);
            if (direct != null)
                return direct;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:FloppyAsset"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            FloppyAsset candidate = AssetDatabase.LoadAssetAtPath<FloppyAsset>(assetPath);
            if (candidate == null)
                continue;
            string candidatePath = candidate.GetImgFilesystemPath();
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
        EditorApplication.delayCall -= SyncProjectRelativeImgPathDeferred;
        EditorApplication.delayCall += SyncProjectRelativeImgPathDeferred;
#endif
    }

#if UNITY_EDITOR
    void SyncProjectRelativeImgPathDeferred()
    {
        if (this == null)
            return;
        string assetPath = AssetDatabase.GetAssetPath(this);
        if (IsFloppyImageAssetPath(assetPath) &&
            !string.Equals(projectRelativeImgPath, assetPath, StringComparison.Ordinal))
            projectRelativeImgPath = assetPath;
    }
#endif
}
}
