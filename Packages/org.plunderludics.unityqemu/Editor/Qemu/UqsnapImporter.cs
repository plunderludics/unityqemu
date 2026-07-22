using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.uqsnap</c> (qcow2 bytes with an embedded savevm tag) as a <see cref="SnapshotAsset"/>.
/// QEMU must not write these files — load always copies into a Library work image.
/// </summary>
[ScriptedImporter(1, "uqsnap")]
public class UqsnapImporter : ScriptedImporter
{
    [Tooltip("Disk referenced by this snapshot's qcow2 backing-file header.")]
    public DiskAsset backingDisk;

    [TextArea(2, 4)]
    public string note;

    [Tooltip("ISO-8601 timestamp when the snapshot was created")]
    public string createdAt;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        DiskAsset resolvedBacking = backingDisk;
        if (resolvedBacking == null)
        {
            try
            {
                string fullPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", ctx.assetPath));
                string backingPath = DiskOverlay.GetBackingPath(fullPath);
                resolvedBacking = DiskAsset.FindByFilesystemPath(backingPath);
                if (!string.IsNullOrEmpty(backingPath) && resolvedBacking == null)
                {
                    ctx.LogImportWarning(
                        $"uqsnap backing file '{backingPath}' has no DiskAsset yet; " +
                        "reimport after its asset exists");
                }
            }
            catch (System.Exception e)
            {
                ctx.LogImportWarning($"Could not inspect uqsnap backing metadata: {e.Message}");
            }
        }

        if (backingDisk == null && resolvedBacking != null)
            SchedulePersistInferredBacking(ctx.assetPath, resolvedBacking);

        var snap = ScriptableObject.CreateInstance<SnapshotAsset>();
        snap.projectRelativeUqsnapPath = ctx.assetPath.Replace('\\', '/');
        snap.backingDisk = resolvedBacking;
        snap.note = note ?? "";
        snap.createdAt = createdAt ?? "";
        snap.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

        ctx.AddObjectToAsset("main", snap);
        ctx.SetMainObject(snap);
    }

    static void SchedulePersistInferredBacking(string imageAssetPath, DiskAsset inferredBacking)
    {
        string backingAssetPath = AssetDatabase.GetAssetPath(inferredBacking);
        if (string.IsNullOrEmpty(backingAssetPath))
            return;

        EditorApplication.delayCall += () =>
        {
            var importer = AssetImporter.GetAtPath(imageAssetPath) as UqsnapImporter;
            DiskAsset backing =
                AssetDatabase.LoadAssetAtPath<DiskAsset>(backingAssetPath);
            if (importer == null || backing == null || importer.backingDisk != null)
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
}
