using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TriInspector;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Guest launch parameters that affect the QEMU process beyond auto disk/VNC/QMP/GDB.
/// Shared by <see cref="VirtualMachine"/> and <see cref="UqsnapMetadata"/>.
/// </summary>
[Serializable]
public class LaunchConfig
{
    public const int DefaultMemoryMb = 64;
    public const string DefaultUsbEhciId = "uq-ehci";

    public const string DefaultExtraQemuArgs = @"
    -cpu pentium
    -vga cirrus
    -audiodev sdl,id=snd0
    -device sb16,audiodev=snd0
    -netdev user,id=net0
    -device rtl8139,netdev=net0
    -usb -device usb-tablet
    ";

    [Min(1)]
    [LabelText("Memory (MB)")]
    [Tooltip("Guest RAM in megabytes (passed as QEMU -m).")]
    public int memoryMb = DefaultMemoryMb;

    [Tooltip(
        "Add a dedicated USB EHCI controller. Needed for USB vvfat hotplug so storage " +
        "does not share UHCI with usb-tablet (which breaks the guest mouse).")]
    public bool usbEhci = true;

    [ShowIf(nameof(usbEhci))]
    [LabelText("EHCI Device Id")]
    [Tooltip("QEMU device id for the EHCI controller.")]
    public string usbEhciId = DefaultUsbEhciId;

    [ShowIf(nameof(usbEhci))]
    [LabelText("EHCI PCI Addr")]
    [Tooltip(
        "Optional PCI slot (e.g. 0x4 or 04.0) so restore matches a hotplugged instance. " +
        "Leave empty to let QEMU assign the next free slot.")]
    public string usbEhciPciAddr = "";

    [TextArea(3, 10)]
    [LabelText("Extra Qemu Args")]
    [Tooltip(
        "Freeform user QEMU args. Do not put -m, disk, VNC, QMP, GDB, or EHCI here — " +
        "those are configured above / by the VirtualMachine. " +
        "CD and floppy images are configured below. vvfat drives are attached from PeripheralsUI.")]
    public string extraQemuArgs = DefaultExtraQemuArgs;

    [Tooltip("CD-ROM images — drag imported .iso (CdRomAsset) here.")]
    public CdRomAsset[] cdroms;

    [Tooltip(
        "Floppy images for guest A:/B: (read-only). Drag imported .img/.ima (FloppyAsset). " +
        "When empty, an empty A: tray is still reserved so you can insert media from " +
        "Peripherals without a restart.")]
    public FloppyAsset[] floppies;

    public static LaunchConfig CreateDefault() => new LaunchConfig();

    public int ResolvedMemoryMb => memoryMb > 0 ? memoryMb : DefaultMemoryMb;

    public string ResolvedUsbEhciId =>
        string.IsNullOrWhiteSpace(usbEhciId) ? DefaultUsbEhciId : usbEhciId.Trim();

    public LaunchConfig Clone()
    {
        return new LaunchConfig
        {
            memoryMb = ResolvedMemoryMb,
            usbEhci = usbEhci,
            usbEhciId = usbEhciId ?? DefaultUsbEhciId,
            usbEhciPciAddr = usbEhciPciAddr ?? "",
            extraQemuArgs = extraQemuArgs ?? "",
            cdroms = CloneArray(cdroms),
            floppies = CloneArray(floppies),
        };
    }

    public void CopyFrom(LaunchConfig other)
    {
        if (other == null)
            return;
        memoryMb = other.ResolvedMemoryMb;
        usbEhci = other.usbEhci;
        usbEhciId = other.usbEhciId ?? DefaultUsbEhciId;
        usbEhciPciAddr = other.usbEhciPciAddr ?? "";
        extraQemuArgs = other.extraQemuArgs ?? "";
        cdroms = CloneArray(other.cdroms);
        floppies = CloneArray(other.floppies);
    }

    /// <summary>Append a CD if not already present.</summary>
    public bool AddCdRom(CdRomAsset asset) => AppendUnique(ref cdroms, asset);

