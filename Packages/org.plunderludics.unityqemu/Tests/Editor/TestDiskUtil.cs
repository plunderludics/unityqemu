using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityQemu.Tests {
/// <summary>Shared helpers for minting tiny throwaway qcow2 images.</summary>
static class TestDiskUtil
{
    /// <summary>
    /// Prefer Library (no spaces) over Application.temporaryCachePath, which includes
    /// the company name and breaks some QEMU backing-file opens on Windows.
    /// </summary>
    public static string TestFixtureDirectory
    {
        get
        {
            string dir = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "UnityQemu", "Tests"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string CreateEmptyQcow2(string fileName, string size = "64M")
    {
        string path = Path.Combine(TestFixtureDirectory, fileName);
        if (File.Exists(path))
            File.Delete(path);

        string qemuImg = Paths.QemuImgPath;
        Assert.IsTrue(File.Exists(qemuImg), $"qemu-img missing: {qemuImg}");

        var psi = new ProcessStartInfo
        {
            FileName = qemuImg,
            Arguments = $"create -f qcow2 \"{path}\" {size}",
            WorkingDirectory = Paths.QemuDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using (var p = Process.Start(psi))
        {
            Assert.IsNotNull(p);
            string stderr = p.StandardError.ReadToEnd();
            Assert.IsTrue(p.WaitForExit(15000), "qemu-img timed out");
            Assert.AreEqual(0, p.ExitCode, stderr);
        }
        Assert.IsTrue(File.Exists(path));
        return path;
    }

    public static DiskAsset DiskAssetForPath(string absoluteQcow2Path)
    {
        var disk = ScriptableObject.CreateInstance<DiskAsset>();
        disk.name = Path.GetFileNameWithoutExtension(absoluteQcow2Path);
        disk.projectRelativeQcow2Path = absoluteQcow2Path;
        return disk;
    }

    public static void SafeDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    public static void DestroyImmediate(Object obj)
    {
        if (obj != null)
            Object.DestroyImmediate(obj);
    }
}
}
