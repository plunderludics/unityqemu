using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>Create <see cref="QemuDiskAsset"/> handles for qcow2 files outside the import pipeline (e.g. qemu~).</summary>
public static class QemuDiskAssetMenu
{
    [MenuItem("Assets/Create/UnityQemu/Disk Asset From QCOW2…", false, 210)]
    public static void CreateDiskFromQcow2()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string picked = EditorUtility.OpenFilePanel("Select base qcow2", projectRoot, "qcow2");
        if (string.IsNullOrEmpty(picked))
            return;

        string full = Path.GetFullPath(picked);
        string rel = MakeProjectRelative(full, projectRoot);

        string assetDir = "Assets";
        if (Selection.activeObject != null)
        {
            string sel = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(sel))
                assetDir = AssetDatabase.IsValidFolder(sel) ? sel : Path.GetDirectoryName(sel);
        }

        string baseName = Path.GetFileNameWithoutExtension(full);
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(
            Path.Combine(assetDir, baseName + ".asset").Replace('\\', '/'));

        var disk = ScriptableObject.CreateInstance<QemuDiskAsset>();
        disk.label = baseName;
        disk.projectRelativeQcow2Path = rel.Replace('\\', '/');
        AssetDatabase.CreateAsset(disk, assetPath);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(disk);
        Debug.Log($"Created QemuDiskAsset → {assetPath} (qcow2: {disk.projectRelativeQcow2Path})");
    }

    static string MakeProjectRelative(string fullPath, string projectRoot)
    {
        fullPath = Path.GetFullPath(fullPath);
        projectRoot = Path.GetFullPath(projectRoot);
        if (fullPath.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            string rel = fullPath.Substring(projectRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace('\\', '/');
        }
        // Outside project: store absolute path (still works with GetQcow2FilesystemPath).
        return fullPath.Replace('\\', '/');
    }
}
}
