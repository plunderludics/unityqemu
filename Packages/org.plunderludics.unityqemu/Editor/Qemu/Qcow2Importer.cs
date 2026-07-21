using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.qcow2</c> as a <see cref="QemuDiskAsset"/> so disks/snapshots appear as native Unity assets.
/// The binary stays on disk; QEMU must not write imported files (use ephemeral work overlays).
/// </summary>
[ScriptedImporter(1, "qcow2")]
public class Qcow2Importer : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var disk = ScriptableObject.CreateInstance<QemuDiskAsset>();
        disk.projectRelativeQcow2Path = ctx.assetPath.Replace('\\', '/');
        disk.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        disk.name = disk.label;

        ctx.AddObjectToAsset("main", disk);
        ctx.SetMainObject(disk);
    }
}
}
