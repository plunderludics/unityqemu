using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>Create <see cref="DiskAsset"/> handles for qcow2 files outside the import pipeline (e.g. qemu~).</summary>
public static class DiskAssetMenu
{
    [MenuItem("Assets/UnityQemu/Infer QCOW2 Backing Chain", true)]
    static bool CanInferBackingChain() => Selection.activeObject is DiskAsset;

    [MenuItem("Assets/UnityQemu/Infer QCOW2 Backing Chain", false, 209)]
    static void InferBackingChain()
    {
        var selected = Selection.activeObject as DiskAsset;
        if (selected == null)
            return;

        string full = selected.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(full) || !File.Exists(full))
        {
            Debug.LogWarning($"DiskAsset '{selected.name}' has no readable qcow2 at '{full}'");
            return;
        }

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string selectedAssetPath = AssetDatabase.GetAssetPath(selected);
        string assetDir = Path.GetDirectoryName(selectedAssetPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(assetDir))
            assetDir = "Assets";

        DiskAsset resolved = FindOrCreateDiskChain(
            full, projectRoot, assetDir, new HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(resolved);
    }

    [MenuItem("Assets/Create/UnityQemu/Disk Asset From QCOW2…", false, 210)]
    public static void CreateDiskFromQcow2()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string picked = EditorUtility.OpenFilePanel("Select base qcow2", projectRoot, "qcow2");
        if (string.IsNullOrEmpty(picked))
            return;

        string full = Path.GetFullPath(picked);
        string assetDir = "Assets";
        if (Selection.activeObject != null)
        {
            string sel = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(sel))
                assetDir = AssetDatabase.IsValidFolder(sel) ? sel : Path.GetDirectoryName(sel);
        }

        DiskAsset disk = FindOrCreateDiskChain(
            full, projectRoot, assetDir, new HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(disk);
        Debug.Log(
            $"DiskAsset ready: {AssetDatabase.GetAssetPath(disk)} " +
            $"(qcow2: {disk.projectRelativeQcow2Path})");
    }

    static DiskAsset FindOrCreateDiskChain(
        string fullPath,
        string projectRoot,
        string assetDir,
        HashSet<string> visiting)
    {
        fullPath = Path.GetFullPath(fullPath);
        DiskAsset disk = DiskAsset.FindByFilesystemPath(fullPath);
        if (disk == null)
        {
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(assetDir, baseName + ".asset").Replace('\\', '/'));
            disk = ScriptableObject.CreateInstance<DiskAsset>();
            disk.label = baseName;
            disk.projectRelativeQcow2Path =
                MakeProjectRelative(fullPath, projectRoot).Replace('\\', '/');
            AssetDatabase.CreateAsset(disk, assetPath);
            Debug.Log($"Created DiskAsset → {assetPath}");
        }

        if (!visiting.Add(fullPath))
        {
            Debug.LogWarning($"Cycle found while reading qcow2 backing chain at '{fullPath}'");
            return disk;
        }

        try
        {
            string backingPath = DiskOverlay.GetBackingPath(fullPath);
            if (!string.IsNullOrEmpty(backingPath))
            {
                DiskAsset backing = FindOrCreateDiskChain(
                    backingPath, projectRoot, assetDir, visiting);
                disk = AssignBackingDisk(disk, backing);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(
                $"Could not infer backing metadata for '{fullPath}'. " +
                $"The disk asset was created without it. {e.Message}");
        }
        finally
        {
            visiting.Remove(fullPath);
        }

        return disk;
    }

    static DiskAsset AssignBackingDisk(DiskAsset disk, DiskAsset backing)
    {
        if (disk == null || disk.backingDisk == backing)
            return disk;

        string assetPath = AssetDatabase.GetAssetPath(disk);
        AssetImporter importer = AssetImporter.GetAtPath(assetPath);
        var serializedImporter = importer != null ? new SerializedObject(importer) : null;
        SerializedProperty property = serializedImporter?.FindProperty("backingDisk");
        if (property != null)
        {
            property.objectReferenceValue = backing;
            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
        }

        disk.backingDisk = backing;
        EditorUtility.SetDirty(disk);
        return disk;
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
