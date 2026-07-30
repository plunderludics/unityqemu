using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Inspector UI for durable snapshots. Save/load work is on
/// <see cref="VirtualMachine"/> / <see cref="DurableSnapshot"/>;
/// this component only exposes options, confirms, and file dialogs.
/// Save sibling / Overwrite → parent = current.disk.backingDisk.
/// Save child → parent = current.disk.
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("snapshot")]
[DeclareHorizontalGroup("snapshot/actions")]
[DeclareHorizontalGroup("save")]
[DeclareFoldoutGroup("Debug", Expanded = false)]
public class SnapshotUI : MonoBehaviour
{
    const string DefaultNewSnapshotName = "snap1";

    [PropertyOrder(0)]
    public VirtualMachine virtualMachine;

    [PropertyOrder(0)]
    [Tooltip(
        "Also write a .uqsnap migration stream (RAM/CPU) next to the disk tip. " +
        "On by default. Turn off to save a cold-bootable child/sibling DiskAsset only. " +
        "Attached USB vvfat drives are disconnected automatically (with confirmation) before capture.")]
    [LabelText("Include machine state")]
    public bool includeMachineState = true;

    [PropertyOrder(0)]
    [Tooltip(
        "Gzip the machine-state file when saving. On by default (smaller files). " +
        "Turn off for faster saves; the choice is stored with each snapshot so load stays correct.")]
    [LabelText("Compress machine state")]
    [EnableIf(nameof(includeMachineState))]
    public bool compressMachineState = true;

    [PropertyOrder(0)]
    [Tooltip(
        "Write a sibling .png (same basename as the .uqsnap) from the live VNC frame when saving. " +
        "Used as UqsnapAsset.screenshot and the Project window icon.")]
    [LabelText("Capture screenshot")]
    [EnableIf(nameof(includeMachineState))]
    public bool captureScreenshot = true;

    [PropertyTooltip("$CurrentSnapshotTooltip")]
    [PropertyOrder(1)]
    [Group("snapshot")]
    [ShowInInspector, ReadOnly]
    [LabelText("$CurrentSnapshotLabel")]
    public BootableAsset sessionCurrent =>
        virtualMachine != null ? virtualMachine.sessionCurrent : null;

    /// <summary>Session tip when it is a uqsnap; null if session is a plain disk or unset.</summary>
    public UqsnapAsset currentSnapshot => sessionCurrent as UqsnapAsset;

#if UNITY_EDITOR
    string CurrentSnapshotLabel =>
        CurrentSnapshotIsFrozen ? "Session Current ❄" : "Session Current";

    string CurrentSnapshotTooltip
    {
        get
        {
            const string baseText =
                "Live tip for this QEMU session (VirtualMachine.sessionCurrent). " +
                "Load / Save update this; the VM Snapshot / Disk slots are boot config only.";
            if (!CurrentSnapshotIsFrozen)
                return baseText;
            return baseText +
                   "\n\n❄ means other disks use this tip's disk as their parent — " +
                   "overwriting it can corrupt those children. Prefer Save sibling or Save child.";
        }
    }

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    DiskAsset CurrentDiskTip =>
        sessionCurrent != null ? sessionCurrent.DiskTip : null;

    /// <summary>Disk tip for the live session (for editor shortcuts / dialogs).</summary>
    public DiskAsset SessionDiskTip => CurrentDiskTip;

    public bool CanOverwriteForShortcut => CanOverwrite;
    public bool CanSaveChildForShortcut => CanSaveChild;
    public bool CanSaveSiblingForShortcut => CanSaveSibling;

    string DefaultSaveFolder
    {
        get
        {
            string folder = FolderOfAsset(currentSnapshot);
            if (string.IsNullOrEmpty(folder))
                folder = FolderOfAsset(CurrentDiskTip);
            if (string.IsNullOrEmpty(folder) && virtualMachine != null)
                folder = FolderOfAsset(virtualMachine.ActiveDiskAsset);
            return string.IsNullOrEmpty(folder) ? "Assets" : folder;
        }
    }

