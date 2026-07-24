using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.qcow2</c> as a <see cref="DiskAsset"/>. Pure disk — no machine state.
/// </summary>
[ScriptedImporter(6, "qcow2")]
public class DiskAssetImporter : ScriptedImporter
{
    [Tooltip("Immediate backing DiskAsset (from the qcow2 backing-file header).")]
    public DiskAsset backingDisk;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported DiskAsset.")]
    public string note;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        DiskAsset resolvedBacking = null;
        try
        {
            string fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", ctx.assetPath));
            string backingPath = DiskOverlay.GetBackingPath(fullPath);
            if (!string.IsNullOrEmpty(backingPath))
            {
                resolvedBacking = DiskAsset.FindByFilesystemPath(backingPath);
                if (resolvedBacking == null)
                {
                    ctx.LogImportWarning(
                        $"Image backing file '{backingPath}' has no DiskAsset yet; " +
                        "reimport after its asset exists");
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
