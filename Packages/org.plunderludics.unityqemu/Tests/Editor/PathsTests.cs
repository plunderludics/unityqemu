using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityQemu.Tests {
public class PathsTests
{
    [Test]
    public void ToBuildRelativeLocation_StripsAssetsPrefix()
    {
        Assert.AreEqual(
            "qemu/disk/win95/win95.qcow2",
            Paths.ToBuildRelativeLocation("Assets/qemu/disk/win95/win95.qcow2"));
    }

    [Test]
    public void ToBuildRelativeLocation_PackagesBecomeSiblingOfBuildAssets()
    {
        Assert.AreEqual(
            "../Packages/org.plunderludics.unityqemu/Tests/x.qcow2",
            Paths.ToBuildRelativeLocation(
                "Packages/org.plunderludics.unityqemu/Tests/x.qcow2"));
    }

    [Test]
    public void ResolveProjectRelativeFile_RootedPath_Passthrough()
    {
        string rooted = Path.GetFullPath(Path.Combine(Application.temporaryCachePath, "x.qcow2"));
        Assert.AreEqual(rooted, Paths.ResolveProjectRelativeFile(rooted));
    }

    [Test]
    public void ToObfuscatedBuildFileName_IsStableSha256Hex()
    {
        const string path = "Assets/qemu/disk/win95/win95.qcow2";
        string a = Paths.ToObfuscatedBuildFileName(path);
        string b = Paths.ToObfuscatedBuildFileName(path.Replace('/', '\\'));
        Assert.AreEqual(64, a.Length);
        Assert.AreEqual(a, b);
        Assert.AreEqual(a, Paths.ToObfuscatedBuildFileName("  " + path + "  "));
    }

    [Test]
    public void BundledQemuBinaries_Exist()
    {
        Assert.IsTrue(Directory.Exists(Paths.QemuDir), $"Missing QEMU dir: {Paths.QemuDir}");
        Assert.IsTrue(File.Exists(Paths.QemuSystemI386Path), Paths.QemuSystemI386Path);
        Assert.IsTrue(File.Exists(Paths.QemuImgPath), Paths.QemuImgPath);
    }

    [Test]
    public void ResolveEditorQemuDir_Windows_PrefersWinSubdirWhenPresent()
    {
        string root = Paths.QemuRootDir;
        string win = Path.Combine(root, Paths.QemuHostSubdirWindows);
        string resolved = Paths.ResolveEditorQemuDir(QemuHostKind.Windows);
        if (Directory.Exists(win) && Paths.HasQemuSystemBinary(win, QemuHostKind.Windows))
            Assert.AreEqual(Path.GetFullPath(win), Path.GetFullPath(resolved));
        else
            Assert.AreEqual(Path.GetFullPath(root), Path.GetFullPath(resolved));
    }

    [Test]
    public void HostBinaryNames_MatchOs()
    {
        Assert.AreEqual("qemu-system-i386.exe", Paths.QemuSystemBinaryName(QemuHostKind.Windows));
        Assert.AreEqual("qemu-img.exe", Paths.QemuImgBinaryName(QemuHostKind.Windows));
        Assert.AreEqual("qemu-system-i386", Paths.QemuSystemBinaryName(QemuHostKind.MacOS));
        Assert.AreEqual("qemu-img", Paths.QemuImgBinaryName(QemuHostKind.Linux));
    }

    [Test]
    public void ResolveEditorQemuDir_Mac_PrefersRequestedArch()
    {
        string root = Paths.QemuRootDir;
        string arm = Path.Combine(root, Paths.QemuHostSubdirMacOS);
        string x64 = Path.Combine(root, Paths.QemuHostSubdirMacOSX64);
        if (Directory.Exists(arm) && Paths.HasQemuSystemBinary(arm, QemuHostKind.MacOS))
        {
            Assert.AreEqual(
                Path.GetFullPath(arm),
                Path.GetFullPath(Paths.ResolveEditorQemuDir(QemuHostKind.MacOS, preferX64: false)));
        }

        if (Directory.Exists(x64) && Paths.HasQemuSystemBinary(x64, QemuHostKind.MacOS))
        {
            Assert.AreEqual(
                Path.GetFullPath(x64),
                Path.GetFullPath(Paths.ResolveEditorQemuDir(QemuHostKind.MacOS, preferX64: true)));
        }
    }

    [Test]
    public void QueryBundledQemuVersion_ReturnsQemuBanner()
    {
        string version = VirtualMachine.QueryBundledQemuVersion();
        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        StringAssert.Contains("QEMU", version);
    }
}
}
