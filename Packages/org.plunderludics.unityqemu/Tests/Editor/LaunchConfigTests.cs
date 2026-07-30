using System.Collections.Generic;
using NUnit.Framework;

namespace UnityQemu.Tests {
public class LaunchConfigTests
{
    [TestCase("0x4", "0x4")]
    [TestCase("04.0", "0x4")]
    [TestCase("4.0", "0x4")]
    [TestCase("0x1a", "0x1a")]
    [TestCase("", null)]
    [TestCase("not-a-slot", null)]
    public void NormalizePciAddrArg(string input, string expected)
    {
        Assert.AreEqual(expected, LaunchConfig.NormalizePciAddrArg(input));
    }

    [Test]
    public void Clone_CopiesScalarsAndLeavesArraysIndependent()
    {
        var src = LaunchConfig.CreateDefault();
        src.memoryMb = 128;
        src.usbEhci = false;
        src.extraQemuArgs = "-cpu 486";

        LaunchConfig clone = src.Clone();
        Assert.AreEqual(128, clone.memoryMb);
        Assert.IsFalse(clone.usbEhci);
        Assert.AreEqual("-cpu 486", clone.extraQemuArgs);

        clone.memoryMb = 256;
        Assert.AreEqual(128, src.memoryMb);
    }

    [Test]
    public void AppendUsbEhciArgs_EmitsDeviceWithOptionalAddr()
    {
        var cfg = new LaunchConfig
        {
            usbEhci = true,
            usbEhciId = "uq-ehci",
            usbEhciPciAddr = "0x5",
        };
        var args = new List<string>();
        cfg.AppendUsbEhciArgs(args);
        Assert.AreEqual(2, args.Count);
        Assert.AreEqual("-device", args[0]);
        Assert.AreEqual("usb-ehci,id=uq-ehci,addr=0x5", args[1]);
    }

    [Test]
    public void RecordUsbEhci_EnablesAndNormalizesAddr()
    {
        var cfg = new LaunchConfig { usbEhci = false };
        Assert.IsTrue(cfg.RecordUsbEhci("my-ehci", "04.0"));
        Assert.IsTrue(cfg.usbEhci);
        Assert.AreEqual("my-ehci", cfg.usbEhciId);
        Assert.AreEqual("0x4", cfg.usbEhciPciAddr);
    }
}
}
