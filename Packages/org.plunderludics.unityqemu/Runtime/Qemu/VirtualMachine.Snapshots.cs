using System;
using System.IO;
using System.Threading.Tasks;

namespace UnityQemu {
/// <summary>
/// Durable snapshot tip save/load on a running guest.
/// Editor asset import path is in <see cref="DurableSnapshot"/>;
/// inspector entry points live on <see cref="SnapshotUI"/>.
/// </summary>
public partial class VirtualMachine
{
    /// <summary>Live session disk tip (<see cref="sessionCurrent"/>), or null.</summary>
    public DiskAsset SessionDiskTip =>
        _sessionCurrent != null ? _sessionCurrent.DiskTip : null;

    public bool CanSaveChildDurable =>
        QmpConnected && ActiveDiskAsset != null;

    public bool CanSaveSiblingDurable =>
        QmpConnected && SessionDiskTip != null && SessionDiskTip.backingDisk != null;

    public bool CanOverwriteDurable => CanSaveSiblingDurable;

    public bool SessionDiskTipIsFrozen =>
        SessionDiskTip != null && DiskAsset.HasChildDisks(SessionDiskTip);

#if UNITY_EDITOR
    /// <summary>
    /// Durable tip save: freeze work layer, optionally migrate to <paramref name="uqsnapProjectPath"/>,
    /// write thin <paramref name="qcow2ProjectPath"/>, import assets, update session tip.
    /// Pass null/empty uqsnap for a disk-only tip.
    /// </summary>
    public Task<BootableAsset> SaveDurableSnapshotAsync(
        string qcow2ProjectPath,
        string uqsnapProjectPath,
        DiskAsset immediateParent,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> progress = null) =>
        DurableSnapshot.SaveAsync(
            this, qcow2ProjectPath, uqsnapProjectPath, immediateParent,
            compressMachineState, captureScreenshot, progress);

    /// <summary>Stop, prepare, and start into <paramref name="snap"/>.</summary>
    public Task LoadDurableSnapshotAsync(UqsnapAsset snap) =>
        DurableSnapshot.LoadAsync(this, snap);

    /// <summary>Reload the in-session quick-save from the last durable capture.</summary>
    public Task ReloadDurableStateAsync() =>
        DurableSnapshot.ReloadSessionStateAsync(this);

    /// <summary>
    /// Save a child tip under <paramref name="parent"/> (defaults to session tip / active disk).
    /// Caller should detach hotplugged vvfat before a machine-state save.
    /// </summary>
    public Task<BootableAsset> SaveChildDurableAsync(
        string qcow2ProjectPath,
        string uqsnapProjectPath,
        DiskAsset parent = null,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> progress = null)
    {
        DiskAsset resolvedParent = parent ?? SessionDiskTip ?? ActiveDiskAsset;
        if (resolvedParent == null)
            throw new InvalidOperationException("No current disk tip to parent under");
        return SaveDurableSnapshotAsync(
            qcow2ProjectPath, uqsnapProjectPath, resolvedParent,
            compressMachineState, captureScreenshot, progress);
    }

    /// <summary>
    /// Save a sibling tip under the current tip's backing disk.
    /// Caller should detach hotplugged vvfat before a machine-state save.
    /// </summary>
    public Task<BootableAsset> SaveSiblingDurableAsync(
        string qcow2ProjectPath,
        string uqsnapProjectPath,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> progress = null)
    {
        DiskAsset tip = SessionDiskTip;
        if (tip == null || tip.backingDisk == null)
            throw new InvalidOperationException(
                "Save sibling needs a current tip whose disk has a parent");
        return SaveDurableSnapshotAsync(
            qcow2ProjectPath, uqsnapProjectPath, tip.backingDisk,
            compressMachineState, captureScreenshot, progress);
    }

    /// <summary>
    /// Overwrite the current session tip in place (same qcow2 path, same parent).
    /// Caller should confirm when <see cref="SessionDiskTipIsFrozen"/> and detach
    /// hotplugged vvfat before a machine-state save.
    /// </summary>
    public Task<BootableAsset> OverwriteCurrentDurableAsync(
        bool compressMachineState = true,
        bool captureScreenshot = true,
        bool includeMachineState = true,
        Action<string> progress = null)
    {
        DiskAsset tip = SessionDiskTip;
        if (tip == null || tip.backingDisk == null)
            throw new InvalidOperationException(
                "Overwrite needs a current tip whose disk has a parent");

        string existingDisk = tip.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(existingDisk))
        {
            string tipLabel = _sessionCurrent != null
                ? _sessionCurrent.DisplayLabel
                : tip.DisplayLabel;
            throw new InvalidOperationException(
                $"Tip '{tipLabel}' has no linked disk file");
        }

        string diskProjectPath = DurableSnapshot.MakeProjectRelative(existingDisk);
        string uqsnapProjectPath = includeMachineState
            ? Path.ChangeExtension(diskProjectPath, ".uqsnap")
            : null;
        return SaveDurableSnapshotAsync(
            diskProjectPath, uqsnapProjectPath, tip.backingDisk,
            compressMachineState, captureScreenshot, progress);
    }
#endif
}
}
