using System;
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

    public const string DefaultExtraQemuArgs = @"
    -cpu pentium
    -vga cirrus
    -audiodev dsound,id=snd0
    -device sb16,audiodev=snd0
    -netdev user,id=net0
    -device rtl8139,netdev=net0
    -usb -device usb-tablet
    ";

    // Strip accidental "-m N" / "-mN" from freeform args (memory comes from memoryMb).
    static readonly Regex MemoryArgRegex = new Regex(
        @"(?:^|\s+)-m\s*\d+\b",
        RegexOptions.CultureInvariant);

    [Min(1)]
    [LabelText("Memory (MB)")]
    [Tooltip("Guest RAM in megabytes (passed as QEMU -m).")]
    public int memoryMb = DefaultMemoryMb;

    [TextArea(3, 10)]
    [LabelText("Extra Qemu Args")]
    [Tooltip(
        "Freeform QEMU args excluding memory (-m), disk, VNC, QMP, and GDB. " +
        "CD, floppy, and host-folder shares are configured below.")]
    public string extraQemuArgs = DefaultExtraQemuArgs;

    [Tooltip("CD-ROM images — drag imported .iso (CdRomAsset) here.")]
    public CdRomAsset[] cdroms;

    [Tooltip(
        "Floppy sources for guest A:/B: (tiny ~1.44MB vvfat or .img, attached read-only " +
        "so savevm/loadvm keep working). When empty, an empty A: tray is still reserved " +
        "so PeripheralsUI can hot-insert without a restart. " +
        "For larger or writable shares prefer Host Folders or SMB.")]
    public UnityEngine.Object[] floppies;

    [Tooltip(
        "Host folders shared into the guest as extra IDE disks via QEMU vvfat " +
        "(fat:rw:…). Snapshot of folder at boot/attach — not a live sync. " +
        "Drag a project folder here.")]
    [LabelText("Host Folders (vvfat)")]
    public UnityEngine.Object[] hostFolders;

    [Tooltip(
        "Project folder exported over QEMU user-mode SMB (live-ish network share). " +
        "Requires -netdev user in Extra Qemu Args (default has it). " +
        "In the guest open \\\\10.0.2.4\\qemu")]
    [LabelText("SMB Share Folder")]
    public UnityEngine.Object smbShareFolder;

    public static LaunchConfig CreateDefault() => new LaunchConfig();

    public int ResolvedMemoryMb => memoryMb > 0 ? memoryMb : DefaultMemoryMb;

    public LaunchConfig Clone()
    {
        return new LaunchConfig
        {
            memoryMb = ResolvedMemoryMb,
            extraQemuArgs = extraQemuArgs ?? "",
            cdroms = CloneArray(cdroms),
            floppies = CloneArray(floppies),
            hostFolders = CloneArray(hostFolders),
            smbShareFolder = smbShareFolder,
        };
    }

    public void CopyFrom(LaunchConfig other)
    {
        if (other == null)
            return;
        memoryMb = other.ResolvedMemoryMb;
        extraQemuArgs = other.extraQemuArgs ?? "";
        cdroms = CloneArray(other.cdroms);
        floppies = CloneArray(other.floppies);
        hostFolders = CloneArray(other.hostFolders);
        smbShareFolder = other.smbShareFolder;
    }

    /// <summary>Append a CD if not already present.</summary>
    public bool AddCdRom(CdRomAsset asset) => AppendUnique(ref cdroms, asset);

    /// <summary>Append a host folder (vvfat share) if not already present.</summary>
    public bool AddHostFolder(UnityEngine.Object folder) => AppendUnique(ref hostFolders, folder);

    /// <summary>Append a floppy source if not already present.</summary>
    public bool AddFloppy(UnityEngine.Object source) => AppendUnique(ref floppies, source);

    /// <summary>
    /// Append <paramref name="item"/> to <paramref name="array"/> (dropping null slots)
    /// unless already present.
    /// </summary>
    static bool AppendUnique<T>(ref T[] array, T item) where T : UnityEngine.Object
    {
        if (item == null)
            return false;

        var list = new System.Collections.Generic.List<T>();
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

    /// <summary>
    /// Memory and extra-args for a QEMU launch. Strips any <c>-m</c> from extra args
    /// so RAM is only applied via <see cref="memoryMb"/>.
    /// </summary>
    public void GetRuntimeMemoryAndExtraArgs(out int memoryMbOut, out string extraArgsOut)
    {
        memoryMbOut = ResolvedMemoryMb;
        extraArgsOut = StripMemoryArgs(extraQemuArgs);
    }

    /// <summary>Strip <c>-m N</c> from freeform args (RAM comes from <see cref="memoryMb"/>).</summary>
    public static string StripMemoryArgs(string extraArgs)
    {
        if (string.IsNullOrEmpty(extraArgs))
            return "";
        return MemoryArgRegex.Replace(extraArgs, "").Trim();
    }

    static T[] CloneArray<T>(T[] source)
    {
        if (source == null || source.Length == 0)
            return Array.Empty<T>();
        return (T[])source.Clone();
    }
}
}
