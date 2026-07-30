using NUnit.Framework;

namespace UnityQemu.Tests {
public class QemuPortAllocatorTests
{
    [Test]
    public void LooksLikeAddressInUse_MatchesCommonBindErrors()
    {
        Assert.IsTrue(QemuPortAllocator.LooksLikeAddressInUse("Address already in use"));
        Assert.IsTrue(QemuPortAllocator.LooksLikeAddressInUse("Failed to start VNC server"));
        Assert.IsFalse(QemuPortAllocator.LooksLikeAddressInUse("guest booted ok"));
        Assert.IsFalse(QemuPortAllocator.LooksLikeAddressInUse(null));
    }

    [Test]
    public void PreferredVncDisplay_IsStableInRange()
    {
        int a = QemuPortAllocator.PreferredVncDisplay();
        int b = QemuPortAllocator.PreferredVncDisplay();
        Assert.AreEqual(a, b);
        Assert.GreaterOrEqual(a, 0);
        Assert.LessOrEqual(a, 99);
    }

    [Test]
    public void ClaimEphemeralPort_HoldsThenReleases()
    {
        using (QemuPortAllocator.HeldPort held = QemuPortAllocator.ClaimEphemeralPort())
        {
            Assert.Greater(held.Port, 0);
            Assert.IsFalse(QemuPortAllocator.IsPortFree(held.Port));
            held.HandOff();
            // After handoff the OS bind is released but the port stays claimed in-process.
            Assert.IsTrue(QemuPortAllocator.IsPortFree(held.Port));
        }
    }
}
}
