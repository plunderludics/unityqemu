using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Host OS for bundled QEMU trees under <c>qemu~</c>.
/// </summary>
public enum QemuHostKind
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// Editor vs standalone path roots for bundled QEMU and shipped disk/state/ISO files.
/// Mirrors UnityHawk's <c>Paths</c>: binaries live under <c>qemu~</c>, guest images under
/// <c>QemuAssets</c> in player builds (copied by <see cref="BuildProcessing"/>).
/// </summary>
public static class Paths
{
    public const string PackageName = "org.plunderludics.unityqemu";
    public const string QemuDirName = "qemu~";
    public const string QemuAssetsDirName = "QemuAssets";

    public const string QemuHostSubdirWindows = "win";
    /// <summary>Apple Silicon (arm64) macOS QEMU tree.</summary>
    public const string QemuHostSubdirMacOS = "macos";
    /// <summary>Intel (x86_64) macOS QEMU tree.</summary>
    public const string QemuHostSubdirMacOSX64 = "macos-x64";
    public const string QemuHostSubdirLinux = "linux";

    /// <summary>Player-side arch subdirs when a universal Mac build ships both trees.</summary>
    public const string QemuPlayerArchArm64 = "arm64";
    public const string QemuPlayerArchX64 = "x64";

    /// <summary>Project-relative package qemu tree: <c>Packages/…/qemu~</c>.</summary>
    public static string QemuDirRelative =>
        Path.Combine("Packages", PackageName, QemuDirName);

    /// <summary>Absolute package <c>qemu~</c> root (may contain <c>win</c>/<c>macos</c>/<c>linux</c>).</summary>
    public static string QemuRootDir
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", QemuDirRelative));
#else
            // Player builds ship a flat tree for the target OS only.
            return Path.Combine(Application.dataPath, PackageName, QemuDirName);
#endif
        }
    }

    /// <summary>
    /// Absolute path to the QEMU install used by this process.
    /// Editor: <c>qemu~/win|macos|macos-x64|linux</c>. Player: flat tree, or
    /// <c>arm64</c>/<c>x64</c> under <see cref="QemuRootDir"/> for universal Mac builds.
    /// </summary>
    public static string QemuDir
    {
        get
        {
#if UNITY_EDITOR
            return ResolveEditorQemuDir(CurrentHostKind);
#else
            return ResolvePlayerQemuDir();
#endif
        }
    }

    public static QemuHostKind CurrentHostKind
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return QemuHostKind.MacOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return QemuHostKind.Linux;
            return QemuHostKind.Windows;
        }
    }

    /// <summary>True when this process is x86/x64 (Intel Mac or Windows/Linux x64).</summary>
    public static bool IsX64Process =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
        RuntimeInformation.ProcessArchitecture == Architecture.X86;

    public static string HostSubdirName(QemuHostKind kind) => kind switch
    {
        QemuHostKind.MacOS => IsX64Process ? QemuHostSubdirMacOSX64 : QemuHostSubdirMacOS,
        QemuHostKind.Linux => QemuHostSubdirLinux,
        _ => QemuHostSubdirWindows,
    };

    public static string QemuSystemBinaryName(QemuHostKind kind) =>
        kind == QemuHostKind.Windows ? "qemu-system-i386.exe" : "qemu-system-i386";

    public static string QemuImgBinaryName(QemuHostKind kind) =>
        kind == QemuHostKind.Windows ? "qemu-img.exe" : "qemu-img";

    public static string QemuSystemI386Path =>
        Path.Combine(QemuDir, QemuSystemBinaryName(CurrentHostKind));

    public static string QemuImgPath =>
        Path.Combine(QemuDir, QemuImgBinaryName(CurrentHostKind));

    /// <summary>
    /// Preferred editor source tree for the current host arch (macOS picks arm64 vs x64).
    /// </summary>
    public static string ResolveEditorQemuDir(QemuHostKind kind) =>
        kind == QemuHostKind.MacOS
            ? ResolveEditorQemuDir(kind, preferX64: IsX64Process)
            : ResolveEditorQemuDirCore(kind, HostSubdirName(kind));

    /// <summary>
    /// Editor/source tree for packaging. For macOS, <paramref name="preferX64"/> selects
    /// <c>macos-x64</c> vs <c>macos</c> (arm64), with fallback to the other if missing.
    /// </summary>
    public static string ResolveEditorQemuDir(QemuHostKind kind, bool preferX64)
    {
        if (kind != QemuHostKind.MacOS)
            return ResolveEditorQemuDirCore(kind, HostSubdirName(kind));

        string primary = preferX64 ? QemuHostSubdirMacOSX64 : QemuHostSubdirMacOS;
        string fallback = preferX64 ? QemuHostSubdirMacOS : QemuHostSubdirMacOSX64;
        string root = QemuRootDir;
        string preferred = Path.Combine(root, primary);
        if (Directory.Exists(preferred) && HasQemuSystemBinary(preferred, kind))
            return preferred;
        string alt = Path.Combine(root, fallback);
        if (Directory.Exists(alt) && HasQemuSystemBinary(alt, kind))
            return alt;
        return preferred;
    }

    static string ResolveEditorQemuDirCore(QemuHostKind kind, string subdir)
    {
        string root = QemuRootDir;
        string preferred = Path.Combine(root, subdir);
        if (Directory.Exists(preferred) && HasQemuSystemBinary(preferred, kind))
            return preferred;

        // Legacy: Windows binaries directly under qemu~/ (pre multi-host layout).
        if (kind == QemuHostKind.Windows &&
            Directory.Exists(root) &&
            HasQemuSystemBinary(root, QemuHostKind.Windows))
            return root;

        return preferred;
    }

    public static bool HasQemuSystemBinary(string qemuDir, QemuHostKind kind)
    {
        if (string.IsNullOrEmpty(qemuDir))
            return false;
        return File.Exists(Path.Combine(qemuDir, QemuSystemBinaryName(kind)));
    }

