using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.uqsnap</c> as a <see cref="UqsnapAsset"/> (migration stream + disk ref + metadata).
/// Optional sibling <c>.png</c> (same basename) becomes <see cref="UqsnapAsset.screenshot"/> and the Project icon.
/// </summary>
[ScriptedImporter(4, "uqsnap")]
public class UqsnapImporter : ScriptedImporter
{
    [Tooltip("Disk tip this machine state belongs to.")]
    public DiskAsset disk;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported UqsnapAsset.")]
    public string note;

    [Tooltip("Launch config and version metadata.")]
    public UqsnapMetadata metadata;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        // Foo.uqsnap ↔ Foo.qcow2 — reimport when the sibling disk appears/changes.
        string qcow2Path = Path.ChangeExtension(ctx.assetPath, ".qcow2").Replace('\\', '/');
        ctx.DependsOnSourceAsset(qcow2Path);

        if (disk == null)
        {
            disk = AssetDatabase.LoadAssetAtPath<DiskAsset>(qcow2Path);
            if (disk != null)
                SchedulePersistDisk(ctx.assetPath, disk);
        }

        string pngPath = UqsnapAsset.SiblingScreenshotProjectPath(ctx.assetPath);
        Texture2D screenshot = null;
        if (!string.IsNullOrEmpty(pngPath))
        {
            // Reimport this .uqsnap when the sibling preview changes.
            ctx.DependsOnSourceAsset(pngPath);
            screenshot = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
        }

        var snap = ScriptableObject.CreateInstance<UqsnapAsset>();
        snap.projectRelativeUqsnapPath = ctx.assetPath.Replace('\\', '/');
        snap.disk = disk;
        snap.note = note ?? "";
        snap.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        snap.name = snap.label;
        snap.metadata = metadata != null ? metadata.Clone() : UqsnapMetadata.CreateEmpty();
        snap.screenshot = screenshot;

        ctx.AddObjectToAsset("main", snap);
        ctx.SetMainObject(snap);

        Texture2D icon = screenshot;
        if (icon == null)
        {
            icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/org.plunderludics.unityqemu/Editor/Icons/UqsnapAssetIcon.png");
        }
        if (icon != null)
            EditorGUIUtility.SetIconForObject(snap, icon);

        if (disk == null)
        {
            ctx.LogImportWarning(
                $"No DiskAsset linked and no sibling '{qcow2Path}'. " +
                "Assign Disk on the importer, or use UnityQemu → Repair Uqsnap Disk Links.");
        }
    }

    /// <summary>
    /// For each .uqsnap with a null/missing disk, link the sibling .qcow2 DiskAsset when present.
    /// </summary>
    [MenuItem("UnityQemu/Repair Uqsnap Disk Links")]
    public static void RepairAllDiskLinks()
    {
        int fixedCount = 0;
        int alreadyOk = 0;
        int missingSibling = 0;
        string[] guids = AssetDatabase.FindAssets("t:UqsnapAsset");
        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!UqsnapAsset.IsUqsnapAssetPath(path))
                    continue;
                EditorUtility.DisplayProgressBar(
                    "Repair Uqsnap Disk Links", path, (float)i / guids.Length);

                var snap = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(path);
                var importer = AssetImporter.GetAtPath(path) as UqsnapImporter;
                if (importer == null)
                    continue;

                DiskAsset current = snap != null ? snap.disk : importer.disk;
                if (current != null)
                {
                    alreadyOk++;
                    continue;
                }

                string qcow2Path = Path.ChangeExtension(path, ".qcow2").Replace('\\', '/');
                DiskAsset sibling = AssetDatabase.LoadAssetAtPath<DiskAsset>(qcow2Path);
                if (sibling == null)
                {
                    missingSibling++;
                    Debug.LogWarning(
                        $"UnityQemu: '{path}' has no disk and no sibling DiskAsset at '{qcow2Path}'");
                    continue;
                }

                importer.disk = sibling;
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(path);
                importer.SaveAndReimport();
                fixedCount++;
                Debug.Log($"UnityQemu linked disk for '{path}': '{qcow2Path}'");
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog(
            "Repair Uqsnap Disk Links",
            $"Fixed: {fixedCount}\nAlready linked: {alreadyOk}\nNo sibling .qcow2: {missingSibling}",
            "OK");
    }

    static void SchedulePersistDisk(string uqsnapPath, DiskAsset inferredDisk)
    {
        string diskPath = AssetDatabase.GetAssetPath(inferredDisk);
        if (string.IsNullOrEmpty(diskPath))
            return;

        EditorApplication.delayCall += () =>
        {
            var importer = AssetImporter.GetAtPath(uqsnapPath) as UqsnapImporter;
            DiskAsset loaded = AssetDatabase.LoadAssetAtPath<DiskAsset>(diskPath);
            if (importer == null || loaded == null)
                return;
            if (importer.disk == loaded)
                return;

            importer.disk = loaded;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(uqsnapPath);
            Debug.Log($"UnityQemu linked disk for '{uqsnapPath}': '{diskPath}'");
        };
    }
}

[CustomEditor(typeof(UqsnapImporter))]
public class UqsnapImporterEditor : ScriptedImporterEditor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("disk"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("note"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("metadata"), true);
        serializedObject.ApplyModifiedProperties();
        ApplyRevertGUI();
    }
}
}
