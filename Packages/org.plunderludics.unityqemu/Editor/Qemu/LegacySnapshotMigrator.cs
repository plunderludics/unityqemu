using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityQemu.Editor {
/// <summary>
/// One-time converter for legacy durable snapshots (embedded savevm, fat disk copy)
/// into the current format (thin disk diff + compressed <c>.vmstate</c> sidecar).
/// <para>
/// Each snapshot is booted through the legacy path (byte-copy + loadvm), then re-saved
/// in place over its own file with its original parent. The in-place overwrite keeps
/// the asset GUID, so children and scene references stay valid; the guest-visible disk
/// content is unchanged, so child overlays remain correct.
/// </para>
/// </summary>
public static class LegacySnapshotMigrator
{
    public static bool IsRunning { get; private set; }
    public static string Status { get; private set; } = "idle";

    static readonly StringBuilder _report = new StringBuilder();
    public static string Report => _report.ToString();

    [MenuItem("Tools/UnityQemu/Convert Legacy Snapshots In Folder…")]
    static void MigrateFolderMenu()
    {
        if (IsRunning)
        {
            EditorUtility.DisplayDialog(
                "Snapshot conversion", "A conversion is already running.", "OK");
            return;
        }

        var ui = UnityEngine.Object.FindFirstObjectByType<DurableSnapshotUI>(
            FindObjectsInactive.Exclude);
        if (ui == null || ui.virtualMachine == null)
        {
            EditorUtility.DisplayDialog(
                "Snapshot conversion",
                "Needs an active Virtual Machine with a Durable Snapshot UI in the scene.",
                "OK");
            return;
        }

        string folder = EditorUtility.OpenFolderPanel(
            "Choose the snapshot folder to convert",
            Application.dataPath, "");
        if (string.IsNullOrEmpty(folder))
            return;

        string projectFolder = FileUtil.GetProjectRelativePath(folder.Replace('\\', '/'));
        if (string.IsNullOrEmpty(projectFolder))
        {
            EditorUtility.DisplayDialog(
                "Snapshot conversion", "The folder must be inside this project's Assets.", "OK");
            return;
        }

        List<string> paths = FindLegacySnapshotPaths(projectFolder);
        if (paths.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Snapshot conversion", $"No legacy snapshots found under {projectFolder}.", "OK");
            return;
        }

        bool proceed = EditorUtility.DisplayDialog(
            "Convert legacy snapshots?",
            $"{paths.Count} snapshot(s) under {projectFolder} will be booted and re-saved " +
            "in the new format (much smaller files). Each file is replaced in place — " +
            "make a backup of the folder first if you haven't.\n\n" +
            "This boots the VM once per snapshot and takes a few minutes per snapshot.",
            "Convert", "Cancel");
        if (!proceed)
            return;

        _ = MigrateFolderAsync(ui, projectFolder);
    }

    /// <summary>Project-relative paths of .uqsnaps under the folder that still lack a sidecar.</summary>
    public static List<string> FindLegacySnapshotPaths(string projectFolder)
    {
        string prefix = projectFolder.Replace('\\', '/').TrimEnd('/') + "/";
        var paths = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:DiskAsset", new[] { projectFolder.TrimEnd('/') }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
            // Only direct children of the chosen folder — never sibling folders
            // (FindAssets already scopes, but keep the prefix check as a guard).
            if (!assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase))
                continue;
            var disk = AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
            if (disk == null || !disk.HasVmState || disk.HasVmStateSidecar)
                continue;
            paths.Add(assetPath);
        }
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    /// <summary>Convert every legacy snapshot under <paramref name="projectFolder"/>, one at a time.</summary>
    public static async Task MigrateFolderAsync(DurableSnapshotUI ui, string projectFolder)
    {
        if (IsRunning)
            throw new InvalidOperationException("Snapshot conversion already running");

        IsRunning = true;
        _report.Clear();
        int converted = 0, failed = 0;
        try
        {
            List<string> paths = FindLegacySnapshotPaths(projectFolder);
            Status = $"converting {paths.Count} snapshot(s) under {projectFolder}";
            Debug.Log($"UnityQemu: {Status}");

            foreach (string assetPath in paths)
            {
                try
                {
                    await MigrateOneAsync(ui, assetPath);
                    converted++;
                }
                catch (Exception e)
                {
                    failed++;
                    _report.AppendLine($"FAILED {assetPath}: {e.Message}");
                    Debug.LogError($"UnityQemu: snapshot conversion failed for {assetPath}: {e}");
                    // Keep going — each snapshot converts independently.
                }
            }

            Status = $"done: {converted} converted, {failed} failed";
            Debug.Log($"UnityQemu: snapshot conversion {Status}\n{_report}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Boot one legacy snapshot and re-save it in place in the new format.</summary>
    public static async Task MigrateOneAsync(DurableSnapshotUI ui, string assetPath)
    {
        if (ui == null || ui.virtualMachine == null)
            throw new InvalidOperationException("DurableSnapshotUI with a VirtualMachine required");

        // Fresh load every time: an earlier in-place reimport (e.g. of this snapshot's
        // parent) invalidates previously loaded DiskAsset references.
        var snapshot = AssetDatabase.LoadAssetAtPath<DiskAsset>(assetPath);
        if (snapshot == null)
            throw new InvalidOperationException($"No DiskAsset at '{assetPath}'");
        if (!snapshot.HasVmState)
            throw new InvalidOperationException($"'{assetPath}' is not a durable snapshot");
        if (snapshot.HasVmStateSidecar)
        {
            _report.AppendLine($"skipped {assetPath} (already converted)");
            return;
        }
        if (snapshot.backingDisk == null)
            throw new InvalidOperationException(
                $"'{assetPath}' has no backingDisk asset — assign its parent before converting");

        string fullPath = snapshot.GetQcow2FilesystemPath();
        long oldBytes = new FileInfo(fullPath).Length;

        VirtualMachine vm = ui.virtualMachine;
        // Each snapshot must boot (and re-save) with its own stored launch config —
        // an inspector override (e.g. a different memory size) makes loadvm fail.
        bool hadOverride = vm.overrideSnapshotLaunchConfig;
        if (hadOverride)
            vm.overrideSnapshotLaunchConfig = false;

        try
        {
            Status = $"booting {assetPath}";
            Debug.Log($"UnityQemu: converting {assetPath} ({oldBytes / (1024.0 * 1024.0):F0} MB)…");
            await ui.LoadDurableSnapshotAsync(snapshot);
            if (!vm.QmpConnected)
                throw new InvalidOperationException("QMP did not connect after boot");
            if (!string.IsNullOrEmpty(vm.LastStateRestoreError))
                throw new InvalidOperationException(
                    $"saved state did not restore: {vm.LastStateRestoreError}");
            // Small settle so loadvm's resume and device state are fully established.
            await Task.Delay(2000);

            Status = $"re-saving {assetPath}";
            DiskAsset saved = await ui.SaveDurableSnapshotAsync(assetPath, snapshot.backingDisk);
            if (saved == null)
                throw new InvalidOperationException("Re-save returned no asset");

            string sidecar = saved.GetVmStateSidecarPath();
            if (string.IsNullOrEmpty(sidecar) || !File.Exists(sidecar))
                throw new InvalidOperationException("Converted snapshot has no .vmstate sidecar");

            long newBytes = new FileInfo(fullPath).Length;
            long sidecarBytes = new FileInfo(sidecar).Length;
            string line =
                $"{assetPath}: {oldBytes / (1024.0 * 1024.0):F0} MB → " +
                $"{newBytes / (1024.0 * 1024.0):F0} MB disk + " +
                $"{sidecarBytes / (1024.0 * 1024.0):F0} MB state";
            _report.AppendLine(line);
            Debug.Log($"UnityQemu: converted {line}");
        }
        finally
        {
            if (hadOverride)
                vm.overrideSnapshotLaunchConfig = true;
        }
    }
}
}