#if !UNITY_EDITOR
    static string ResolvePlayerQemuDir()
    {
        string root = QemuRootDir;
        if (CurrentHostKind == QemuHostKind.MacOS)
        {
            string arch = IsX64Process ? QemuPlayerArchX64 : QemuPlayerArchArm64;
            string nested = Path.Combine(root, arch);
            if (HasQemuSystemBinary(nested, QemuHostKind.MacOS))
                return nested;
        }

        return root;
    }
#endif

    /// <summary>
    /// Root used with <see cref="ToBuildRelativeLocation"/> in player builds.
    /// Editor resolution uses the project root instead (see <see cref="ResolveProjectRelativeFile"/>).
    /// </summary>
    public static string QemuAssetsDirForBuild =>
        Path.Combine(Application.dataPath, QemuAssetsDirName);

    /// <summary>
    /// Writable work overlays. Editor: <c>Library/UnityQemu/work</c>.
    /// Player: <c>persistentDataPath/UnityQemu/work</c>.
    /// </summary>
    public static string WorkDirectory
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "UnityQemu", "work"));
#else
            return Path.Combine(Application.persistentDataPath, "UnityQemu", "work");
#endif
        }
    }

    /// <summary>
    /// Normalize a project-relative path for hashing / comparisons (forward slashes, trimmed).
    /// </summary>
    public static string NormalizeProjectRelativePath(string projectRelativePath)
    {
        if (string.IsNullOrEmpty(projectRelativePath))
            return null;
        return projectRelativePath.Replace('\\', '/').Trim();
    }

    /// <summary>
    /// Opaque build filename derived from the project-relative path (SHA-256 hex).
    /// Used when Project Settings → UnityQemu → Obfuscate Guest File Names is on.
    /// </summary>
    public static string ToObfuscatedBuildFileName(string projectRelativePath)
    {
        string norm = NormalizeProjectRelativePath(projectRelativePath);
        if (string.IsNullOrEmpty(norm))
            return null;

        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Convert a project-relative path (<c>Assets/…</c> or <c>Packages/…</c>) to the
    /// location used under <see cref="QemuAssetsDirName"/> in a player build
    /// (same convention as UnityHawk: Assets-relative, with <c>../Packages/…</c> for package files).
    /// </summary>
    public static string ToBuildRelativeLocation(string projectRelativePath)
    {
        if (string.IsNullOrEmpty(projectRelativePath))
            return null;

        string p = NormalizeProjectRelativePath(projectRelativePath);
        if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            return p.Substring("Assets/".Length);
        if (p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            return "../" + p;
        return p;
    }

    /// <summary>
    /// Resolve a project-relative guest-image path to an absolute filesystem path.
    /// Editor: under the project root. Player: under <see cref="QemuAssetsDirForBuild"/>
    /// (plain layout, or SHA-256 filename when the build obfuscated names).
    /// </summary>
    public static string ResolveProjectRelativeFile(string projectRelativePath)
    {
        if (string.IsNullOrEmpty(projectRelativePath))
            return null;
        if (Path.IsPathRooted(projectRelativePath))
            return Path.GetFullPath(projectRelativePath);

#if UNITY_EDITOR
        return Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", projectRelativePath));
#else
        string loc = ToBuildRelativeLocation(projectRelativePath);
        string plain = string.IsNullOrEmpty(loc)
            ? null
            : Path.GetFullPath(Path.Combine(QemuAssetsDirForBuild, loc));
        if (!string.IsNullOrEmpty(plain) && File.Exists(plain))
            return plain;

        string hashedName = ToObfuscatedBuildFileName(projectRelativePath);
        if (!string.IsNullOrEmpty(hashedName))
        {
            string hashed = Path.GetFullPath(Path.Combine(QemuAssetsDirForBuild, hashedName));
            if (File.Exists(hashed))
                return hashed;
        }

        // Prefer the plain layout path for missing-file errors on non-obfuscated builds.
        return plain;
#endif
    }
}
}
