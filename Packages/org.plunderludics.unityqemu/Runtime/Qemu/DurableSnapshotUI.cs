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
/// Inspector UI for durable snapshots: savevm into the work image, copy to a <c>.uqsnap</c> asset,
/// load via copy → Library work image + loadvm.
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("target/actions")]
[DeclareFoldoutGroup("Debug", Expanded = false)]
public class DurableSnapshotUI : MonoBehaviour
{
    const string DefaultSnapshotOutputFolder = "Assets/Qemu/Snapshots";
    const string DefaultNewSnapshotName = "snap1";

    [FormerlySerializedAs("qemu")]
    [PropertyOrder(0)]
    public VirtualMachine virtualMachine;

    [Tooltip("Existing snapshot to load or overwrite")]
    [PropertyOrder(1)]
    public SnapshotAsset targetSnapshot;

#if UNITY_EDITOR
    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    [PropertyOrder(2)]
    [Group("target/actions")]
    [Button("Load")]
    [EnableIf(nameof(HasTargetSnapshot))]
    public async void LoadSnapshotButton()
    {
        try
        {
            status = "Loading…";
            await LoadDurableSnapshotAsync(targetSnapshot);
            status = $"Loaded '{targetSnapshot.name}'";
        }
        catch (Exception e)
        {
            status = $"Load failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [PropertyOrder(2)]
    [Group("target/actions")]
    [Button("Overwrite")]
    [EnableIf(nameof(CanOverwrite))]
    public async void OverwriteTargetSnapshotButton()
    {
        try
        {
            string existing = targetSnapshot.GetImageFilesystemPath();
            if (string.IsNullOrEmpty(existing))
                throw new InvalidOperationException(
                    $"Target snapshot '{targetSnapshot.name}' has no .uqsnap file on disk");

            status = $"Overwriting '{targetSnapshot.name}'…";
            var asset = await SaveDurableSnapshotAsync(MakeProjectRelative(existing));
            if (asset != null)
            {
                targetSnapshot = asset;
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
    [Button("Save new snapshot…")]
    [EnableIf(nameof(Ready))]
    public async void SaveNewSnapshotButton()
    {
        EnsureSnapshotFolder(DefaultSnapshotOutputFolder);
        string projectPath = EditorUtility.SaveFilePanelInProject(
            "Save Snapshot",
            DefaultNewSnapshotName,
            "uqsnap",
            "Choose where to save the snapshot",
            DefaultSnapshotOutputFolder);
        if (string.IsNullOrEmpty(projectPath))
            return;

        try
        {
            status = "Saving…";
            var asset = await SaveDurableSnapshotAsync(projectPath);
            if (asset != null)
            {
                targetSnapshot = asset;
                status = $"Saved '{asset.name}'";
            }
        }
        catch (Exception e)
        {
            status = $"Save failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    /// <summary>
    /// Instantly re-load the durable state already in the current work image (no process
    /// restart). Works after any durable save or load this session — both leave the
    /// savevm tag in the open work qcow2.
    /// </summary>
    [PropertyOrder(4)]
    [Button("Reload last saved state")]
    [EnableIf(nameof(Ready))]
    public async void ReloadDurableStateButton()
    {
        try
        {
            status = "Reloading…";
            string result = await virtualMachine.RunHumanMonitorCommandAsync(
                $"loadvm {DiskOverlay.DurableSaveVmTag}");
            // HMP reports loadvm failures as text output (e.g. missing tag on a fresh boot).
            if (!string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException(result.Trim());
            status = "Reloaded last saved state";
        }
        catch (Exception e)
        {
            status = $"Reload failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    bool CanOverwrite => Ready && targetSnapshot != null;
    bool HasTargetSnapshot => targetSnapshot != null && virtualMachine != null;
    bool Ready => virtualMachine != null && virtualMachine.QmpConnected;

    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";

    [PropertyOrder(101)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    bool QmpReady => Ready;

    [PropertyOrder(102)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string WorkOverlay => virtualMachine != null ? virtualMachine.WorkOverlayPath : "";

    /// <summary>
    /// Pause → savevm → copy work image to a <c>.uqsnap</c> asset → resume.
    /// Prefers copying while QEMU stays paused (true pause/resume). If Windows locks the
    /// work file, falls back to stop → copy → restart + loadvm.
    /// </summary>
    public async Task<SnapshotAsset> SaveDurableSnapshotAsync(string uqsnapProjectPath)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
        if (virtualMachine.ActiveDiskAsset == null)
            throw new InvalidOperationException(
                "The active disk has no underlying DiskAsset; configure a disk or valid snapshot first");
        if (string.IsNullOrWhiteSpace(uqsnapProjectPath))
            throw new ArgumentException("No output path given", nameof(uqsnapProjectPath));

        string workPath = virtualMachine.WorkOverlayPath;
        if (string.IsNullOrEmpty(workPath) || !File.Exists(workPath))
            throw new InvalidOperationException(
                "No work image — boot with a Disk Asset (work overlay on) or a Boot Snapshot first");

        DiskAsset backingDisk = virtualMachine.ActiveDiskAsset;

        string uqsnapFull = Path.GetFullPath(Path.Combine(Application.dataPath, "..", uqsnapProjectPath));

        await virtualMachine.PauseAsync();
        bool qemuStillRunning = true;
        try
        {
            await virtualMachine.RunHumanMonitorCommandAsync($"savevm {DiskOverlay.DurableSaveVmTag}");

            try
            {
                DiskOverlay.EnsureBackingMatches(
                    workPath, backingDisk.GetQcow2FilesystemPath());
                DiskOverlay.CopyAtomic(workPath, uqsnapFull);
            }
            catch (Exception e)
            {
                // Common on Windows: QEMU keeps the qcow2 open exclusively even while paused.
                Debug.LogWarning(
                    $"Could not copy work image while QEMU is running ({e.Message}). " +
                    "Stopping QEMU to copy, then restarting into the saved state.");
                await virtualMachine.StopGuestProcessAsync();
                qemuStillRunning = false;
                DiskOverlay.EnsureBackingMatches(
                    workPath, backingDisk.GetQcow2FilesystemPath());
                DiskOverlay.CopyAtomic(workPath, uqsnapFull);
            }
        }
        catch
        {
            if (qemuStillRunning)
            {
                try { await virtualMachine.ResumeAsync(); }
                catch (Exception resumeError)
                {
                    Debug.LogWarning($"Failed to resume after save error: {resumeError.Message}");
                }
            }
            throw;
        }

        AssetDatabase.ImportAsset(uqsnapProjectPath, ImportAssetOptions.ForceUpdate);
        AssetImporter importer = AssetImporter.GetAtPath(uqsnapProjectPath);
        if (importer == null)
            throw new Exception($"No importer found for '{uqsnapProjectPath}'");

        var serializedImporter = new SerializedObject(importer);
        SerializedProperty backingProperty = serializedImporter.FindProperty("backingDisk");
        SerializedProperty createdAtProperty = serializedImporter.FindProperty("createdAt");
        if (backingProperty == null)
            throw new Exception(
                $"The uqsnap importer for '{uqsnapProjectPath}' has no backingDisk property");

        string createdAt = DateTime.UtcNow.ToString("o");
        backingProperty.objectReferenceValue = backingDisk;
        if (createdAtProperty != null)
            createdAtProperty.stringValue = createdAt;
        serializedImporter.ApplyModifiedPropertiesWithoutUndo();
        importer.SaveAndReimport();

        var snapAsset = AssetDatabase.LoadAssetAtPath<SnapshotAsset>(uqsnapProjectPath);
        if (snapAsset == null)
            throw new Exception(
                $"uqsnap imported but no SnapshotAsset at '{uqsnapProjectPath}' — is UqsnapImporter present?");

        Debug.Log($"Durable snapshot saved: {uqsnapProjectPath}");

        if (qemuStillRunning)
        {
            // Guest is already at the savevm point — just unpause.
            await virtualMachine.ResumeAsync();
        }
        else
        {
            // Restarted after a locked-file fallback; reload the tag we just wrote into the work image.
            virtualMachine.RequestLoadVmOnReady(DiskOverlay.DurableSaveVmTag);
            await virtualMachine.StartGuestProcessAsync();
        }

        return snapAsset;
    }

    /// <summary>Stop QEMU, copy .uqsnap → work image, restart, loadvm.</summary>
    public async Task LoadDurableSnapshotAsync(SnapshotAsset snapshot)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        await virtualMachine.StopGuestProcessAsync();
        virtualMachine.PrepareBootFromSnapshot(snapshot);
        await virtualMachine.StartGuestProcessAsync();
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
        return fullPath.Replace('\\', '/');
    }
#else
    // Stubs so the component still serializes cleanly in player builds.
    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";
#endif
}
}