    /// <summary>Remove a CD if present.</summary>
    public bool RemoveCdRom(CdRomAsset asset) => RemoveItem(ref cdroms, asset);

    /// <summary>Append a floppy image if not already present.</summary>
    public bool AddFloppy(FloppyAsset asset) => AppendUnique(ref floppies, asset);

    /// <summary>Remove a floppy image if present.</summary>
    public bool RemoveFloppy(FloppyAsset asset) => RemoveItem(ref floppies, asset);

    /// <summary>
    /// Append <paramref name="item"/> to <paramref name="array"/> (dropping null slots)
    /// unless already present.
    /// </summary>
    static bool AppendUnique<T>(ref T[] array, T item) where T : UnityEngine.Object
    {
        if (item == null)
            return false;

        var list = new List<T>();
        if (array != null)
        {
            foreach (var existing in array)
            {
                if (existing != null)
                    list.Add(existing);
            }
        }

        if (list.Contains(item))
            return false;

        list.Add(item);
        array = list.ToArray();
        return true;
    }

    static bool RemoveItem<T>(ref T[] array, T item) where T : UnityEngine.Object
    {
        if (item == null || array == null || array.Length == 0)
            return false;

        var list = new List<T>();
        bool removed = false;
        foreach (var existing in array)
        {
            if (existing == null)
                continue;
            if (!removed && ReferenceEquals(existing, item))
            {
                removed = true;
                continue;
            }
            list.Add(existing);
        }

        if (!removed)
            return false;

        array = list.ToArray();
        return true;
    }

    /// <summary>
    /// Emit <c>-device usb-ehci,...</c> when <see cref="usbEhci"/> is on.
    /// </summary>
    public void AppendUsbEhciArgs(IList<string> args)
    {
        if (!usbEhci || args == null)
            return;

        string device = $"usb-ehci,id={ResolvedUsbEhciId}";
        string addr = NormalizePciAddrArg(usbEhciPciAddr);
        if (!string.IsNullOrEmpty(addr))
            device += $",addr={addr}";

        args.Add("-device");
        args.Add(device);
    }

    /// <summary>
    /// Record EHCI on this config for the next durable boot/restore.
    /// When <paramref name="enable"/> is false, only updates id/addr if EHCI is already on
    /// (so extras-owned EHCI on older scenes is not duplicated by also enabling this field).
    /// </summary>
    /// <returns>True if fields changed.</returns>
    public bool RecordUsbEhci(string id = null, string pciAddr = null, bool enable = true)
    {
        if (!enable && !usbEhci)
            return false;

        if (string.IsNullOrWhiteSpace(id))
            id = DefaultUsbEhciId;
        else
            id = id.Trim();

        string newAddr = usbEhciPciAddr ?? "";
        if (!string.IsNullOrWhiteSpace(pciAddr))
        {
            string normalized = NormalizePciAddrArg(pciAddr);
            if (!string.IsNullOrEmpty(normalized))
                newAddr = normalized;
        }

        bool changed = !usbEhci
            || !string.Equals(usbEhciId ?? "", id, StringComparison.Ordinal)
            || !string.Equals(usbEhciPciAddr ?? "", newAddr, StringComparison.OrdinalIgnoreCase);

        usbEhci = true;
        usbEhciId = id;
        usbEhciPciAddr = newAddr;
        return changed;
    }

    /// <summary>Normalize <c>04.0</c> / <c>4.0</c> / <c>0x4</c> to a QEMU <c>addr=</c> value.</summary>
    public static string NormalizePciAddrArg(string pciAddr)
    {
        if (string.IsNullOrWhiteSpace(pciAddr))
            return null;
        string s = pciAddr.Trim();
        var m = Regex.Match(
            s,
            @"^(?:0x)?([0-9a-fA-F]{1,2})(?:\.0)?$",
            RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        int slot = Convert.ToInt32(m.Groups[1].Value, 16);
        return "0x" + slot.ToString("x");
    }

    static T[] CloneArray<T>(T[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<T>();
        return (T[])source.Clone();
    }
}
}
