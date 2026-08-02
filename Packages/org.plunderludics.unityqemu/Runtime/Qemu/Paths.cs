using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityQemu {
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

    /// <summary>Project-relative package qemu tree: <c>Packages/…/qemu~</c>.</summary>
    public static string QemuDirRelative =>
        Path.Combine("Packages", PackageName, QemuDirName);

    /// <summary>
    /// Absolute path to the bundled QEMU install directory.
    /// Editor: package <c>qemu~</c>. Player: <c>{exe}_Data/org.plunderludics.unityqemu/qemu~</c>.
    /// </summary>
    public static string QemuDir
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", QemuDirRelative));
#else
            return Path.Combine(Application.dataPath, PackageName, QemuDirName);
#endif
        }
    }

    public static string QemuSystemI386Path =>
        Path.Combine(QemuDir, "qemu-system-i386.exe");

    public static string QemuImgPath =>
        Path.Combine(QemuDir, "qemu-img.exe");

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
