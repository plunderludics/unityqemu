using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.iso</c> as a <see cref="CdRomAsset"/> so CD images appear as typed Unity assets.
/// The binary stays on disk; UnityQemu only reads the path for QEMU <c>-drive media=cdrom</c>.
/// </summary>
[ScriptedImporter(1, "iso")]
public class IsoImporter : ScriptedImporter
{
    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported CdRomAsset.")]
    public string note;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        var cdrom = ScriptableObject.CreateInstance<CdRomAsset>();
        cdrom.projectRelativeIsoPath = ctx.assetPath.Replace('\\', '/');
        cdrom.note = note ?? "";
        cdrom.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        cdrom.name = cdrom.label;

        ctx.AddObjectToAsset("main", cdrom);
        ctx.SetMainObject(cdrom);
    }
}
}
