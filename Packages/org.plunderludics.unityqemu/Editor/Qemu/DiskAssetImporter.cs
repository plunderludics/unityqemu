using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.qcow2</c> and <c>.uqsnap</c> as <see cref="DiskAsset"/>.
/// Snapshots set <see cref="hasUqsnapMetadata"/>; plain disks leave it false.
/// QEMU must not write these files — use ephemeral Library work images.
/// </summary>
[ScriptedImporter(4, new[] { "qcow2", "uqsnap" })]
public class DiskAssetImporter : ScriptedImporter
{
    [Tooltip("Immediate backing DiskAsset (from the qcow2 backing-file header).")]
    public DiskAsset backingDisk;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported DiskAsset.")]
    public string note;

    [Tooltip("True when this import is a durable .uqsnap (drives DiskAsset.HasVmState).")]
    public bool hasUqsnapMetadata;

    [Tooltip("Durable-snapshot metadata. Used when hasUqsnapMetadata is set; ignored for plain .qcow2.")]
    public UqsnapMetadata uqsnapMetadata;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        bool isUqsnap = ctx.assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase);

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
                    // Fix stale absolute backing paths from another checkout/machine.
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

        // Extension is authoritative for whether this file can be a durable snapshot.
        if (isUqsnap)
            hasUqsnapMetadata = true;
        else
            hasUqsnapMetadata = false;

        var disk = ScriptableObject.CreateInstance<DiskAsset>();
        disk.projectRelativeQcow2Path = ctx.assetPath.Replace('\\', '/');
        disk.backingDisk = resolvedBacking;
        disk.note = note ?? "";
        disk.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        disk.name = disk.label;
        disk.hasUqsnapMetadata = hasUqsnapMetadata;
        disk.uqsnapMetadata = hasUqsnapMetadata
            ? (uqsnapMetadata != null ? uqsnapMetadata.Clone() : UqsnapMetadata.CreateEmpty())
            : null;

        ctx.AddObjectToAsset("main", disk);
        ctx.SetMainObject(disk);
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

/// <summary>
/// Importer inspector: settings only. Tree / kind header live on <see cref="DiskAssetEditor"/>.
/// Hides snapshot metadata fields for plain .qcow2 assets.
/// </summary>
[CustomEditor(typeof(DiskAssetImporter))]
public class DiskAssetImporterEditor : ScriptedImporterEditor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("backingDisk"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("note"));

        string path = AssetDatabase.GetAssetPath(assetTarget);
        bool isUqsnap = !string.IsNullOrEmpty(path) &&
            path.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase);
        if (isUqsnap)
        {
            SerializedProperty hasMeta = serializedObject.FindProperty("hasUqsnapMetadata");
            SerializedProperty meta = serializedObject.FindProperty("uqsnapMetadata");
            if (hasMeta != null)
                EditorGUILayout.PropertyField(hasMeta);
            if (meta != null)
                EditorGUILayout.PropertyField(meta, true);
        }

        serializedObject.ApplyModifiedProperties();
        ApplyRevertGUI();
    }
}
}
