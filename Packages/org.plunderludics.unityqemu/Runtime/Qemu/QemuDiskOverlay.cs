using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityQemu {
/// <summary>
/// Helpers for ephemeral work overlays and atomic qcow2 copies (D2 snapshot prototype).
/// </summary>
public static class QemuDiskOverlay
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

        RunQemuImg(
            $"create -f qcow2 -b \"{backing}\" -F qcow2 \"{overlayPath}\"");
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

    static void RunQemuImg(string arguments)
    {
        string qemuImg = GetQemuImgPath();
        var psi = new ProcessStartInfo
        {
            FileName = qemuImg,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
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
                    $"qemu-img failed ({p.ExitCode}): {arguments}\n{stdout}\n{stderr}");
            }
            if (!string.IsNullOrWhiteSpace(stdout))
                Debug.Log($"qemu-img: {stdout.Trim()}");
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