    static string FolderOfAsset(UnityEngine.Object asset)
    {
        if (asset == null)
            return null;
        string path = AssetDatabase.GetAssetPath(asset);
        if (string.IsNullOrEmpty(path))
        {
            if (asset is UqsnapAsset snap &&
                !string.IsNullOrEmpty(snap.projectRelativeUqsnapPath))
                path = snap.projectRelativeUqsnapPath;
            else if (asset is DiskAsset disk &&
                     !string.IsNullOrEmpty(disk.projectRelativeQcow2Path))
                path = disk.projectRelativeQcow2Path;
        }
        if (string.IsNullOrEmpty(path))
            return null;
        string dir = Path.GetDirectoryName(path.Replace('\\', '/'));
        return string.IsNullOrEmpty(dir) ? null : dir.Replace('\\', '/');
    }

    [PropertyOrder(2)]
    [Group("snapshot/actions")]
    [Button("Reload state")]
    [EnableIf(nameof(Ready))]
    public async void ReloadDurableStateButton()
    {
        try
        {
            status = "Reloading…";
            await virtualMachine.ReloadDurableStateAsync();
            status = "Reloaded session state";
        }
        catch (Exception e)
        {
            status = $"Reload failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [PropertyOrder(2)]
    [Group("snapshot/actions")]
    [GUIColor("$OverwriteButtonColor")]
    [Button("Overwrite")]
    [EnableIf(nameof(CanOverwrite))]
    public async void OverwriteCurrentSnapshotButton() =>
        await OverwriteCurrentSnapshotAsync();

    Color OverwriteButtonColor =>
        CurrentSnapshotIsFrozen
            ? new Color(1f, 0.55f, 0.55f)
            : new Color(1f, 0.92f, 0.55f);

    /// <summary>
    /// Overwrite the current session tip in place (uqsnap or plain child qcow2).
    /// Returns false if the user cancelled a confirmation dialog (guest left as-is).
    /// </summary>
    public async Task<bool> OverwriteCurrentSnapshotAsync()
    {
        try
        {
            DiskAsset tip = CurrentDiskTip;
            if (tip == null || tip.backingDisk == null)
            {
                status = "Overwrite needs a current tip whose disk has a parent";
                return false;
            }

            string tipLabel = sessionCurrent != null ? sessionCurrent.DisplayLabel : tip.DisplayLabel;
            if (DiskAsset.HasChildDisks(tip))
            {
                string[] childNames = DiskAsset.GetChildDiskNames(tip);
                string childList = childNames.Length <= 8
                    ? string.Join(", ", childNames)
                    : string.Join(", ", childNames, 0, 8) + $" (+{childNames.Length - 8} more)";
                bool proceed = EditorUtility.DisplayDialog(
                    "Overwrite frozen tip?",
                    $"'{tipLabel}'s disk is frozen — {childNames.Length} disk(s) use it as parent:\n\n" +
                    $"{childList}\n\nOverwrite anyway?",
                    "Overwrite anyway",
                    "Cancel");
                if (!proceed)
                {
                    status = "Overwrite cancelled";
                    return false;
                }
            }

            string existingDisk = tip.GetQcow2FilesystemPath();
            if (string.IsNullOrEmpty(existingDisk))
                throw new InvalidOperationException(
                    $"Tip '{tipLabel}' has no linked disk file");

            status = $"Overwriting '{tipLabel}'…";
            string diskProjectPath = DurableSnapshot.MakeProjectRelative(existingDisk);
            string uqsnapProjectPath = includeMachineState
                ? Path.ChangeExtension(diskProjectPath, ".uqsnap")
                : null;
            var asset = await SaveViaVmAsync(diskProjectPath, uqsnapProjectPath, tip.backingDisk);
            if (asset != null)
                status = $"Overwrote '{asset.name}'";
            return true;
        }
        catch (Exception e)
        {
            status = $"Overwrite failed: {e.Message}";
            Debug.LogException(e);
            return false;
        }
    }

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save child")]
    [EnableIf(nameof(CanSaveChild))]
    public async void SaveChildSnapshotButton() =>
        await SaveChildSnapshotAsync();

    /// <summary>
    /// Save a child tip. Returns false if the file panel was cancelled or save failed.
    /// </summary>
    public async Task<bool> SaveChildSnapshotAsync()
    {
        DiskAsset parent = CurrentDiskTip ?? virtualMachine.ActiveDiskAsset;
        if (parent == null)
        {
            status = "No current disk tip to parent under";
            return false;
        }

        EnsureSnapshotFolder(DefaultSaveFolder);
        string extension = includeMachineState ? "uqsnap" : "qcow2";
        string projectPath = EditorUtility.SaveFilePanelInProject(
            includeMachineState ? "Save Child Snapshot" : "Save Child Disk",
            DefaultNewSnapshotName,
            extension,
            includeMachineState
                ? $"Child of '{parent.name}' — disk tip + machine state."
                : $"Child of '{parent.name}' — disk tip only (cold boot).",
            DefaultSaveFolder);
        if (string.IsNullOrEmpty(projectPath))
            return false;

        try
        {
            status = $"Saving child of '{parent.name}'…";
            string qcow2Path = Path.ChangeExtension(projectPath, ".qcow2");
            string uqsnapPath = includeMachineState
                ? Path.ChangeExtension(projectPath, ".uqsnap")
                : null;
            var asset = await SaveViaVmAsync(qcow2Path, uqsnapPath, parent);
            if (asset != null)
                status = $"Saved child '{asset.name}' → parent '{parent.name}'";
            return true;
        }
        catch (Exception e)
        {
            status = $"Save child failed: {e.Message}";
            Debug.LogException(e);
            return false;
        }
    }

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save sibling")]
    [EnableIf(nameof(CanSaveSibling))]
    public async void SaveSiblingSnapshotButton() =>
        await SaveSiblingSnapshotAsync();

    /// <summary>
    /// Save a sibling tip. Returns false if cancelled, unavailable, or save failed.
    /// </summary>
    public async Task<bool> SaveSiblingSnapshotAsync()
    {
        DiskAsset tip = CurrentDiskTip;
        if (tip == null || tip.backingDisk == null)
        {
            status = "Save sibling needs a current snapshot whose disk has a parent";
            return false;
        }

        DiskAsset parent = tip.backingDisk;
        EnsureSnapshotFolder(DefaultSaveFolder);
        string extension = includeMachineState ? "uqsnap" : "qcow2";
        string projectPath = EditorUtility.SaveFilePanelInProject(
            includeMachineState ? "Save Sibling Snapshot" : "Save Sibling Disk",
            DefaultNewSnapshotName,
            extension,
            includeMachineState
                ? $"Same parent as '{(sessionCurrent != null ? sessionCurrent.DisplayLabel : tip.name)}' ({parent.name})."
                : $"Same parent as '{(sessionCurrent != null ? sessionCurrent.DisplayLabel : tip.name)}' ({parent.name}) — disk tip only.",
            DefaultSaveFolder);
        if (string.IsNullOrEmpty(projectPath))
            return false;

        try
        {
            status = "Saving sibling…";
            string qcow2Path = Path.ChangeExtension(projectPath, ".qcow2");
            string uqsnapPath = includeMachineState
                ? Path.ChangeExtension(projectPath, ".uqsnap")
                : null;
            var asset = await SaveViaVmAsync(qcow2Path, uqsnapPath, parent);
            if (asset != null)
                status = $"Saved sibling '{asset.name}' (parent: {parent.name})";
            return true;
        }
        catch (Exception e)
        {
            status = $"Save sibling failed: {e.Message}";
            Debug.LogException(e);
            return false;
        }
    }

    [PropertyOrder(4)]
    [Button("Load snapshot…")]
    [EnableIf(nameof(HasVirtualMachine))]
    public async void LoadOtherStateButton()
    {
        EnsureSnapshotFolder(DefaultSaveFolder);
        string absolutePath = EditorUtility.OpenFilePanel(
            "Load snapshot",
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", DefaultSaveFolder)),
            "uqsnap");
        if (string.IsNullOrEmpty(absolutePath))
            return;

        try
        {
            string projectPath = DurableSnapshot.MakeProjectRelative(absolutePath);
            UqsnapAsset snap = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(projectPath);
            if (snap == null)
            {
                // OpenFilePanel may return a junction target path.
                foreach (string guid in AssetDatabase.FindAssets("t:UqsnapAsset"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    var candidate = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(p);
                    if (candidate == null)
                        continue;
                    string fs = candidate.GetMachineStateFilesystemPath();
                    if (!string.IsNullOrEmpty(fs) && DiskOverlay.PathsEqual(fs, absolutePath))
                    {
                        snap = candidate;
                        break;
                    }
                }
            }
            if (snap == null)
                throw new InvalidOperationException(
                    $"No snapshot asset found for '{absolutePath}'.");

            status = $"Loading '{snap.name}'…";
            await virtualMachine.LoadDurableSnapshotAsync(snap);
            if (!string.IsNullOrEmpty(virtualMachine.LastStateRestoreError))
            {
                status = $"Loaded '{snap.name}' (disk only — state restore failed)";
                return;
            }
            status = $"Loaded '{snap.name}'";
        }
        catch (Exception e)
        {
            status = $"Load failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    async Task<BootableAsset> SaveViaVmAsync(
        string qcow2ProjectPath, string uqsnapProjectPath, DiskAsset parent)
    {
        if (!string.IsNullOrEmpty(uqsnapProjectPath) &&
            !await ConfirmAndDetachVvfatForMachineStateSaveAsync())
        {
            status = "Save cancelled";
            return null;
        }

        return await virtualMachine.SaveDurableSnapshotAsync(
            qcow2ProjectPath,
            uqsnapProjectPath,
            parent,
            compressMachineState,
            captureScreenshot,
            s => status = s);
    }

    /// <summary>
    /// Writable USB vvfat blocks migration capture. Confirm, then detach all session
    /// hotplugs before saving machine state.
    /// </summary>
    async Task<bool> ConfirmAndDetachVvfatForMachineStateSaveAsync()
    {
        if (virtualMachine == null)
            return true;

        PeripheralsUI peripherals = virtualMachine.GetComponent<PeripheralsUI>();
        if (peripherals == null)
            return true;

        IReadOnlyList<string> folders = await peripherals.GetHotpluggedVvfatFolderPathsAsync();
        if (folders.Count == 0)
            return true;

        var message = new StringBuilder();
        message.AppendLine(
            folders.Count == 1
                ? "Saving machine state will automatically disconnect this USB vvfat drive:"
                : $"Saving machine state will automatically disconnect these {folders.Count} USB vvfat drives:");
        message.AppendLine();
        foreach (string folder in folders)
            message.AppendLine($"• {FormatVvfatFolderForDialog(folder)}");
        message.AppendLine();
        message.Append(
            "vvfat shares are session-only and are not included in snapshots. Continue?");

        bool proceed = EditorUtility.DisplayDialog(
            "Disconnect vvfat for save?",
            message.ToString(),
            "Save anyway",
            "Cancel");
        if (!proceed)
            return false;

        status = "Disconnecting vvfat…";
        await peripherals.DetachAllVvfatDrivesAsync();
        return true;
    }

    static string FormatVvfatFolderForDialog(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            return "(unknown folder)";

        string projectRelative = DurableSnapshot.MakeProjectRelative(folderPath);
        if (!string.IsNullOrEmpty(projectRelative) &&
            projectRelative.StartsWith("Assets/", StringComparison.Ordinal))
            return projectRelative;

        return folderPath.Replace('\\', '/');
    }

    // Same tip geometry as sibling (parent required for convert -B); works for
    // uqsnap or plain DiskAsset tips. Base disks stay disabled — use Save child.
    bool CanOverwrite => CanSaveSibling;
    bool HasVirtualMachine => virtualMachine != null;
    bool Ready => virtualMachine != null && virtualMachine.QmpConnected;
    bool CanSaveChild => Ready && virtualMachine.ActiveDiskAsset != null;
    bool CanSaveSibling =>
        Ready && CurrentDiskTip != null && CurrentDiskTip.backingDisk != null;
    bool CurrentSnapshotIsFrozen =>
        CurrentDiskTip != null && DiskAsset.HasChildDisks(CurrentDiskTip);

    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    [LabelText("Status")]
    string status = "Idle";

    [PropertyOrder(101)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    [LabelText("Connected")]
    bool QmpReady => Ready;

    [PropertyOrder(102)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    [LabelText("Work image")]
    string WorkOverlay => virtualMachine != null ? virtualMachine.WorkOverlayPath : "";

    [PropertyOrder(103)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    [LabelText("Boot config")]
    string BootDiskName
    {
        get
        {
            if (virtualMachine == null)
                return "(none)";
            if (virtualMachine.snapshot != null)
                return virtualMachine.snapshot.name + " (config)";
            if (virtualMachine.diskAsset != null)
                return virtualMachine.diskAsset.name + " (config disk)";
            return "(none)";
        }
    }

    [PropertyOrder(104)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    [LabelText("Session")]
    string SessionDiskName =>
        sessionCurrent != null ? sessionCurrent.DisplayLabel : "(none)";

    static void EnsureSnapshotFolder(string projectFolder)
    {
        string folder = projectFolder.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folder))
            return;

        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
#else
    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";
#endif
}
}
