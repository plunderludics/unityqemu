using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// Imports <c>.img</c>/<c>.ima</c> as a <see cref="FloppyAsset"/> so floppy images appear as
/// typed Unity assets. The binary stays on disk; UnityQemu only reads the path for QEMU
/// <c>-drive if=floppy</c>.
/// </summary>
[ScriptedImporter(1, new[] { "img", "ima" })]
public class FloppyImporter : ScriptedImporter
{
    [TextArea(2, 4)]
    [Tooltip("Freeform annotation stored on the imported FloppyAsset.")]
    public string note;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        var floppy = ScriptableObject.CreateInstance<FloppyAsset>();
        floppy.projectRelativeImgPath = ctx.assetPath.Replace('\\', '/');
        floppy.note = note ?? "";
        floppy.label = Path.GetFileNameWithoutExtension(ctx.assetPath);
        floppy.name = floppy.label;

        ctx.AddObjectToAsset("main", floppy);
        ctx.SetMainObject(floppy);

        Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Packages/org.plunderludics.unityqemu/Editor/Icons/FloppyAssetIcon.png");
        if (icon != null)
            EditorGUIUtility.SetIconForObject(floppy, icon);
    }
}
}
