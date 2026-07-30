using System.Collections.Generic;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Optional per-scene list of guest assets to force into player builds even when
/// nothing in the scene references them. At most one should be active in a build scene.
/// </summary>
[ExecuteAlways]
public class QemuExtraBuildAssets : MonoBehaviour
{
    [Tooltip(
        "Extra DiskAsset / UqsnapAsset / CdRomAsset / FloppyAsset references to include even if no " +
        "scene component references them.")]
    public List<ScriptableObject> extraAssets = new List<ScriptableObject>();
}
}
