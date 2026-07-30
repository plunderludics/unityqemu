using System.Collections.Generic;
using TriInspector;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Optional per-scene build options for UnityQemu player packaging.
/// At most one should be active in a build scene.
/// </summary>
[ExecuteAlways]
public class QemuBuildSettings : MonoBehaviour
{
    [Tooltip(
        "When off (default), copy the entire qemu~ tree into the player build (safe, large). " +
        "When on, copy only files listed in the package's qemu-i386.manifest " +
        "(~120 MB: qemu-system-i386 + qemu-img + DLL closure + SeaBIOS PC firmware).")]
    public bool trimQemuToI386 = false;

    [Tooltip(
        "Extra DiskAsset / UqsnapAsset / CdRomAsset / FloppyAsset references to include even if no " +
        "scene component references them.")]
    public List<ScriptableObject> extraAssets = new List<ScriptableObject>();

#if UNITY_EDITOR
    [ShowInInspector, ReadOnly]
    [LabelText("Manifest")]
    string ManifestHint =>
        trimQemuToI386
            ? "Will package qemu-i386.manifest only"
            : "Will package full qemu~ (default)";
#endif
}
}
