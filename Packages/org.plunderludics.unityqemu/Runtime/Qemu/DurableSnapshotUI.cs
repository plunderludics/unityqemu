using System;
using System.IO;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable snapshots (D4): thin qcow2 disk diff (<c>.uqsnap</c>) + gzipped migration
/// stream (<c>.uqsnap.vmstate</c>), written without restarting QEMU.
/// <para>
/// Save sibling / Overwrite → parent = current.backingDisk.
/// Save child → parent = current.
/// Legacy .uqsnaps (embedded savevm, no sidecar) still load via byte-copy + loadvm;
/// re-saving one converts it to the D4 format.
/// </para>
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("snapshot")]
[DeclareHorizontalGroup("snapshot/actions")]
[DeclareHorizontalGroup("save")]
[DeclareFoldoutGroup("Debug", Expanded = false)]
public class DurableSnapshotUI : MonoBehaviour
{
    const string DefaultSnapshotOutputFolder = "Assets/qemu/Snapshots";
    const string DefaultNewSnapshotName = "snap1";

    [PropertyOrder(0)]
    public VirtualMachine virtualMachine;

    [PropertyTooltip("$CurrentSnapshotTooltip")]
    [PropertyOrder(1)]
    [Group("snapshot")]
    [ReadOnly]
    [LabelText("$CurrentSnapshotLabel")]
    [FormerlySerializedAs("targetDisk")]
    public DiskAsset currentSnapshot;

#if UNITY_EDITOR
    /// <summary>
    /// Last <see cref="VirtualMachine.diskAsset"/> we mirrored into <see cref="currentSnapshot"/> via
    /// change-detection. Save can move currentSnapshot ahead of the still-running disk until Ready.
    /// </summary>
    DiskAsset _diskAssetMirroredToCurrent;

    string CurrentSnapshotLabel =>
        CurrentSnapshotIsFrozen ? "Current Snapshot ❄" : "Current Snapshot";

