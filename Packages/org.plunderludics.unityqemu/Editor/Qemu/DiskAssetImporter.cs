using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.qcow2</c> as a <see cref="DiskAsset"/>. Pure disk — no machine state.
/// </summary>
[ScriptedImporter(7, "qcow2")]
public class DiskAssetImporter : ScriptedImporter
{
    [Tooltip("Immediate backing DiskAsset (from the qcow2 backing-file header).")]
    public DiskAsset backingDisk;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported DiskAsset.")]
    public string note;

    /// <summary>
    /// Tell the Asset Database to import the backing qcow2 before this one when possible,
    /// and to reimport this asset when that backing's import result changes.
    /// Must not call AssetDatabase APIs.
    /// </summary>
    public static string[] GatherDependenciesFromSourceFile(string assetPath)
    {
        string backingProjectPath = TryGetBackingProjectPath(assetPath);
        if (string.IsNullOrEmpty(backingProjectPath))
            return Array.Empty<string>();
        return new[] { backingProjectPath };
    }

    public override void OnImportAsset(AssetImportContext ctx)
    {
        DiskAsset resolvedBacking = null;
        string backingProjectPath = null;
        try
        {
            string fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", ctx.assetPath));
            string backingFsPath = DiskOverlay.GetBackingPath(fullPath);
            backingProjectPath = TryMakeProjectRelativeAssetPath(backingFsPath);
            if (!string.IsNullOrEmpty(backingProjectPath))
            {
                // Artifact dep: reimport when the backing DiskAsset is created/updated
                // (source-file dep alone does not fire on first import of the sibling).
                ctx.DependsOnArtifact(backingProjectPath);

                resolvedBacking = AssetDatabase.LoadAssetAtPath<DiskAsset>(backingProjectPath);
                if (resolvedBacking == null)
                {
                    // Usually transient — Asset Database should reimport us after the
                    // backing finishes (GatherDependencies / DependsOnArtifact).
                    ctx.LogImportWarning(
                        $"Backing '{backingProjectPath}' has no DiskAsset yet; " +
                        "will retry when that asset finishes importing.");
                }
                else
                {
                    string localBacking = resolvedBacking.GetQcow2FilesystemPath();
                    if (!string.IsNullOrEmpty(localBacking) && File.Exists(localBacking))
                    {
                        try
                        {
                            DiskOverlay.EnsureBackingMatches(fullPath, localBacking);
                        }
                        catch (Exception e)
                        {
                            ctx.LogImportWarning(
                                $"Resolved backing DiskAsset '{resolvedBacking.name}' but could not " +
                                $"repair the qcow2 header path: {e.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            ctx.LogImportWarning($"Could not inspect image backing metadata: {e.Message}");
        }

        if (resolvedBacking == null)
            resolvedBacking = backingDisk;

        if (backingDisk != resolvedBacking && resolvedBacking != null)
            SchedulePersistInferredBacking(ctx.assetPath, resolvedBacking);

        var disk = ScriptableObject.CreateInstance<DiskAsset>();
        disk.projectRelativeQcow2Path = ctx.assetPath.Replace('\\', '/');
        disk.backingDisk = resolvedBacking;
        disk.note = note ?? "";
        disk.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        disk.name = disk.label;

        ctx.AddObjectToAsset("main", disk);
        ctx.SetMainObject(disk);

        Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Packages/org.plunderludics.unityqemu/Editor/Icons/DiskAssetIcon.png");
        if (icon != null)
            EditorGUIUtility.SetIconForObject(disk, icon);
    }

    /// <summary>
    /// Project-relative path of this image's qcow2 backing file, or null.
    /// Safe for <see cref="GatherDependenciesFromSourceFile"/> (no AssetDatabase).
    /// </summary>
    static string TryGetBackingProjectPath(string assetPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", assetPath));
            if (!File.Exists(fullPath))
                return null;
            string backingFsPath = DiskOverlay.GetBackingPath(fullPath);
            return TryMakeProjectRelativeAssetPath(backingFsPath);
        }
        catch
        {
            return null;
        }
    }

    static string TryMakeProjectRelativeAssetPath(string absoluteOrNull)
    {
        if (string.IsNullOrEmpty(absoluteOrNull) || !File.Exists(absoluteOrNull))
            return null;

        string full = Path.GetFullPath(absoluteOrNull);
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string rootPrefix = projectRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return full.Substring(rootPrefix.Length).Replace('\\', '/');

        // Foreign absolute path that still ends in /Assets/... — map into this project.
        string normalized = full.Replace('\\', '/');
        int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetsIdx < 0)
            return null;
        string fromAssets = normalized.Substring(assetsIdx + 1);
        string remapped = Path.GetFullPath(Path.Combine(
            projectRoot, fromAssets.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(remapped))
            return null;
        if (!remapped.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return remapped.Substring(rootPrefix.Length).Replace('\\', '/');
    }

    static void SchedulePersistInferredBacking(string imageAssetPath, DiskAsset inferredBacking)
    {
        string backingAssetPath = AssetDatabase.GetAssetPath(inferredBacking);
        if (string.IsNullOrEmpty(backingAssetPath))
            return;

        EditorApplication.delayCall += () =>
        {
            var importer = AssetImporter.GetAtPath(imageAssetPath) as DiskAssetImporter;
            DiskAsset backing = AssetDatabase.LoadAssetAtPath<DiskAsset>(backingAssetPath);
            if (importer == null || backing == null)
                return;
            if (importer.backingDisk == backing)
                return;

            importer.backingDisk = backing;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(imageAssetPath);
            Debug.Log(
                $"UnityQemu persisted inferred backingDisk for '{imageAssetPath}': " +
                $"'{backingAssetPath}'");
        };
    }
}

[CustomEditor(typeof(DiskAssetImporter))]
public class DiskAssetImporterEditor : ScriptedImporterEditor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("backingDisk"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("note"));
        serializedObject.ApplyModifiedProperties();
        ApplyRevertGUI();
    }
}
}
