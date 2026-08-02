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
    public void QueryBundledQemuVersion_ReturnsQemuBanner()
    {
        string version = VirtualMachine.QueryBundledQemuVersion();
        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        StringAssert.Contains("QEMU", version);
    }
}
}
