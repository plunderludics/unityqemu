using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.uqsnap</c> as a <see cref="UqsnapAsset"/> (migration stream + disk ref + metadata).
/// Optional sibling <c>.png</c> (same basename) becomes <see cref="UqsnapAsset.screenshot"/> and the Project icon.
/// </summary>
[ScriptedImporter(5, "uqsnap")]
public class UqsnapImporter : ScriptedImporter
{
    [Tooltip("Disk tip this machine state belongs to.")]
    public DiskAsset disk;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported UqsnapAsset.")]
    public string note;

    [Tooltip("Launch config and version metadata.")]
    public UqsnapMetadata metadata;

    /// <summary>
    /// Prefer importing the sibling <c>.qcow2</c> before this <c>.uqsnap</c>, and reimport
    /// when that disk's import result changes. Must not call AssetDatabase APIs.
    /// </summary>
    public static string[] GatherDependenciesFromSourceFile(string assetPath)
    {
        string qcow2Path = Path.ChangeExtension(assetPath, ".qcow2").Replace('\\', '/');
        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", qcow2Path));
        if (!File.Exists(full))
            return System.Array.Empty<string>();
        return new[] { qcow2Path };
    }

    public override void OnImportAsset(AssetImportContext ctx)
    {
        // Foo.uqsnap ↔ Foo.qcow2 — artifact dep so we reimport when the DiskAsset appears
        // (DependsOnSourceAsset alone does not fire on the sibling's first import).
        string qcow2Path = Path.ChangeExtension(ctx.assetPath, ".qcow2").Replace('\\', '/');
        ctx.DependsOnArtifact(qcow2Path);

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
            ctx.DependsOnArtifact(pngPath);
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
            string fullQcow2 = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", qcow2Path));
            if (File.Exists(fullQcow2))
            {
                ctx.LogImportWarning(
                    $"Sibling '{qcow2Path}' has no DiskAsset yet; " +
                    "will retry when that asset finishes importing.");
            }
            else
            {
                ctx.LogImportWarning(
                    $"No DiskAsset linked and no sibling file at '{qcow2Path}'. " +
                    "Assign Disk on the importer, or add a matching .qcow2 beside this .uqsnap.");
            }
        }
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