    string CurrentSnapshotTooltip
    {
        get
        {
            const string baseText =
                "Snapshot this UI is focused on. Follows the VM's Disk Asset when that changes " +
                "or the VM finishes starting. Save child / sibling / overwrite also update it.";
            if (!CurrentSnapshotIsFrozen)
                return baseText;
            return baseText +
                   "\n\n❄ means other snapshots use this as their parent — overwriting it can " +
                   "corrupt those children. Prefer Save sibling or Save child instead.";
        }
    }

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
        if (virtualMachine != null)
            virtualMachine.OnReady += HandleVmReady;
        SyncCurrentSnapshotFromVmDiskChange();
    }

    void OnDisable()
    {
        if (virtualMachine != null)
            virtualMachine.OnReady -= HandleVmReady;
    }

    void Update()
    {
        SyncCurrentSnapshotFromVmDiskChange();
    }

    void HandleVmReady()
    {
        // Restart/start boots whatever is in Disk Asset — Current Snapshot should match that,
        // even if a prior Save left Current Snapshot on a newly written .uqsnap.
        ForceCurrentSnapshotFromVmDisk();
    }

    /// <summary>
    /// When the VM Disk Asset changes in the inspector, follow it.
    /// Does not clobber a post-save Current Snapshot while Disk Asset stays the same.
    /// </summary>
    void SyncCurrentSnapshotFromVmDiskChange()
    {
        if (virtualMachine == null)
            return;
        DiskAsset boot = virtualMachine.diskAsset;
        if (boot == _diskAssetMirroredToCurrent)
            return;
        ForceCurrentSnapshotFromVmDisk();
    }

    void ForceCurrentSnapshotFromVmDisk()
    {
        if (virtualMachine == null)
            return;
        DiskAsset boot = virtualMachine.diskAsset;
        _diskAssetMirroredToCurrent = boot;
        if (boot != null)
            currentSnapshot = boot;
    }

    void SetCurrentSnapshot(DiskAsset snapshot)
    {
        currentSnapshot = snapshot;
    }

    [PropertyOrder(2)]
    [Group("snapshot/actions")]
    [Button("Reload state")]
    [EnableIf(nameof(Ready))]
    [PropertyTooltip("Rewind to the last quick-save in this session (or to the moment the snapshot was loaded).")]
    public async void ReloadDurableStateButton()
    {
        try
        {
            status = "Reloading…";
            string result = await virtualMachine.RunHumanMonitorCommandAsync(
                $"loadvm {DiskOverlay.DurableSaveVmTag}");
            if (!string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException(result.Trim());
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
    [Button("Overwrite")]
    [EnableIf(nameof(CanOverwrite))]
    public async void OverwriteCurrentSnapshotButton()
    {
        try
        {
            if (DiskAsset.HasChildDisks(currentSnapshot))
            {
                string[] childNames = DiskAsset.GetChildDiskNames(currentSnapshot);
                string childList = childNames.Length <= 8
                    ? string.Join(", ", childNames)
                    : string.Join(", ", childNames, 0, 8) + $" (+{childNames.Length - 8} more)";
                bool proceed = EditorUtility.DisplayDialog(
                    "Overwrite frozen snapshot?",
                    $"'{currentSnapshot.name}' is frozen — {childNames.Length} disk(s) use it as their parent:\n\n" +
                    $"{childList}\n\n" +
                    "Overwriting can corrupt those children (parents must stay immutable).\n\n" +
                    "Prefer Save sibling / Save child instead. Overwrite anyway?",
                    "Overwrite anyway",
                    "Cancel");
                if (!proceed)
                {
                    status = "Overwrite cancelled";
                    return;
                }
            }

            string existing = currentSnapshot.GetQcow2FilesystemPath();
            if (string.IsNullOrEmpty(existing))
                throw new InvalidOperationException(
                    $"Snapshot '{currentSnapshot.name}' has no image file on disk");

            status = $"Overwriting '{currentSnapshot.name}'…";
            // Same place in the tree: parent stays current.backingDisk (sibling semantics).
            var asset = await SaveDurableSnapshotAsync(
                MakeProjectRelative(existing),
                currentSnapshot.backingDisk);
            if (asset != null)
            {
                SetCurrentSnapshot(asset);
                status = $"Overwrote '{asset.name}'";
            }
        }
        catch (Exception e)
        {
            status = $"Overwrite failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save child")]
    [EnableIf(nameof(CanSaveChild))]
    public async void SaveChildSnapshotButton()
    {
        // Parent is Current Snapshot (not VM Disk Asset): after saving Y while still running
        // on X, Current Snapshot is Y so the next child becomes Z→Y, not another Z→X.
        DiskAsset parent = currentSnapshot != null ? currentSnapshot : virtualMachine.diskAsset;
        if (parent == null)
        {
            status = "No current snapshot / disk to parent under";
            return;
        }

        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string projectPath = EditorUtility.SaveFilePanelInProject(
            "Save Child Snapshot",
            DefaultNewSnapshotName,
            "uqsnap",
            $"Child of '{parent.name}' — stores only changes on top of that snapshot.",
            DefaultSnapshotOutputFolder);
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            status = $"Saving child of '{parent.name}'…";
            var asset = await SaveDurableSnapshotAsync(projectPath, parent);
            if (asset != null)
            {
                SetCurrentSnapshot(asset);
                status = $"Saved child '{asset.name}' → parent '{parent.name}'";
            }
        }
        catch (Exception e)
        {
            status = $"Save child failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [PropertyOrder(3)]
    [Group("save")]
    [Button("Save sibling")]
    [EnableIf(nameof(CanSaveSibling))]
    public async void SaveSiblingSnapshotButton()
    {
        DiskAsset current = currentSnapshot;
        if (current == null || current.backingDisk == null)
        {
            status = "Save sibling needs a current snapshot that has a parent";
            return;
        }

        DiskAsset parent = current.backingDisk;
        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string projectPath = EditorUtility.SaveFilePanelInProject(
            "Save Sibling Snapshot",
            DefaultNewSnapshotName,
            "uqsnap",
            $"Same parent as '{current.name}' ({parent.name}).",
            DefaultSnapshotOutputFolder);
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            status = "Saving sibling…";
            // Same parent as current → sibling in the tree (not a child of current).
            var asset = await SaveDurableSnapshotAsync(projectPath, parent);
            if (asset != null)
            {
                SetCurrentSnapshot(asset);
                status = $"Saved sibling '{asset.name}' (parent: {parent.name})";
            }
        }
        catch (Exception e)
        {
            status = $"Save sibling failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [PropertyOrder(4)]
    [Button("Load snapshot…")]
    [EnableIf(nameof(HasVirtualMachine))]
    public async void LoadOtherStateButton()
    {
        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string absolutePath = EditorUtility.OpenFilePanel(
            "Load snapshot",
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", DefaultSnapshotOutputFolder)),
            "uqsnap");
        if (string.IsNullOrEmpty(absolutePath))
            return;

        try
        {
            // OpenFilePanel may return the junction target (e.g. unityqemu/Assets/…)
            // while this project reaches the same files via sketches-urp/Assets/qemu.
            DiskAsset snapshot = DiskAsset.FindByFilesystemPath(absolutePath);
            if (snapshot == null || !snapshot.HasVmState)
                throw new InvalidOperationException(
                    $"No snapshot asset found for '{absolutePath}'. " +
                    "Is the file imported in this project?");

            status = $"Loading '{snapshot.name}'…";
            await LoadDurableSnapshotAsync(snapshot);
            if (!string.IsNullOrEmpty(virtualMachine.LastStateRestoreError))
            {
                status = $"Loaded '{snapshot.name}' (disk only — state restore failed)";
                return;
            }
            status = $"Loaded '{snapshot.name}'";
        }
        catch (Exception e)
        {
            status = $"Load failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    bool CanOverwrite =>
        currentSnapshot != null &&
        currentSnapshot.HasVmState &&
        currentSnapshot.backingDisk != null;
    bool HasVirtualMachine => virtualMachine != null;
    bool Ready => virtualMachine != null && virtualMachine.QmpConnected;
    bool CanSaveChild => Ready && virtualMachine.diskAsset != null;
    bool CanSaveSibling =>
        Ready && currentSnapshot != null && currentSnapshot.backingDisk != null;
    bool CurrentSnapshotIsFrozen =>
        currentSnapshot != null && DiskAsset.HasChildDisks(currentSnapshot);

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
    [LabelText("Boot disk")]
    string BootDiskName =>
        virtualMachine != null && virtualMachine.diskAsset != null
            ? virtualMachine.diskAsset.name
            : "(none)";

    /// <summary>
    /// D4 durable save: capture state (pause → freeze work layer via live external
    /// snapshot → quick-save → migrate RAM to a gzipped <c>.vmstate</c> sidecar → resume),
    /// then write the disk diff offline with <c>qemu-img convert -B parent</c>.
    /// QEMU keeps running; no restart (except when overwriting the currently booted
    /// snapshot, whose file QEMU holds open as backing).
    /// <para>
    /// <b>Child vs sibling</b> is which parent you pass (unchanged from the D2 design):
    /// <list type="bullet">
    /// <item>Save sibling / Overwrite → <c>current.backingDisk</c> (same parent as current).</item>
    /// <item>Save child → <c>current</c> itself.</item>
    /// </list>
    /// <c>convert -B</c> computes the true content diff against the parent either way,
    /// and also thins out legacy byte-copy boots (fat work images) automatically.
    /// </para>
    /// </summary>
    public async Task<DiskAsset> SaveDurableSnapshotAsync(
        string uqsnapProjectPath,
        DiskAsset immediateParent)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
        if (immediateParent == null)
            throw new InvalidOperationException("Immediate parent DiskAsset is required");
        if (string.IsNullOrWhiteSpace(uqsnapProjectPath))
            throw new ArgumentException("No output path given", nameof(uqsnapProjectPath));

        string parentPath = immediateParent.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(parentPath) || !File.Exists(parentPath))
            throw new FileNotFoundException(
                $"Parent '{immediateParent.name}' has no image file", parentPath);

        string uqsnapFull = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", uqsnapProjectPath));
        string sidecarPath = uqsnapFull + DiskAsset.VmStateSidecarSuffix;
        // Capture to a temp name; only swap it in after the disk diff is written, so a
        // failed overwrite never leaves an existing snapshot with mismatched disk/state.
        string sidecarTmp = sidecarPath + ".new";

        status = "Capturing state…";
        string frozenLayer = await virtualMachine.CaptureStateAsync(sidecarTmp);

        status = "Writing disk diff…";
        bool qemuStillRunning = true;
        try
        {
            try
            {
                DiskOverlay.ConvertThin(frozenLayer, parentPath, uqsnapFull);
            }
            catch (Exception e)
            {
                // Overwrite of the currently booted snapshot: QEMU holds the destination
                // open as a backing file, so the delete/rename fails. Stop, write, then
                // restart into the new snapshot (fast: -incoming from the fresh sidecar).
                Debug.LogWarning(
                    $"Could not write '{uqsnapFull}' while QEMU is running ({e.Message}). " +
                    "Stopping QEMU to write, then restarting into the saved state.");
                await virtualMachine.StopGuestProcessAsync();
                qemuStillRunning = false;
                DiskOverlay.ConvertThin(frozenLayer, parentPath, uqsnapFull);
            }

            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
            File.Move(sidecarTmp, sidecarPath);
        }
        catch
        {
            try { if (File.Exists(sidecarTmp)) File.Delete(sidecarTmp); } catch { /* ignore */ }
            throw;
        }

        AssetDatabase.ImportAsset(uqsnapProjectPath, ImportAssetOptions.ForceUpdate);
        AssetImporter importer = AssetImporter.GetAtPath(uqsnapProjectPath);
        if (importer == null)
            throw new Exception($"No importer found for '{uqsnapProjectPath}'");

        LaunchConfig effective = virtualMachine.EffectiveLaunchConfig;
        LaunchConfig toStore = effective != null
            ? effective.Clone()
            : LaunchConfig.CreateDefault();
        toStore.GetRuntimeMemoryAndExtraArgs(out int memoryMb, out string extraArgs);
        toStore.memoryMb = memoryMb;
        toStore.extraQemuArgs = extraArgs ?? "";

        var serializedImporter = new SerializedObject(importer);
        SerializedProperty backingProperty = serializedImporter.FindProperty("backingDisk");
        SerializedProperty metaProperty = serializedImporter.FindProperty("uqsnapMetadata");
        if (backingProperty == null)
            throw new Exception(
                $"The disk importer for '{uqsnapProjectPath}' has no backingDisk property");
        if (metaProperty == null)
            throw new Exception(
                $"The disk importer for '{uqsnapProjectPath}' has no uqsnapMetadata property");

        backingProperty.objectReferenceValue = immediateParent;

        SerializedProperty hasMetaProperty = serializedImporter.FindProperty("hasUqsnapMetadata");
        if (hasMetaProperty != null)
            hasMetaProperty.boolValue = true;

        SerializedProperty createdAtProperty = metaProperty.FindPropertyRelative("createdAt");
        SerializedProperty launchConfigProperty = metaProperty.FindPropertyRelative("launchConfig");
        SerializedProperty qemuVersionProperty = metaProperty.FindPropertyRelative("qemuVersion");
        SerializedProperty unityQemuVersionProperty =
            metaProperty.FindPropertyRelative("unityQemuVersion");

        if (createdAtProperty != null)
            createdAtProperty.stringValue = DateTime.UtcNow.ToString("o");
        if (launchConfigProperty != null)
        {
            SerializedProperty memoryMbProperty =
                launchConfigProperty.FindPropertyRelative("memoryMb");
            SerializedProperty extraArgsProperty =
                launchConfigProperty.FindPropertyRelative("extraQemuArgs");
            SerializedProperty cdromsProperty =
                launchConfigProperty.FindPropertyRelative("cdroms");
            SerializedProperty floppiesProperty =
                launchConfigProperty.FindPropertyRelative("floppies");
            SerializedProperty hostFoldersProperty =
                launchConfigProperty.FindPropertyRelative("hostFolders");
            SerializedProperty smbShareFolderProperty =
                launchConfigProperty.FindPropertyRelative("smbShareFolder");
            if (memoryMbProperty != null)
                memoryMbProperty.intValue = toStore.memoryMb;
            if (extraArgsProperty != null)
                extraArgsProperty.stringValue = toStore.extraQemuArgs ?? "";
            SetObjectReferenceArray(cdromsProperty, toStore.cdroms);
            SetObjectReferenceArray(floppiesProperty, toStore.floppies);
            SetObjectReferenceArray(hostFoldersProperty, toStore.hostFolders);
            if (smbShareFolderProperty != null)
                smbShareFolderProperty.objectReferenceValue = toStore.smbShareFolder;
        }
        if (qemuVersionProperty != null)
            qemuVersionProperty.stringValue = VirtualMachine.QueryBundledQemuVersion();
        if (unityQemuVersionProperty != null)
            unityQemuVersionProperty.stringValue = VirtualMachine.QueryUnityQemuPackageVersion();

        serializedImporter.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(importer);
        AssetDatabase.WriteImportSettingsIfDirty(uqsnapProjectPath);
        importer.SaveAndReimport();

        var diskAsset = AssetDatabase.LoadAssetAtPath<DiskAsset>(uqsnapProjectPath);
        if (diskAsset == null || !diskAsset.HasVmState)
            throw new Exception(
                $"uqsnap imported but no DiskAsset with uqsnapMetadata at '{uqsnapProjectPath}'");

        Debug.Log(
            $"Durable snapshot saved: {uqsnapProjectPath} (backing={immediateParent.name}, " +
            $"memoryMb={toStore.memoryMb})");

        if (!qemuStillRunning)
        {
            // Overwrite path stopped QEMU to replace the open backing file — restart
            // into the just-saved snapshot (thin overlay + incoming state feed).
            virtualMachine.PrepareBootFromDisk(diskAsset, loadVmState: true);
            await virtualMachine.StartGuestProcessAsync();
        }
        // Otherwise QEMU never stopped: the session continues on the new work layer,
        // whose chain content-equals the new snapshot. Current Snapshot is updated by
        // the caller; Disk Asset stays until the next Load/restart.

        return diskAsset;
    }

    /// <summary>
    /// Stop QEMU and boot the .uqsnap DiskAsset: thin overlay + incoming state feed for
    /// D4 snapshots, byte-copy + loadvm for legacy ones.
    /// </summary>
    public async Task LoadDurableSnapshotAsync(DiskAsset snapshot)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.HasVmState)
            throw new InvalidOperationException(
                $"'{snapshot.name}' has no uqsnap metadata — assign a .uqsnap DiskAsset");

        await virtualMachine.StopGuestProcessAsync();
        virtualMachine.PrepareBootFromDisk(snapshot, loadVmState: true);
        SetCurrentSnapshot(snapshot);
        // Disk Asset changed — keep mirror tracking in sync so Update doesn't fight this.
        _diskAssetMirroredToCurrent = snapshot;
        await virtualMachine.StartGuestProcessAsync();
    }

    static void SetObjectReferenceArray(SerializedProperty property, UnityEngine.Object[] values)
    {
        if (property == null || !property.isArray)
            return;

        values ??= Array.Empty<UnityEngine.Object>();
        property.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
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

    static string MakeProjectRelative(string fullPath)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
            + Path.DirectorySeparatorChar;
        fullPath = Path.GetFullPath(fullPath);
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return fullPath.Substring(root.Length).Replace('\\', '/');

        // Junction / other checkout: resolve to the DiskAsset imported in this project.
        DiskAsset found = DiskAsset.FindByFilesystemPath(fullPath);
        if (found != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(found);
            if (!string.IsNullOrEmpty(assetPath))
                return assetPath.Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }
#else
    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";
#endif
}
}
