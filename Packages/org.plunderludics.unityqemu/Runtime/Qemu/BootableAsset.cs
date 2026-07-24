using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Shared base for bootable guest assets: plain disks and durable snapshots.
/// Concrete types stay separate so object pickers / importers only accept the right kind;
/// this base lets session UI show either as one readonly field.
/// </summary>
public abstract class BootableAsset : ScriptableObject
{
    [Tooltip("Display name (defaults to asset name)")]
    public string label;

    [TextArea(2, 4)]
    [Tooltip("Freeform annotation for this asset.")]
    public string note;

    public string DisplayLabel =>
        !string.IsNullOrEmpty(label) ? label : name;

    /// <summary>Disk tip used for -hda / overlays (self for disks; linked tip for snapshots).</summary>
    public abstract DiskAsset DiskTip { get; }

    protected virtual void OnValidate()
    {
        if (string.IsNullOrEmpty(label))
            label = name;
    }
}
}
