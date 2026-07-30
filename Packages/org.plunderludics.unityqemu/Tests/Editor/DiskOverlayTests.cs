using System.IO;
using NUnit.Framework;

namespace UnityQemu.Tests {
public class DiskOverlayTests
{
    [Test]
    public void CreateOverlay_SetsBackingToBaseImage()
    {
        string basePath = null;
        string overlayPath = null;
        try
        {
            basePath = TestDiskUtil.CreateEmptyQcow2("base.qcow2");
            overlayPath = Path.Combine(
                Path.GetDirectoryName(basePath)!, "overlay.qcow2");

            DiskOverlay.CreateOverlay(basePath, overlayPath);

            Assert.IsTrue(File.Exists(overlayPath));
            string backing = DiskOverlay.GetBackingPath(overlayPath);
            Assert.IsTrue(
                DiskOverlay.PathsEqual(backing, basePath),
                $"Expected backing '{basePath}', got '{backing}'");
        }
        finally
        {
            TestDiskUtil.SafeDelete(overlayPath);
            TestDiskUtil.SafeDelete(basePath);
        }
    }

    [Test]
    public void IsUnderWorkDirectory_TrueOnlyForWorkPaths()
    {
        string workFile = Path.Combine(DiskOverlay.WorkDirectory, "probe.qcow2");
        Assert.IsTrue(DiskOverlay.IsUnderWorkDirectory(workFile));
        Assert.IsFalse(DiskOverlay.IsUnderWorkDirectory(Paths.QemuImgPath));
    }

    [Test]
    public void ConvertThin_WritesSiblingBackedByParent()
    {
        string basePath = null;
        string overlayPath = null;
        string thinPath = null;
        try
        {
            basePath = TestDiskUtil.CreateEmptyQcow2("convert-base.qcow2");
            string dir = Path.GetDirectoryName(basePath)!;
            overlayPath = Path.Combine(dir, "convert-overlay.qcow2");
            thinPath = Path.Combine(dir, "convert-thin.qcow2");

            DiskOverlay.CreateOverlay(basePath, overlayPath);
            DiskOverlay.ConvertThin(overlayPath, basePath, thinPath);

            Assert.IsTrue(File.Exists(thinPath));
            Assert.IsTrue(
                DiskOverlay.PathsEqual(DiskOverlay.GetBackingPath(thinPath), basePath));
        }
        finally
        {
            TestDiskUtil.SafeDelete(thinPath);
            TestDiskUtil.SafeDelete(overlayPath);
            TestDiskUtil.SafeDelete(basePath);
        }
    }
}
}
