using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityQemu {
/// <summary>
/// Ephemeral work images and qcow2 helpers for the snapshot model.
/// <list type="bullet">
/// <item>Asset images are immutable; only <see cref="WorkDirectory"/> files are written by QEMU.</item>
/// <item>Boots use a thin work overlay on the disk tip; durable machine state lives in <c>.uqsnap</c>.</item>
/// <item>Relative backing is OK for siblings under Assets/; work images always use absolute backing.</item>
/// </list>
/// </summary>
public static class DiskOverlay
{
    /// <summary>Internal savevm tag used on the ephemeral work overlay for session Reload.</summary>
    public const string DurableSaveVmTag = "__unityqemu_state";

    public static string WorkDirectory
    {
        get
        {
            string dir = Paths.WorkDirectory;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static bool IsUnderWorkDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string workDir = Path.GetFullPath(WorkDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(workDir, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetQemuImgPath() => Paths.QemuImgPath;

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

        if (!File.Exists(GetQemuImgPath()))
            throw new FileNotFoundException("qemu-img not found", GetQemuImgPath());

        string overlayDir = Path.GetDirectoryName(overlayPath);
        if (!string.IsNullOrEmpty(overlayDir))
            Directory.CreateDirectory(overlayDir);

        if (File.Exists(overlayPath))
            File.Delete(overlayPath);

        string backing = PreferBackingFileArgument(overlayPath, baseQcow2Path);
        RunQemuImg("create", "-f", "qcow2", "-b", backing, "-F", "qcow2", overlayPath);
    }

    /// <summary>
    /// Create a fresh thin work overlay under Library/UnityQemu/work for the given session id.
    /// </summary>
    public static string CreateWorkOverlay(string baseQcow2Path, string sessionId)
    {
        string overlayPath = WorkOverlayPathForSession(sessionId);
        CreateOverlay(baseQcow2Path, overlayPath);
        return overlayPath;
    }

    static string WorkOverlayPathForSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(WorkDirectory);
        return Path.Combine(WorkDirectory, $"{SanitizeFileName(sessionId)}.qcow2");
    }

    /// <summary>
    /// Path for the Nth extra work layer of a session (created by live external
    /// snapshots during D4 saves). Shares the session prefix so the orphan sweep
    /// treats the whole chain as one unit.
    /// </summary>
    public static string WorkLayerPathForSession(string sessionId, int layer)
    {
        if (string.IsNullOrEmpty(sessionId))
            sessionId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(WorkDirectory);
        return Path.Combine(WorkDirectory, $"{SanitizeFileName(sessionId)}_l{layer}.qcow2");
    }

    /// <summary>
    /// Best-effort delete of a work image and its <c>.tmp</c> leftover.
    /// No-op for paths outside the work directory.
    /// </summary>
    public static void TryDeleteWorkFile(string path)
    {
        if (!IsUnderWorkDirectory(path))
            return;
        TryDeleteFile(path);
        TryDeleteFile(path + ".tmp");
    }

    /// <summary>
    /// Delete work images (and .tmp leftovers) that belong to none of the given live
    /// sessions — e.g. files from previous editor sessions, whose instance-id-based names
    /// no longer match any VirtualMachine. Files still open in a running QEMU fail the
    /// delete and are skipped; they get another chance on the next sweep.
    /// </summary>
    public static void CleanupOrphanedWorkFiles(IEnumerable<string> activeSessionIds)
    {
        // Sessions own "{id}.qcow2" plus any "{id}_lN.qcow2" layer files (D4 saves).
        var keepPrefixes = new List<string>();
        if (activeSessionIds != null)
        {
            foreach (string id in activeSessionIds)
            {
                if (!string.IsNullOrEmpty(id))
                    keepPrefixes.Add(SanitizeFileName(id));
            }
        }

        foreach (string file in Directory.GetFiles(WorkDirectory))
        {
            string name = Path.GetFileName(file);
            bool isTmp = name.EndsWith(".qcow2.tmp", StringComparison.OrdinalIgnoreCase);
            if (!isTmp && !name.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase))
                continue;

            string qcow2Name = isTmp ? name.Substring(0, name.Length - ".tmp".Length) : name;
            string stem = qcow2Name.Substring(0, qcow2Name.Length - ".qcow2".Length);
            bool kept = false;
            foreach (string prefix in keepPrefixes)
            {
                if (string.Equals(stem, prefix, StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith(prefix + "_l", StringComparison.OrdinalIgnoreCase))
                {
                    kept = true;
                    break;
                }
            }
            if (kept)
                continue;

            if (TryDeleteFile(file))
                UnityEngine.Debug.Log($"UnityQemu: deleted orphaned work image {name}");
        }
    }

    static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false; // still open (running QEMU) — retried on a later sweep
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Write a minimal thin overlay: guest-visible content of <paramref name="sourcePath"/>
    /// (including its backing chain), stored as only the clusters that differ from
    /// <paramref name="backingPath"/>. Drops internal snapshots (quick-save savevm blobs)
    /// by construction — <c>qemu-img convert</c> reads active content only.
    /// One command covers save-child and save-sibling/overwrite;
    /// only the backing choice differs. Safe while QEMU holds the source read-only
    /// (<c>-U</c> shared open); temp + rename so readers never see a partial file.
    /// </summary>
    public static void ConvertThin(string sourcePath, string backingPath, string destPath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Convert source qcow2 not found", sourcePath);
        if (string.IsNullOrEmpty(backingPath) || !File.Exists(backingPath))
            throw new FileNotFoundException("Convert backing qcow2 not found", backingPath);

        string destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        string tmp = destPath + ".tmpconv";
        if (File.Exists(tmp))
            File.Delete(tmp);
        try
        {
            RunQemuImg(
                600_000,
                "convert", "-U", "-O", "qcow2",
                "-B", Path.GetFullPath(backingPath).Replace('\\', '/'), "-F", "qcow2",
                sourcePath, tmp);
            if (File.Exists(destPath))
                File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Make every declared qcow2 backing-file header agree with the Unity asset graph.
    /// QEMU must not have any of these images open while this runs.
    /// </summary>
    public static void EnsureBackingChain(DiskAsset disk)
    {
        EnsureBackingChain(disk, new HashSet<DiskAsset>());
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

    /// <summary>
    /// Header-only repair so <paramref name="overlayPath"/> names
    /// <paramref name="expectedBackingPath"/> (junctions / relative→absolute after work copy).
    /// </summary>
    public static void EnsureBackingMatches(string overlayPath, string expectedBackingPath)
    {
        if (string.IsNullOrEmpty(overlayPath) || !File.Exists(overlayPath))
            throw new FileNotFoundException("Overlay qcow2 not found", overlayPath);
        if (string.IsNullOrEmpty(expectedBackingPath) || !File.Exists(expectedBackingPath))
            throw new FileNotFoundException("Expected backing qcow2 not found", expectedBackingPath);

        overlayPath = Path.GetFullPath(overlayPath);
        expectedBackingPath = Path.GetFullPath(expectedBackingPath);

        string actualBackingPath = GetBackingPath(overlayPath);
        if (PathsEqual(actualBackingPath, expectedBackingPath))
            return;

        Debug.LogWarning(
            $"UnityQemu backing path mismatch for '{overlayPath}'. " +
            $"qcow2 header='{actualBackingPath ?? "<none>"}', expected='{expectedBackingPath}'. " +
            "Rewriting header with qemu-img rebase -u.");

        RebaseHeaderOnto(overlayPath, expectedBackingPath);
        string repaired = GetBackingPath(overlayPath);
        if (!PathsEqual(repaired, expectedBackingPath))
            throw new InvalidOperationException(
                $"qemu-img rebase -u left backing as '{repaired ?? "<none>"}' " +
                $"(expected '{expectedBackingPath}')");
    }

    /// <summary>
    /// Header-only rewrite of <paramref name="overlayPath"/>'s backing path
    /// (<c>qemu-img rebase -u</c>). QEMU must not have the image open.
    /// </summary>
    public static void RebaseHeaderOnto(string overlayPath, string newBackingPath)
    {
        if (string.IsNullOrEmpty(overlayPath) || !File.Exists(overlayPath))
            throw new FileNotFoundException("Overlay qcow2 not found", overlayPath);
        if (string.IsNullOrEmpty(newBackingPath) || !File.Exists(newBackingPath))
            throw new FileNotFoundException("New backing qcow2 not found", newBackingPath);

        if (PathsEqual(overlayPath, newBackingPath))
            throw new InvalidOperationException(
                $"Cannot rebase '{overlayPath}' onto itself.");

        overlayPath = Path.GetFullPath(overlayPath);
        string backingArg = PreferBackingFileArgument(overlayPath, newBackingPath);
        RunQemuImg(
            "rebase", "-u", "-f", "qcow2",
            "-b", backingArg, "-F", "qcow2", overlayPath);
    }

    /// <summary>Fully resolved backing filename, or null for a base image.</summary>
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

    /// <summary>
    /// True if both paths name the same file, including Windows junctions
    /// (volume serial + file index).
    /// </summary>
    public static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return string.Equals(a, b, StringComparison.Ordinal);
        string fa = NormalizePathForCompare(a);
        string fb = NormalizePathForCompare(b);
        if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase))
            return true;
        return SameFilesystemObject(fa, fb);
    }

    static string NormalizePathForCompare(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Backing argument for qemu-img: relative only when parent is a same-folder sibling
    /// under Assets (junction-friendly). Always absolute for work images.
    /// </summary>
    static string PreferBackingFileArgument(string overlayPath, string backingPath)
    {
        overlayPath = Path.GetFullPath(overlayPath);
        backingPath = Path.GetFullPath(backingPath);

        if (IsUnderWorkDirectory(overlayPath))
            return backingPath.Replace('\\', '/');

        string overlayDir = Path.GetDirectoryName(overlayPath) ?? ".";
        string backing = MakeRelativePath(overlayDir, backingPath);
        if (string.IsNullOrEmpty(backing) || backing.Contains(".."))
            backing = backingPath;
        return backing.Replace('\\', '/');
    }

    static bool SameFilesystemObject(string pathA, string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB))
            return false;

        if (Application.platform != RuntimePlatform.WindowsEditor &&
            Application.platform != RuntimePlatform.WindowsPlayer)
            return false;

        try
        {
            return SameFileByWindowsFileId(pathA, pathB);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"UnityQemu: file-identity compare failed: {e.Message}");
            return false;
        }
    }

    static bool SameFileByWindowsFileId(string pathA, string pathB)
    {
        using (SafeFileHandle handleA = OpenForFileId(pathA))
        using (SafeFileHandle handleB = OpenForFileId(pathB))
        {
            if (handleA.IsInvalid || handleB.IsInvalid)
                return false;
            if (!GetFileInformationByHandle(handleA, out BY_HANDLE_FILE_INFORMATION infoA))
                return false;
            if (!GetFileInformationByHandle(handleB, out BY_HANDLE_FILE_INFORMATION infoB))
                return false;
            return infoA.VolumeSerialNumber == infoB.VolumeSerialNumber &&
                   infoA.FileIndexHigh == infoB.FileIndexHigh &&
                   infoA.FileIndexLow == infoB.FileIndexLow;
        }
    }

    static SafeFileHandle OpenForFileId(string path) =>
        CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

    const uint FileReadAttributes = 0x0080;
    const uint FileShareRead = 0x0001;
    const uint FileShareWrite = 0x0002;
    const uint FileShareDelete = 0x0004;
    const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    static string RunQemuImg(params string[] arguments) =>
        RunQemuImg(120_000, arguments);

    static string RunQemuImg(int timeoutMs, params string[] arguments)
    {
        string qemuImg = GetQemuImgPath();
        var psi = new ProcessStartInfo
        {
            FileName = qemuImg,
            WorkingDirectory = Paths.QemuDir,
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
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* ignore */ }
                throw new TimeoutException(
                    $"qemu-img timed out after {timeoutMs}ms: {string.Join(" ", arguments)}");
            }
            if (p.ExitCode != 0)
            {
                string exitHint = p.ExitCode < 0
                    ? $"qemu-img crashed ({p.ExitCode} / 0x{(uint)p.ExitCode:X8})"
                    : $"qemu-img failed ({p.ExitCode})";
                throw new Exception(
                    $"{exitHint}: {string.Join(" ", arguments)}\n{stdout}\n{stderr}");
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
            return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}
}
