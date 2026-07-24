using System;
using System.IO;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable snapshots: thin <c>.qcow2</c> disk diff + <c>.uqsnap</c> migration stream,
/// written without restarting QEMU.
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
    const string DefaultSnapshotOutputFolder = "Assets/qemu/Snapshots";
    const string DefaultNewSnapshotName = "snap1";

    [PropertyOrder(0)]
    public VirtualMachine virtualMachine;

    [PropertyOrder(0)]
    [Tooltip(
        "Gzip the machine-state file when saving. On by default (smaller files). " +
        "Turn off for faster saves; the choice is stored with each snapshot so load stays correct.")]
    [LabelText("Compress machine state")]
    public bool compressMachineState = true;

    [PropertyOrder(0)]
    [Tooltip(
        "Write a sibling .png (same basename as the .uqsnap) from the live VNC frame when saving. " +
        "Used as UqsnapAsset.screenshot and the Project window icon.")]
    [LabelText("Capture screenshot")]
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

    [PropertyOrder(2)]
    [Group("snapshot/actions")]
    [Button("Reload state")]
    [EnableIf(nameof(Ready))]
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
            DiskAsset tip = CurrentDiskTip;
            if (DiskAsset.HasChildDisks(tip))
            {
                string[] childNames = DiskAsset.GetChildDiskNames(tip);
                string childList = childNames.Length <= 8
                    ? string.Join(", ", childNames)
                    : string.Join(", ", childNames, 0, 8) + $" (+{childNames.Length - 8} more)";
                bool proceed = EditorUtility.DisplayDialog(
                    "Overwrite frozen snapshot?",
                    $"'{currentSnapshot.name}'s disk is frozen — {childNames.Length} disk(s) use it as parent:\n\n" +
                    $"{childList}\n\nOverwrite anyway?",
                    "Overwrite anyway",
                    "Cancel");
                if (!proceed)
                {
                    status = "Overwrite cancelled";
                    return;
                }
            }

            string existingDisk = tip.GetQcow2FilesystemPath();
            if (string.IsNullOrEmpty(existingDisk))
                throw new InvalidOperationException(
                    $"Snapshot '{currentSnapshot.name}' has no linked disk file");

            status = $"Overwriting '{currentSnapshot.name}'…";
            var asset = await SaveDurableSnapshotAsync(
                MakeProjectRelative(existingDisk),
                Path.ChangeExtension(MakeProjectRelative(existingDisk), ".uqsnap"),
                tip.backingDisk);
            if (asset != null)
                status = $"Overwrote '{asset.name}'";
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
        DiskAsset parent = CurrentDiskTip ?? virtualMachine.ActiveDiskAsset;
        if (parent == null)
        {
            status = "No current disk tip to parent under";
            return;
        }

        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string projectPath = EditorUtility.SaveFilePanelInProject(
            "Save Child Snapshot",
            DefaultNewSnapshotName,
            "uqsnap",
            $"Child of '{parent.name}' — stores only changes on top of that disk.",
            DefaultSnapshotOutputFolder);
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            status = $"Saving child of '{parent.name}'…";
            string qcow2Path = Path.ChangeExtension(projectPath, ".qcow2");
            var asset = await SaveDurableSnapshotAsync(qcow2Path, projectPath, parent);
            if (asset != null)
                status = $"Saved child '{asset.name}' → parent '{parent.name}'";
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
        DiskAsset tip = CurrentDiskTip;
        if (tip == null || tip.backingDisk == null)
        {
            status = "Save sibling needs a current snapshot whose disk has a parent";
            return;
        }

        DiskAsset parent = tip.backingDisk;
        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string projectPath = EditorUtility.SaveFilePanelInProject(
            "Save Sibling Snapshot",
            DefaultNewSnapshotName,
            "uqsnap",
            $"Same parent as '{(sessionCurrent != null ? sessionCurrent.DisplayLabel : tip.name)}' ({parent.name}).",
            DefaultSnapshotOutputFolder);
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            status = "Saving sibling…";
            string qcow2Path = Path.ChangeExtension(projectPath, ".qcow2");
            var asset = await SaveDurableSnapshotAsync(qcow2Path, projectPath, parent);
            if (asset != null)
                status = $"Saved sibling '{asset.name}' (parent: {parent.name})";
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
            string projectPath = MakeProjectRelative(absolutePath);
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
            await LoadDurableSnapshotAsync(snap);
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

    bool CanOverwrite =>
        currentSnapshot != null &&
        CurrentDiskTip != null &&
        CurrentDiskTip.backingDisk != null;
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

    /// <summary>
    /// Capture machine state + write thin disk diff. Creates/updates
    /// <paramref name="qcow2ProjectPath"/> and <paramref name="uqsnapProjectPath"/>.
    /// </summary>
    public async Task<UqsnapAsset> SaveDurableSnapshotAsync(
        string qcow2ProjectPath,
        string uqsnapProjectPath,
        DiskAsset immediateParent)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
        if (immediateParent == null)
            throw new InvalidOperationException("Immediate parent DiskAsset is required");
        if (string.IsNullOrWhiteSpace(qcow2ProjectPath) || string.IsNullOrWhiteSpace(uqsnapProjectPath))
            throw new ArgumentException("Output paths required");

        string parentPath = immediateParent.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(parentPath) || !File.Exists(parentPath))
            throw new FileNotFoundException(
                $"Parent '{immediateParent.name}' has no image file", parentPath);

        string qcow2Full = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", qcow2ProjectPath));
        string uqsnapFull = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", uqsnapProjectPath));
        string pngProjectPath = UqsnapAsset.SiblingScreenshotProjectPath(uqsnapProjectPath);
        string pngFull = string.IsNullOrEmpty(pngProjectPath)
            ? null
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", pngProjectPath));
        string stateTmp = Path.Combine(
            Application.temporaryCachePath,
            Path.GetFileName(uqsnapFull) + ".new");

        // Grab the frame before migrate/pause so the preview matches the saved state.
        bool wroteScreenshot = false;
        if (captureScreenshot && !string.IsNullOrEmpty(pngFull))
        {
            status = "Capturing screenshot…";
            wroteScreenshot = TryWriteScreenshotPng(virtualMachine.Texture, pngFull);
            if (!wroteScreenshot)
            {
                Debug.LogWarning(
                    "UnityQemu: could not capture snapshot screenshot " +
                    "(no VNC frame yet?). Saving without preview.");
            }
        }

        status = "Capturing state…";
        string frozenLayer = await virtualMachine.CaptureStateAsync(
            stateTmp, gzip: compressMachineState);

        status = "Writing disk diff…";
        bool qemuStillRunning = true;
        try
        {
            try
            {
                DiskOverlay.ConvertThin(frozenLayer, parentPath, qcow2Full);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"Could not write '{qcow2Full}' while QEMU is running ({e.Message}). " +
                    "Stopping QEMU to write, then restarting into the saved state.");
                await virtualMachine.StopGuestProcessAsync();
                qemuStillRunning = false;
                DiskOverlay.ConvertThin(frozenLayer, parentPath, qcow2Full);
            }

            if (File.Exists(uqsnapFull))
                File.Delete(uqsnapFull);
            File.Move(stateTmp, uqsnapFull);
        }
        catch
        {
            try { if (File.Exists(stateTmp)) File.Delete(stateTmp); } catch { /* ignore */ }
            throw;
        }

        AssetDatabase.ImportAsset(qcow2ProjectPath, ImportAssetOptions.ForceUpdate);
        AssetImporter diskImporter = AssetImporter.GetAtPath(qcow2ProjectPath);
        if (diskImporter == null)
            throw new Exception($"No AssetImporter for '{qcow2ProjectPath}'");
        var diskSo = new SerializedObject(diskImporter);
        SerializedProperty backingProp = diskSo.FindProperty("backingDisk");
        if (backingProp == null)
            throw new Exception($"qcow2 importer missing backingDisk on '{qcow2ProjectPath}'");
        backingProp.objectReferenceValue = immediateParent;
        diskSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(diskImporter);
        AssetDatabase.WriteImportSettingsIfDirty(qcow2ProjectPath);
        diskImporter.SaveAndReimport();

        DiskAsset diskAsset = AssetDatabase.LoadAssetAtPath<DiskAsset>(qcow2ProjectPath);
        if (diskAsset == null)
            throw new Exception($"No DiskAsset at '{qcow2ProjectPath}'");

        // Import the PNG before the .uqsnap so UqsnapImporter can load it as screenshot/icon.
        if (wroteScreenshot && !string.IsNullOrEmpty(pngProjectPath))
            AssetDatabase.ImportAsset(pngProjectPath, ImportAssetOptions.ForceUpdate);

        AssetDatabase.ImportAsset(uqsnapProjectPath, ImportAssetOptions.ForceUpdate);
        AssetImporter snapImporter = AssetImporter.GetAtPath(uqsnapProjectPath);
        if (snapImporter == null)
            throw new Exception($"No AssetImporter for '{uqsnapProjectPath}'");

        LaunchConfig effective = virtualMachine.EffectiveLaunchConfig;
        LaunchConfig toStore = effective != null
            ? effective.Clone()
            : LaunchConfig.CreateDefault();
        toStore.GetRuntimeMemoryAndExtraArgs(out int memoryMb, out string extraArgs);
        toStore.memoryMb = memoryMb;
        toStore.extraQemuArgs = extraArgs ?? "";

        var snapSo = new SerializedObject(snapImporter);
        snapSo.FindProperty("disk").objectReferenceValue = diskAsset;
        SerializedProperty metaProperty = snapSo.FindProperty("metadata");
        if (metaProperty == null)
            throw new Exception($"uqsnap importer missing metadata on '{uqsnapProjectPath}'");

        metaProperty.FindPropertyRelative("createdAt").stringValue = DateTime.UtcNow.ToString("o");
        metaProperty.FindPropertyRelative("vmstateUncompressed").boolValue = !compressMachineState;
        metaProperty.FindPropertyRelative("qemuVersion").stringValue =
            VirtualMachine.QueryBundledQemuVersion();
        metaProperty.FindPropertyRelative("unityQemuVersion").stringValue =
            VirtualMachine.QueryUnityQemuPackageVersion();

        SerializedProperty launchConfigProperty = metaProperty.FindPropertyRelative("launchConfig");
        if (launchConfigProperty != null)
        {
            launchConfigProperty.FindPropertyRelative("memoryMb").intValue = toStore.memoryMb;
            launchConfigProperty.FindPropertyRelative("extraQemuArgs").stringValue =
                toStore.extraQemuArgs ?? "";
            SetObjectReferenceArray(
                launchConfigProperty.FindPropertyRelative("cdroms"), toStore.cdroms);
            SetObjectReferenceArray(
                launchConfigProperty.FindPropertyRelative("floppies"), toStore.floppies);
            SetObjectReferenceArray(
                launchConfigProperty.FindPropertyRelative("hostFolders"), toStore.hostFolders);
            launchConfigProperty.FindPropertyRelative("smbShareFolder").objectReferenceValue =
                toStore.smbShareFolder;
        }

        snapSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(snapImporter);
        AssetDatabase.WriteImportSettingsIfDirty(uqsnapProjectPath);
        snapImporter.SaveAndReimport();

        var snapAsset = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(uqsnapProjectPath);
        if (snapAsset == null)
            throw new Exception($"No UqsnapAsset at '{uqsnapProjectPath}'");

        Debug.Log(
            $"Durable snapshot saved: {uqsnapProjectPath} + {qcow2ProjectPath} " +
            $"(backing={immediateParent.name}, memoryMb={toStore.memoryMb})");

        if (!qemuStillRunning)
        {
            virtualMachine.PrepareBoot(snapAsset, loadVmState: true);
            await virtualMachine.StartGuestProcessAsync();
        }
        else
        {
            // Session keeps running on the new work layer; update session tip only
            // (boot-config Snapshot / Disk slots stay as configured).
            virtualMachine.SetSessionCurrent(snapAsset);
        }

        return snapAsset;
    }

    public async Task LoadDurableSnapshotAsync(UqsnapAsset snap)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (snap == null)
            throw new ArgumentNullException(nameof(snap));
        if (snap.disk == null)
            throw new InvalidOperationException(
                $"'{snap.name}' has no linked disk");

        await virtualMachine.StopGuestProcessAsync();
        virtualMachine.PrepareBoot(snap, loadVmState: true);
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

    /// <summary>
    /// Writes a PNG next to the snapshot from the live VNC <see cref="Texture2D"/>.
    /// Returns false when there is no usable frame.
    /// </summary>
    static bool TryWriteScreenshotPng(Texture2D source, string absolutePngPath)
    {
        if (source == null || source.width <= 0 || source.height <= 0)
            return false;
        if (string.IsNullOrEmpty(absolutePngPath))
            return false;

        Texture2D copy = null;
        try
        {
            copy = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            copy.SetPixels32(source.GetPixels32());
            copy.Apply(false, false);
            byte[] png = copy.EncodeToPNG();
            if (png == null || png.Length == 0)
                return false;

            string dir = Path.GetDirectoryName(absolutePngPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(absolutePngPath, png);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"UnityQemu: screenshot write failed ({absolutePngPath}): {e.Message}");
            return false;
        }
        finally
        {
            if (copy != null)
                UnityEngine.Object.DestroyImmediate(copy);
        }
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
