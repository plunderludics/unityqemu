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
/// Inspector UI for durable snapshots. Functionality lives on
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
        "On by default. Uses migrate fd: (Windows get-win32-socket; macOS/Linux getfd). " +
        "Turn off to save a cold-bootable child/sibling DiskAsset only. " +
        "Attached USB vvfat drives are disconnected automatically (with confirmation) before capture.")]
    [LabelText("Include machine state")]
    [EnableIf(nameof(CanIncludeMachineState))]
    public bool includeMachineState = true;

    bool CanIncludeMachineState => MigrationRelay.SupportsOutgoingFdCapture;

    bool EffectiveIncludeMachineState =>
        includeMachineState && MigrationRelay.SupportsOutgoingFdCapture;

    [PropertyOrder(0)]
    [Tooltip(
        "Gzip the machine-state file when saving. On by default (smaller files). " +
        "Turn off for faster saves; the choice is stored with each snapshot so load stays correct.")]
    [LabelText("Compress machine state")]
    [EnableIf(nameof(EffectiveIncludeMachineState))]
    public bool compressMachineState = true;

    [PropertyOrder(0)]
    [Tooltip(
        "Write a sibling .png (same basename as the .uqsnap) from the live VNC frame when saving. " +
        "Used as UqsnapAsset.screenshot and the Project window icon.")]
    [LabelText("Capture screenshot")]
    [EnableIf(nameof(EffectiveIncludeMachineState))]
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

    string DefaultSaveFolder => DefaultSaveFolderFor(virtualMachine);

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

    public Task<bool> OverwriteCurrentSnapshotAsync() =>
        OverwriteAsync(
            virtualMachine, EffectiveIncludeMachineState, compressMachineState, captureScreenshot,
            s => status = s);

    public Task<bool> SaveChildSnapshotAsync() =>
        SaveChildAsync(
            virtualMachine, EffectiveIncludeMachineState, compressMachineState, captureScreenshot,
            s => status = s);

    public Task<bool> SaveSiblingSnapshotAsync() =>
        SaveSiblingAsync(
            virtualMachine, EffectiveIncludeMachineState, compressMachineState, captureScreenshot,
            s => status = s);

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save child")]
    [EnableIf(nameof(CanSaveChild))]
    public async void SaveChildSnapshotButton() =>
        await SaveChildSnapshotAsync();

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save sibling")]
    [EnableIf(nameof(CanSaveSibling))]
    public async void SaveSiblingSnapshotButton() =>
        await SaveSiblingSnapshotAsync();

    /// <summary>Editor UX for overwrite — works without a SnapshotUI component.</summary>
    public static async Task<bool> OverwriteAsync(
        VirtualMachine vm,
        bool includeMachineState = true,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> onStatus = null)
    {
        void SetStatus(string s) => onStatus?.Invoke(s);
        try
        {
            if (vm == null)
            {
                SetStatus("No VirtualMachine");
                return false;
            }

            DiskAsset tip = vm.SessionDiskTip;
            if (tip == null || tip.backingDisk == null)
            {
                SetStatus("Overwrite needs a current tip whose disk has a parent");
                return false;
            }

            string tipLabel = vm.sessionCurrent != null
                ? vm.sessionCurrent.DisplayLabel
                : tip.DisplayLabel;
            if (vm.SessionDiskTipIsFrozen &&
                !ConfirmOverwriteFrozenTip(tip, tipLabel))
            {
                SetStatus("Overwrite cancelled");
                return false;
            }

            if (!await ConfirmAndDetachVvfatAsync(vm, includeMachineState, onStatus))
            {
                SetStatus("Save cancelled");
                return false;
            }

            SetStatus($"Overwriting '{tipLabel}'…");
            var asset = await vm.OverwriteCurrentDurableAsync(
                compressMachineState, captureScreenshot, includeMachineState, SetStatus);
            if (asset != null)
                SetStatus($"Overwrote '{asset.name}'");
            return true;
        }
        catch (Exception e)
        {
            SetStatus($"Overwrite failed: {e.Message}");
            Debug.LogException(e);
            return false;
        }
    }

    /// <summary>Editor UX for save-child — works without a SnapshotUI component.</summary>
    public static Task<bool> SaveChildAsync(
        VirtualMachine vm,
        bool includeMachineState = true,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> onStatus = null)
    {
        DiskAsset parent = vm != null ? vm.SessionDiskTip ?? vm.ActiveDiskAsset : null;
        if (parent == null)
        {
            onStatus?.Invoke(
                vm == null ? "No VirtualMachine" : "No current disk tip to parent under");
            return Task.FromResult(false);
        }

        return PromptAndSaveNewTipAsync(
            vm,
            parent,
            includeMachineState ? "Save Child Snapshot" : "Save Child Disk",
            includeMachineState
                ? $"Child of '{parent.name}' — disk tip + machine state."
                : $"Child of '{parent.name}' — disk tip only (cold boot).",
            includeMachineState,
            onStatus,
            (qcow2, uqsnap, progress) => vm.SaveChildDurableAsync(
                qcow2, uqsnap, parent, compressMachineState, captureScreenshot, progress),
            asset => $"Saved child '{asset.name}' → parent '{parent.name}'",
            "Save child failed");
    }

    /// <summary>Editor UX for save-sibling — works without a SnapshotUI component.</summary>
    public static Task<bool> SaveSiblingAsync(
        VirtualMachine vm,
        bool includeMachineState = true,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> onStatus = null)
    {
        DiskAsset tip = vm != null ? vm.SessionDiskTip : null;
        if (tip == null || tip.backingDisk == null)
        {
            onStatus?.Invoke(
                vm == null
                    ? "No VirtualMachine"
                    : "Save sibling needs a current snapshot whose disk has a parent");
            return Task.FromResult(false);
        }

        DiskAsset parent = tip.backingDisk;
        string tipLabel = vm.sessionCurrent != null ? vm.sessionCurrent.DisplayLabel : tip.name;
        return PromptAndSaveNewTipAsync(
            vm,
            parent,
            includeMachineState ? "Save Sibling Snapshot" : "Save Sibling Disk",
            includeMachineState
                ? $"Same parent as '{tipLabel}' ({parent.name})."
                : $"Same parent as '{tipLabel}' ({parent.name}) — disk tip only.",
            includeMachineState,
            onStatus,
            (qcow2, uqsnap, progress) => vm.SaveSiblingDurableAsync(
                qcow2, uqsnap, compressMachineState, captureScreenshot, progress),
            asset => $"Saved sibling '{asset.name}' (parent: {parent.name})",
            "Save sibling failed");
    }

    static async Task<bool> PromptAndSaveNewTipAsync(
        VirtualMachine vm,
        DiskAsset parent,
        string panelTitle,
        string panelMessage,
        bool includeMachineState,
        Action<string> onStatus,
        Func<string, string, Action<string>, Task<BootableAsset>> save,
        Func<BootableAsset, string> successStatus,
        string failurePrefix)
    {
        void SetStatus(string s) => onStatus?.Invoke(s);

        string folder = DefaultSaveFolderFor(vm);
        EnsureSnapshotFolder(folder);
        string extension = includeMachineState ? "uqsnap" : "qcow2";
        string projectPath = EditorUtility.SaveFilePanelInProject(
            panelTitle,
            DefaultNewSnapshotName,
            extension,
            panelMessage,
            folder);
        if (string.IsNullOrEmpty(projectPath))
            return false;

        try
        {
            if (!await ConfirmAndDetachVvfatAsync(vm, includeMachineState, onStatus))
            {
                SetStatus("Save cancelled");
                return false;
            }

            SetStatus($"Saving under '{parent.name}'…");
            string qcow2Path = Path.ChangeExtension(projectPath, ".qcow2");
            string uqsnapPath = includeMachineState
                ? Path.ChangeExtension(projectPath, ".uqsnap")
                : null;
            var asset = await save(qcow2Path, uqsnapPath, SetStatus);
            if (asset != null)
                SetStatus(successStatus(asset));
            return true;
        }
        catch (Exception e)
        {
            SetStatus($"{failurePrefix}: {e.Message}");
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
            UqsnapAsset snap = FindUqsnapAsset(absolutePath);
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

    static UqsnapAsset FindUqsnapAsset(string absolutePath)
    {
        string projectPath = DurableSnapshot.MakeProjectRelative(absolutePath);
        UqsnapAsset snap = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(projectPath);
        if (snap != null)
            return snap;

        // OpenFilePanel may return a junction target path.
        foreach (string guid in AssetDatabase.FindAssets("t:UqsnapAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            var candidate = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(p);
            if (candidate == null)
                continue;
            string fs = candidate.GetMachineStateFilesystemPath();
            if (!string.IsNullOrEmpty(fs) && DiskOverlay.PathsEqual(fs, absolutePath))
                return candidate;
        }

        return null;
    }

    static bool ConfirmOverwriteFrozenTip(DiskAsset tip, string tipLabel)
    {
        string[] childNames = DiskAsset.GetChildDiskNames(tip);
        string childList = childNames.Length <= 8
            ? string.Join(", ", childNames)
            : string.Join(", ", childNames, 0, 8) + $" (+{childNames.Length - 8} more)";
        return EditorUtility.DisplayDialog(
            "Overwrite frozen tip?",
            $"'{tipLabel}'s disk is frozen — {childNames.Length} disk(s) use it as parent:\n\n" +
            $"{childList}\n\nOverwrite anyway?",
            "Overwrite anyway",
            "Cancel");
    }

    /// <summary>
    /// Writable USB vvfat blocks migration capture. Confirm, then detach all session
    /// hotplugs before saving machine state.
    /// </summary>
    public static async Task<bool> ConfirmAndDetachVvfatAsync(
        VirtualMachine vm, bool includeMachineState, Action<string> onStatus = null)
    {
        if (vm == null || !includeMachineState)
            return true;

        IReadOnlyList<string> folders = await vm.GetHotpluggedVvfatFolderPathsAsync();
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

        if (!EditorUtility.DisplayDialog(
                "Disconnect vvfat for save?",
                message.ToString(),
                "Save anyway",
                "Cancel"))
            return false;

        onStatus?.Invoke("Disconnecting vvfat…");
        await vm.DetachAllVvfatDrivesAsync();
        return true;
    }

    static string DefaultSaveFolderFor(VirtualMachine vm)
    {
        if (vm == null)
            return "Assets";
        string folder = FolderOfAsset(vm.sessionCurrent);
        if (string.IsNullOrEmpty(folder))
            folder = FolderOfAsset(vm.SessionDiskTip);
        if (string.IsNullOrEmpty(folder))
            folder = FolderOfAsset(vm.ActiveDiskAsset);
        return string.IsNullOrEmpty(folder) ? "Assets" : folder;
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

    bool CanOverwrite => virtualMachine != null && virtualMachine.CanOverwriteDurable;
    bool HasVirtualMachine => virtualMachine != null;
    bool Ready => virtualMachine != null && virtualMachine.QmpConnected;
    bool CanSaveChild => virtualMachine != null && virtualMachine.CanSaveChildDurable;
    bool CanSaveSibling => virtualMachine != null && virtualMachine.CanSaveSiblingDurable;
    bool CurrentSnapshotIsFrozen =>
        virtualMachine != null && virtualMachine.SessionDiskTipIsFrozen;

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
#else
    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";
#endif
}
}
