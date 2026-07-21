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
/// Prototype UI for D2 durable snapshots: savevm into the work overlay, copy to Assets, load via restart + loadvm.
/// </summary>
[ExecuteAlways]
public class DurableSnapshotUI : MonoBehaviour
{
    [FormerlySerializedAs("qemu")]
    public VirtualMachine virtualMachine;

    [Tooltip("Folder under Assets where new snapshot .qcow2 + .asset pairs are written")]
    public string snapshotOutputFolder = "Assets/Qemu/Snapshots";

    [Tooltip("Name for the next Save (file stem). Spaces become underscores in the qcow2 filename.")]
    public string newSnapshotName = "snap1";

    [Tooltip("Optional note stored on the QemuSnapshotAsset")]
    [TextArea(2, 4)]
    public string newSnapshotNote = "";

    [Tooltip("Existing snapshot to load / overwrite")]
    public QemuSnapshotAsset targetSnapshot;

    [ShowInInspector, ReadOnly]
    string status = "Idle";

#if UNITY_EDITOR
    [ShowInInspector, ReadOnly]
    bool Ready => virtualMachine != null && virtualMachine.QmpConnected;

    [ShowInInspector, ReadOnly]
    string WorkOverlay => virtualMachine != null ? virtualMachine.WorkOverlayPath : "";

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    [Button("Save durable snapshot")]
    [EnableIf(nameof(Ready))]
    public async void SaveDurableSnapshotButton()
    {
        try
        {
            status = "Saving…";
            var asset = await SaveDurableSnapshotAsync(newSnapshotName, newSnapshotNote, overwrite: targetSnapshot);
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

    [Button("Load durable snapshot")]
    [EnableIf(nameof(HasTargetSnapshot))]
    public async void LoadDurableSnapshotButton()
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

    bool HasTargetSnapshot => targetSnapshot != null && virtualMachine != null;

    /// <summary>
    /// Pause → savevm → stop QEMU → copy work overlay into Assets → create/update QemuSnapshotAsset → restart.
    /// Stops the process before copy for Windows file-lock safety.
    /// </summary>
    public async Task<QemuSnapshotAsset> SaveDurableSnapshotAsync(
        string snapshotName,
        string note,
        QemuSnapshotAsset overwrite = null)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
        if (virtualMachine.ActiveDiskAsset == null)
            throw new InvalidOperationException("Assign virtualMachine.diskAsset (D2 base disk) before durable save");

        string workPath = virtualMachine.WorkOverlayPath;
        if (string.IsNullOrEmpty(workPath) || !File.Exists(workPath))
            throw new InvalidOperationException("No work overlay — boot with diskAsset + useEphemeralWorkOverlay first");

        string stem = string.IsNullOrWhiteSpace(snapshotName) ? "snap" : snapshotName.Trim();
        string fileStem = SanitizeFileStem(stem);

        await virtualMachine.PauseAsync();
        try
        {
            await virtualMachine.RunHumanMonitorCommandAsync($"savevm {QemuDiskOverlay.DurableSaveVmTag}");
        }
        finally
        {
            // Always stop before copying on Windows (open qcow2 may be locked even when paused).
            await virtualMachine.StopGuestProcessAsync();
        }

        EnsureSnapshotFolder(snapshotOutputFolder);

        string qcow2ProjectPath;
        string assetProjectPath;
        QemuSnapshotAsset snapAsset;

        if (overwrite != null)
        {
            snapAsset = overwrite;
            string existingImage = overwrite.GetImageFilesystemPath();
            if (string.IsNullOrEmpty(existingImage))
            {
                qcow2ProjectPath = $"{snapshotOutputFolder.TrimEnd('/')}/{fileStem}.qcow2";
                assetProjectPath = AssetDatabase.GetAssetPath(overwrite);
            }
            else
            {
                qcow2ProjectPath = MakeProjectRelative(existingImage);
                assetProjectPath = AssetDatabase.GetAssetPath(overwrite);
            }
        }
        else
        {
            qcow2ProjectPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{snapshotOutputFolder.TrimEnd('/')}/{fileStem}.qcow2");
            assetProjectPath = Path.ChangeExtension(qcow2ProjectPath, ".asset").Replace('\\', '/');
            snapAsset = null;
        }

        string qcow2Full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", qcow2ProjectPath));
        QemuDiskOverlay.CopyAtomic(workPath, qcow2Full);

        AssetDatabase.ImportAsset(qcow2ProjectPath, ImportAssetOptions.ForceUpdate);
        var imageDisk = AssetDatabase.LoadAssetAtPath<QemuDiskAsset>(qcow2ProjectPath);
        if (imageDisk == null)
            throw new Exception(
                $"qcow2 imported but no QemuDiskAsset at '{qcow2ProjectPath}' — is Qcow2Importer present?");

        if (snapAsset == null)
        {
            snapAsset = ScriptableObject.CreateInstance<QemuSnapshotAsset>();
            AssetDatabase.CreateAsset(snapAsset, assetProjectPath);
        }

        snapAsset.disk = virtualMachine.ActiveDiskAsset;
        snapAsset.image = imageDisk;
        snapAsset.note = note ?? "";
        snapAsset.StampCreatedNow();
        EditorUtility.SetDirty(snapAsset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"D2 durable snapshot saved: {assetProjectPath} (image={qcow2ProjectPath})");

        // Resume session on the same work overlay (still contains the savevm tag).
        await virtualMachine.StartGuestProcessAsync();
        return snapAsset;
    }

    /// <summary>Stop QEMU, copy snapshot image → work overlay, restart, loadvm.</summary>
    public async Task LoadDurableSnapshotAsync(QemuSnapshotAsset snapshot)
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

    static string SanitizeFileStem(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(' ', '_');
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
#endif
}
}
