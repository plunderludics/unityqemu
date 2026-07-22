using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityQemu {
/// <summary>
/// Helpers for ephemeral work overlays and atomic qcow2 copies.
/// </summary>
public static class DiskOverlay
{
    /// <summary>Internal savevm tag embedded in durable snapshot qcow2 copies.</summary>
    public const string DurableSaveVmTag = "__unityqemu_state";

    public static string WorkDirectory
    {
        get
        {
            string dir = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "UnityQemu", "work"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string GetQemuImgPath()
    {
        return Path.GetFullPath(Path.Combine(
            Application.dataPath, "..",
            "Packages", "org.plunderludics.unityqemu", "qemu~", "qemu-img.exe"));
    }

    /// <summary>
    /// Create a qcow2 overlay backed by <paramref name="baseQcow2Path"/>.
    /// Overwrites <paramref name="overlayPath"/> if it already exists.
    /// </summary>
    public static void CreateOverlay(string baseQcow2Path, string overlayPath)
    {
        if (string.IsNullOrEmpty(baseQcow2Path) || !File.Exists(baseQcow2Path))
            throw new FileNotFoundException("Base qcow2 not found", baseQcow2Path);
        if (string.IsNullOrEmpty(overlayPath))
            throw new ArgumentException("overlayPath required");

        string qemuImg = GetQemuImgPath();
        if (!File.Exists(qemuImg))
            throw new FileNotFoundException("qemu-img not found", qemuImg);

        string overlayDir = Path.GetDirectoryName(overlayPath);
        if (!string.IsNullOrEmpty(overlayDir))
            Directory.CreateDirectory(overlayDir);

        if (File.Exists(overlayPath))
            File.Delete(overlayPath);

        // Prefer a relative backing path so the pair stays movable when kept together.
        string backing = MakeRelativePath(overlayDir ?? ".", baseQcow2Path);
        if (string.IsNullOrEmpty(backing) || backing.Contains(".."))
            backing = Path.GetFullPath(baseQcow2Path);

        // qemu-img on Windows accepts forward slashes.
        backing = backing.Replace('\\', '/');

        RunQemuImg("create", "-f", "qcow2", "-b", backing, "-F", "qcow2", overlayPath);
    }

    /// <summary>
    /// Create a fresh work overlay under Library/UnityQemu/work for the given session id.
    /// </summary>
    public static string CreateWorkOverlay(string baseQcow2Path, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString("N");
        string safe = SanitizeFileName(sessionId);
        string overlayPath = Path.Combine(WorkDirectory, $"{safe}.qcow2");
        CreateOverlay(baseQcow2Path, overlayPath);
        return overlayPath;
    }

    /// <summary>
    /// Replace work overlay contents with a copy of an existing qcow2 (e.g. durable snapshot image).
    /// </summary>
    public static string ReplaceWorkOverlayFromCopy(string sourceQcow2Path, string sessionId)
    {
        if (string.IsNullOrEmpty(sourceQcow2Path) || !File.Exists(sourceQcow2Path))
            throw new FileNotFoundException("Source qcow2 not found", sourceQcow2Path);

        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString("N");
        string safe = SanitizeFileName(sessionId);
        string overlayPath = Path.Combine(WorkDirectory, $"{safe}.qcow2");
        Directory.CreateDirectory(WorkDirectory);
        CopyAtomic(sourceQcow2Path, overlayPath);
        return overlayPath;
    }

    /// <summary>Copy file via temp + rename so readers never see a partial destination.</summary>
    public static void CopyAtomic(string sourcePath, string destPath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source not found", sourcePath);

        string destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        string tmp = destPath + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
        File.Copy(sourcePath, tmp, overwrite: true);
        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tmp, destPath);
    }

    /// <summary>
    /// Make every declared qcow2 backing-file header agree with the Unity asset graph.
    /// QEMU must not have any of these images open while this runs.
    /// </summary>
    public static void EnsureBackingChain(DiskAsset disk)
    {
        EnsureBackingChain(disk, new HashSet<DiskAsset>());
    }

    /// <summary>
    /// Validate/repair a .uqsnap's backing header against <see cref="SnapshotAsset.backingDisk"/>,
    /// then walk that disk's chain.
    /// </summary>
    public static void EnsureSnapshotBacking(SnapshotAsset snapshot)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.backingDisk == null)
        {
            string message =
                $"UnityQemu snapshot '{snapshot.name}' has no backingDisk reference";
            Debug.LogWarning(message);
            throw new InvalidOperationException(message);
        }

        string imagePath = snapshot.GetImageFilesystemPath();
        EnsureBackingChain(snapshot.backingDisk);
        EnsureBackingMatches(imagePath, snapshot.backingDisk.GetQcow2FilesystemPath());
    }

    static void EnsureBackingChain(DiskAsset disk, HashSet<DiskAsset> visited)
    {
        if (disk == null || !visited.Add(disk))
        {
            if (disk != null)
            {
                string message = $"UnityQemu backing-disk cycle detected at '{disk.name}'";
                Debug.LogWarning(message);
                throw new InvalidOperationException(message);
            }
            return;
        }

        if (disk.backingDisk == null)
        {
            string imagePath = disk.GetQcow2FilesystemPath();
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                Debug.LogWarning(
                    $"UnityQemu disk asset '{disk.name}' does not resolve to a readable qcow2 at '{imagePath}'");
                throw new FileNotFoundException("Disk qcow2 not found", imagePath);
            }

            string undeclaredBacking;
            try
            {
                undeclaredBacking = GetBackingPath(imagePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"UnityQemu could not inspect '{imagePath}'. The image was not started. {e.Message}");
                throw;
            }
            if (!string.IsNullOrEmpty(undeclaredBacking))
            {
#if UNITY_EDITOR
                DiskAsset inferred = DiskAsset.FindByFilesystemPath(undeclaredBacking);
                if (inferred != null)
                {
                    disk.backingDisk = inferred;
                    disk.SaveInferredBackingDisk();
                    Debug.Log(
                        $"UnityQemu inferred backingDisk for '{disk.name}' from its qcow2 header: " +
                        $"'{inferred.name}'");
                    EnsureBackingChain(disk.backingDisk, visited);
                    EnsureBackingMatches(imagePath, inferred.GetQcow2FilesystemPath());
                    return;
                }
#endif
                Debug.LogWarning(
                    $"UnityQemu disk asset '{disk.name}' has qcow2 backing '{undeclaredBacking}', " +
                    "but backingDisk is not assigned. It can boot, but asset moves cannot be auto-repaired.");
            }
            return;
        }

        EnsureBackingChain(disk.backingDisk, visited);
        EnsureBackingMatches(
            disk.GetQcow2FilesystemPath(),
            disk.backingDisk.GetQcow2FilesystemPath());
    }

    /// <summary>Repair an overlay's qcow2 backing path from its Unity asset reference.</summary>
    public static void EnsureBackingMatches(string overlayPath, string expectedBackingPath)
    {
        if (string.IsNullOrEmpty(overlayPath) || !File.Exists(overlayPath))
        {
            Debug.LogWarning($"UnityQemu cannot validate missing overlay qcow2 '{overlayPath}'");
            throw new FileNotFoundException("Overlay qcow2 not found", overlayPath);
        }
        if (string.IsNullOrEmpty(expectedBackingPath) || !File.Exists(expectedBackingPath))
        {
            Debug.LogWarning(
                $"UnityQemu cannot validate '{overlayPath}': expected backing qcow2 " +
                $"was not found at '{expectedBackingPath}'");
            throw new FileNotFoundException("Expected backing qcow2 not found", expectedBackingPath);
        }

        overlayPath = Path.GetFullPath(overlayPath);
        expectedBackingPath = Path.GetFullPath(expectedBackingPath);
        string actualBackingPath;
        try
        {
            actualBackingPath = GetBackingPath(overlayPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"UnityQemu could not inspect backing path for '{overlayPath}'. " +
                $"The image was not started. {e.Message}");
            throw;
        }
        if (PathsEqual(actualBackingPath, expectedBackingPath))
            return;

        Debug.LogWarning(
            $"UnityQemu backing path mismatch for '{overlayPath}'. " +
            $"qcow2 header='{actualBackingPath ?? "<none>"}', Unity asset='{expectedBackingPath}'. " +
            "Attempting qemu-img rebase -u repair.");

        try
        {
            RunQemuImg(
                "rebase", "-u", "-f", "qcow2",
                "-b", expectedBackingPath, "-F", "qcow2", overlayPath);
            string repairedBackingPath = GetBackingPath(overlayPath);
            if (!PathsEqual(repairedBackingPath, expectedBackingPath))
                throw new InvalidOperationException(
                    $"qemu-img completed but backing path is still '{repairedBackingPath ?? "<none>"}'");
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"UnityQemu could not repair backing path for '{overlayPath}'. " +
                $"The image was not started. {e.Message}");
            throw;
        }
    }

    /// <summary>Return the fully resolved backing filename in a qcow2 header, or null for a base image.</summary>
    public static string GetBackingPath(string imagePath)
    {
        string json = RunQemuImg("info", "--output=json", imagePath);
        var info = JObject.Parse(json);
        string fullBacking = (string)info["full-backing-filename"];
        if (!string.IsNullOrEmpty(fullBacking) && Path.IsPathRooted(fullBacking))
            return Path.GetFullPath(fullBacking);

        string backing = (string)info["backing-filename"] ?? fullBacking;
        if (string.IsNullOrEmpty(backing))
            return null;
        if (Path.IsPathRooted(backing))
            return Path.GetFullPath(backing);
        string imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? ".";
        return Path.GetFullPath(Path.Combine(imageDir, backing));
    }

    static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return string.Equals(a, b, StringComparison.Ordinal);
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    static string RunQemuImg(params string[] arguments)
    {
        string qemuImg = GetQemuImgPath();
        var psi = new ProcessStartInfo
        {
            FileName = qemuImg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using (var p = Process.Start(psi))
        {
            if (p == null)
                throw new Exception("Failed to start qemu-img");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0)
            {
                throw new Exception(
                    $"qemu-img failed ({p.ExitCode}): {string.Join(" ", arguments)}\n{stdout}\n{stderr}");
            }
            if (!string.IsNullOrWhiteSpace(stdout) &&
                (arguments.Length == 0 || arguments[0] != "info"))
                Debug.Log($"qemu-img: {stdout.Trim()}");
            return stdout;
        }
    }

    static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length > 0 ? sb.ToString() : "session";
    }

    static string MakeRelativePath(string fromDir, string toFile)
    {
        try
        {
            string from = Path.GetFullPath(fromDir);
            if (!from.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
                !from.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                from += Path.DirectorySeparatorChar;
            var fromUri = new Uri(from);
            var toUri = new Uri(Path.GetFullPath(toFile));
            string rel = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
            return rel;
        }
        catch
        {
            return null;
        }
    }
}
}
