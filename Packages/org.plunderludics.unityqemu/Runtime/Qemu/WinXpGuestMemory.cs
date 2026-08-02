using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Windows XP x86 kernel struct helpers (SP2/SP3-ish EPROCESS offsets).
/// Not valid for other Windows versions without different offsets.
/// Reads via guest <b>physical</b> memory.
/// </summary>
public static class WinXpGuestMemory
{
    public struct EprocessOffsets
    {
        public int DirectoryTableBase; // KPROCESS
        /// <summary>LARGE_INTEGER; non-zero once the process has exited (XP SP3).</summary>
        public int ExitTime;
        public int UniqueProcessId;
        public int ActiveProcessLinks;
        public int ImageFileName;
        /// <summary>Embedded MM_AVL_TABLE sentinel (XP SP3).</summary>
        public int VadRoot;
        /// <summary>Main EXE image base (PEB-equivalent; XP SP3).</summary>
        public int SectionBaseAddress;
    }

    public static EprocessOffsets XpSp3Defaults => new EprocessOffsets
    {
        DirectoryTableBase = 0x18,
        ExitTime = 0x78,
        UniqueProcessId = 0x84,
        ActiveProcessLinks = 0x88,
        ImageFileName = 0x174,
        VadRoot = 0x11C,
        SectionBaseAddress = 0x138,
    };

    /// <summary>Result of probing a previously known EPROCESS physical address.</summary>
    public enum ProcessProbeResult
    {
        Alive,
        /// <summary>PID/name still match but <c>ExitTime</c> is set.</summary>
        Exited,
        /// <summary>Pool reused or object gone (PID/name mismatch or unreadable).</summary>
        Gone,
    }

    /// <summary>MMVAD node layout when EPROCESS.VadRoot is an MM_AVL_TABLE (XP SP3).</summary>
    const int MmvadLeft = 0x00;
    const int MmvadRight = 0x04;
    const int MmvadStartingVpn = 0x10;
    const int MmvadEndingVpn = 0x14;
    /// <summary>RTL_BALANCED_LINKS.LeftChild within the VadRoot sentinel.</summary>
    const int VadRootFirstChild = 0x04;
    /// <summary>User VPN limit (VA 0x7FFF0000).</summary>
    const uint MaxUserVpn = 0x7FFFF;
    const uint PageSize = 0x1000;
    const uint LargePageSize = 0x400000;

    public struct GuestProcess
    {
        public string Name;
        public uint Pid;
        public uint DirectoryTableBase;
        public long EprocessPhysical;
        public uint EprocessVirtual;
    }

    public struct PhysicalRange
    {
        public long Start;
        public int Length;
    }

    /// <summary>Find the System EPROCESS by scanning RAM for ImageFileName == "System" and PID 4.</summary>
    public static bool TryFindSystemEprocess(
        Func<long, int, byte[]> readPhys,
        long ramStart,
        int ramLength,
        EprocessOffsets off,
        out long eprocessPhysical,
        out string error,
        int scanChunkBytes = 1024 * 1024)
    {
        eprocessPhysical = 0;
        error = null;
        var needle = Encoding.ASCII.GetBytes("System");

        for (int baseOff = 0; baseOff < ramLength; baseOff += scanChunkBytes)
        {
            int len = Math.Min(scanChunkBytes + needle.Length, ramLength - baseOff);
            if (len <= 0) break;
            byte[] block;
            try
            {
                block = readPhys(ramStart + baseOff, len);
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            for (int i = 0; i <= block.Length - needle.Length; i++)
            {
                if (!MatchAt(block, i, needle)) continue;
                long namePhys = ramStart + baseOff + i;
                long eproc = namePhys - off.ImageFileName;
                if (eproc < ramStart) continue;
                if (!TryReadProcess(readPhys, eproc, off, out GuestProcess p)) continue;
                if (p.Pid != 4) continue;
                if (!p.Name.Equals("System", StringComparison.OrdinalIgnoreCase)) continue;
                // ActiveProcessLinks.Flink is a kernel VA — reject obvious false positives.
                uint flinkVa = ReadUInt32(readPhys, eproc + off.ActiveProcessLinks);
                if (flinkVa < 0x80000000u) continue;
                eprocessPhysical = eproc;
                return true;
            }
        }

        error = "System EPROCESS not found (wrong offsets, increase scan range, or guest not booted?)";
        return false;
    }

    /// <summary>
    /// Walk the EPROCESS list. ActiveProcessLinks stores <b>kernel virtual</b> addresses;
    /// translate with System's page tables (kernel space is shared on XP).
    /// </summary>
    public static List<GuestProcess> WalkProcessList(
        Func<long, int, byte[]> readPhys,
        long startEprocessPhysical,
        EprocessOffsets off,
        int maxProcesses = 512)
    {
        var list = new List<GuestProcess>();
        if (!TryReadProcess(readPhys, startEprocessPhysical, off, out GuestProcess head))
            return list;

        uint walkDtb = head.DirectoryTableBase;
        long headPhys = startEprocessPhysical;

        // LIST_ENTRY.Blink at ActiveProcessLinks+4 points at previous link; for head, prev->Flink == head's link.
        uint blinkVa = ReadUInt32(readPhys, headPhys + off.ActiveProcessLinks + 4);
        uint headVa = blinkVa - (uint)off.ActiveProcessLinks;
        if (headVa < 0x80000000u)
        {
            Debug.LogWarning($"Process walk: could not infer System EPROCESS VA (blink=0x{blinkVa:X8})");
            head.EprocessVirtual = 0;
            list.Add(head);
            return list;
        }

        uint currentVa = headVa;
        for (int n = 0; n < maxProcesses; n++)
        {
            if (!TryTranslateVirtualToPhysical(readPhys, walkDtb, currentVa, out long currentPhys))
            {
                Debug.LogWarning($"Process walk: translate EPROCESS VA 0x{currentVa:X8} failed");
                break;
            }
            if (!TryReadProcess(readPhys, currentPhys, off, out GuestProcess p))
                break;

            p.EprocessVirtual = currentVa;
            p.EprocessPhysical = currentPhys;
            list.Add(p);

            uint flinkVa = ReadUInt32(readPhys, currentPhys + off.ActiveProcessLinks);
            if (flinkVa == 0) break;
            uint nextVa = flinkVa - (uint)off.ActiveProcessLinks;
            if (nextVa == headVa) break;
            currentVa = nextVa;
        }

        return list;
    }

    public static bool TryReadProcess(
        Func<long, int, byte[]> readPhys,
        long eprocessPhysical,
        EprocessOffsets off,
        out GuestProcess proc)
    {
        proc = default;
        try
        {
            byte[] nameBytes = readPhys(eprocessPhysical + off.ImageFileName, 16);
            string name = ReadFixedString(nameBytes, 15);
            uint pid = ReadUInt32(readPhys, eprocessPhysical + off.UniqueProcessId);
            uint dtb = ReadUInt32(readPhys, eprocessPhysical + off.DirectoryTableBase);
            proc = new GuestProcess
            {
                Name = name,
                Pid = pid,
                DirectoryTableBase = dtb,
                EprocessPhysical = eprocessPhysical,
            };
            return pid != 0 || name.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cheap liveness check for a cached EPROCESS (a few physical reads).
    /// Does not walk the process list.
    /// </summary>
    public static ProcessProbeResult ProbeProcess(
        Func<long, int, byte[]> readPhys,
        long eprocessPhysical,
        uint expectedPid,
        string expectedName,
        EprocessOffsets off)
    {
        if (eprocessPhysical == 0 || expectedPid == 0)
            return ProcessProbeResult.Gone;

        try
        {
            if (!TryReadProcess(readPhys, eprocessPhysical, off, out GuestProcess proc))
                return ProcessProbeResult.Gone;
            if (proc.Pid != expectedPid || !ProcessNamesMatch(expectedName, proc.Name))
                return ProcessProbeResult.Gone;

            int exitOff = off.ExitTime != 0 ? off.ExitTime : XpSp3Defaults.ExitTime;
            ulong exitTime = ReadUInt64(readPhys, eprocessPhysical + exitOff);
            return exitTime != 0 ? ProcessProbeResult.Exited : ProcessProbeResult.Alive;
        }
        catch
        {
            return ProcessProbeResult.Gone;
        }
    }

    /// <summary>Walk from System and return the first process whose name matches.</summary>
    public static bool TryFindProcessByName(
        Func<long, int, byte[]> readPhys,
        long systemEprocessPhysical,
        string processName,
        EprocessOffsets off,
        out GuestProcess proc,
        int maxProcesses = 512)
    {
        proc = default;
        if (systemEprocessPhysical == 0 || string.IsNullOrEmpty(processName))
            return false;

        var list = WalkProcessList(readPhys, systemEprocessPhysical, off, maxProcesses);
        for (int i = 0; i < list.Count; i++)
        {
            if (!ProcessNamesMatch(processName, list[i].Name))
                continue;
            proc = list[i];
            return true;
        }
        return false;
    }

    /// <summary>Case-insensitive match; optional <c>.exe</c>/<c>.dll</c> suffix on either side.</summary>
    public static bool ProcessNamesMatch(string requested, string actual)
    {
        if (string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(actual))
            return false;
        if (requested.Equals(actual, StringComparison.OrdinalIgnoreCase))
            return true;

        string req = StripExeExtension(requested);
        string act = StripExeExtension(actual);
        return req.Equals(act, StringComparison.OrdinalIgnoreCase);
    }

    static string StripExeExtension(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return name.Substring(0, name.Length - 4);
        return name;
    }

    /// <summary>Fill any zero offset fields from <see cref="XpSp3Defaults"/>.</summary>
    public static EprocessOffsets WithDefaults(EprocessOffsets off)
    {
        var d = XpSp3Defaults;
        if (off.DirectoryTableBase == 0) off.DirectoryTableBase = d.DirectoryTableBase;
        if (off.ExitTime == 0) off.ExitTime = d.ExitTime;
        if (off.UniqueProcessId == 0) off.UniqueProcessId = d.UniqueProcessId;
        if (off.ActiveProcessLinks == 0) off.ActiveProcessLinks = d.ActiveProcessLinks;
        if (off.ImageFileName == 0) off.ImageFileName = d.ImageFileName;
        if (off.VadRoot == 0) off.VadRoot = d.VadRoot;
        if (off.SectionBaseAddress == 0) off.SectionBaseAddress = d.SectionBaseAddress;
        return off;
    }

    /// <summary>
    /// Enumerate user-mode mapped pages via the process VAD tree, returning merged contiguous
    /// <b>physical</b> ranges.
    /// </summary>
    public static List<PhysicalRange> EnumerateUserPhysicalRanges(
        Func<long, int, byte[]> readPhys,
        uint directoryTableBase,
        uint kernelDirectoryTableBase,
        uint eprocessVirtual,
        EprocessOffsets off)
    {
        var ranges = new List<PhysicalRange>();
        if (eprocessVirtual < 0x80000000u)
        {
            Debug.LogWarning("EnumerateUserPhysicalRanges: EPROCESS virtual address unknown");
            return ranges;
        }

        var vads = CollectUserVads(readPhys, kernelDirectoryTableBase, eprocessVirtual, off);
        PhysicalRange? open = null;
        if (vads.Count > 0)
        {
            foreach (var vad in vads)
            {
                WalkVirtualRangePhysical(
                    readPhys, directoryTableBase, vad.StartVpn * PageSize, (vad.EndVpn + 1) * PageSize,
                    ref open, ranges);
            }
        }
        else
        {
            Debug.LogWarning("VAD walk found no regions; falling back to page-table walk");
            WalkVirtualRangePhysical(readPhys, directoryTableBase, 0x10000, 0x7FFF0000, ref open, ranges);
        }

        if (open != null)
            ranges.Add(open.Value);
        return ranges;
    }

    struct VadRange
    {
        public uint StartVpn;
        public uint EndVpn;
    }

    static List<VadRange> CollectUserVads(
        Func<long, int, byte[]> readPhys,
        uint kernelDirectoryTableBase,
        uint eprocessVirtual,
        EprocessOffsets off)
    {
        var vads = new List<VadRange>();
        uint firstVadVa = ReadKernelUInt32(
            readPhys, kernelDirectoryTableBase, eprocessVirtual + (uint)off.VadRoot + VadRootFirstChild);
        if (firstVadVa == 0 || firstVadVa < 0x80000000u)
            return vads;

        var stack = new Stack<uint>();
        stack.Push(firstVadVa);
        while (stack.Count > 0)
        {
            uint nodeVa = stack.Pop();
            if (nodeVa == 0 || nodeVa < 0x80000000u)
                continue;
            if (!TryTranslateVirtualToPhysical(readPhys, kernelDirectoryTableBase, nodeVa, out long nodePhys))
                continue;

            uint left = ReadUInt32(readPhys, nodePhys + MmvadLeft);
            uint right = ReadUInt32(readPhys, nodePhys + MmvadRight);
            uint startVpn = ReadUInt32(readPhys, nodePhys + MmvadStartingVpn);
            uint endVpn = ReadUInt32(readPhys, nodePhys + MmvadEndingVpn);

            if (right != 0)
                stack.Push(right);
            if (IsUserVad(startVpn, endVpn))
                vads.Add(new VadRange { StartVpn = startVpn, EndVpn = endVpn });
            if (left != 0)
                stack.Push(left);
        }

        vads.Sort((a, b) => a.StartVpn.CompareTo(b.StartVpn));
        return vads;
    }

    static bool IsUserVad(uint startVpn, uint endVpn)
    {
        if (startVpn > endVpn)
            return false;
        if (endVpn > MaxUserVpn)
            return false;
        return startVpn <= MaxUserVpn;
    }

    /// <summary>
    /// Walk a VA range via page tables: skip 4 MiB holes, bulk-read PTE pages (4 KiB each).
    /// </summary>
    static void WalkVirtualRangePhysical(
        Func<long, int, byte[]> readPhys,
        uint directoryTableBase,
        uint vaStart,
        uint vaLimit,
        ref PhysicalRange? open,
        List<PhysicalRange> ranges)
    {
        if (vaLimit > 0x80000000u)
            vaLimit = 0x80000000u;
        if (vaStart >= vaLimit)
            return;

        uint cr3 = directoryTableBase & 0xFFFFF000u;
        uint va = vaStart;

        while (va < vaLimit)
        {
            uint pdeIndex = (va >> 22) & 0x3FF;
            uint pde = ReadUInt32(readPhys, cr3 + pdeIndex * 4);

            if ((pde & 1) == 0)
            {
                CloseOpenRange(ref open, ranges);
                va = NextLargePageBoundary(va);
                continue;
            }

            uint pdeBaseVa = va & 0xFFC00000u;
            uint blockEndVa = pdeBaseVa + LargePageSize;
            if (blockEndVa > vaLimit)
                blockEndVa = vaLimit;

            if ((pde & 0x80) != 0)
            {
                for (uint pageVa = va; pageVa < blockEndVa; pageVa += PageSize)
                {
                    long pa = (pde & 0xFFC00000u) | (pageVa & 0x003FFFFFu);
                    AddMappedPage(ref open, ranges, pa);
                }
                va = blockEndVa;
                continue;
            }

            byte[] ptePage;
            try
            {
                ptePage = readPhys(pde & 0xFFFFF000u, (int)PageSize);
            }
            catch
            {
                CloseOpenRange(ref open, ranges);
                va = blockEndVa;
                continue;
            }

            uint pteStart = (va >> 12) & 0x3FF;
            uint pteEnd = ((blockEndVa - 1) >> 12) & 0x3FF;
            for (uint pteIdx = pteStart; pteIdx <= pteEnd; pteIdx++)
            {
                uint pte = ReadUInt32FromBytes(ptePage, (int)pteIdx * 4);
                if ((pte & 1) == 0)
                {
                    CloseOpenRange(ref open, ranges);
                    continue;
                }
                AddMappedPage(ref open, ranges, pte & 0xFFFFF000u);
            }

            va = blockEndVa;
        }
    }

    static uint NextLargePageBoundary(uint va) => ((va >> 22) + 1) << 22;

    static void CloseOpenRange(ref PhysicalRange? open, List<PhysicalRange> ranges)
    {
        if (open == null) return;
        ranges.Add(open.Value);
        open = null;
    }

    static void AddMappedPage(ref PhysicalRange? open, List<PhysicalRange> ranges, long pa)
    {
        if (open != null && open.Value.Start + open.Value.Length == pa)
        {
            var r = open.Value;
            r.Length += (int)PageSize;
            open = r;
            return;
        }

        if (open != null)
            ranges.Add(open.Value);
        open = new PhysicalRange { Start = pa, Length = (int)PageSize };
    }

    static uint ReadUInt32FromBytes(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    static uint ReadKernelUInt32(
        Func<long, int, byte[]> readPhys,
        uint kernelDirectoryTableBase,
        uint kernelVirtualAddress)
    {
        if (!TryTranslateVirtualToPhysical(readPhys, kernelDirectoryTableBase, kernelVirtualAddress, out long pa))
            return 0;
        return ReadUInt32(readPhys, pa);
    }

    public static bool TryTranslateVirtualToPhysical(
        Func<long, int, byte[]> readPhys,
        uint directoryTableBase,
        uint virtualAddress,
        out long physicalAddress)
    {
        physicalAddress = 0;
        uint cr3 = directoryTableBase & 0xFFFFF000u;
        uint pdeIndex = (virtualAddress >> 22) & 0x3FF;
        uint pteIndex = (virtualAddress >> 12) & 0x3FF;
        uint pageOffset = virtualAddress & 0xFFF;

        uint pde = ReadUInt32(readPhys, cr3 + pdeIndex * 4);
        if ((pde & 1) == 0) return false;

        if ((pde & 0x80) != 0) // 4 MiB page
        {
            physicalAddress = (pde & 0xFFC00000u) | (virtualAddress & 0x003FFFFF);
            return true;
        }

        uint pteAddr = (pde & 0xFFFFF000u) + pteIndex * 4;
        uint pte = ReadUInt32(readPhys, pteAddr);
        if ((pte & 1) == 0) return false;

        physicalAddress = (pte & 0xFFFFF000u) | pageOffset;
        return true;
    }

    static uint ReadUInt32(Func<long, int, byte[]> readPhys, long address)
    {
        byte[] b = readPhys(address, 4);
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    static ulong ReadUInt64(Func<long, int, byte[]> readPhys, long address)
    {
        byte[] b = readPhys(address, 8);
        uint lo = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        uint hi = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
        return lo | ((ulong)hi << 32);
    }

    static string ReadFixedString(byte[] bytes, int maxLen)
    {
        int len = 0;
        while (len < maxLen && len < bytes.Length && bytes[len] != 0)
            len++;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    static bool MatchAt(byte[] haystack, int offset, byte[] needle)
    {
        for (int i = 0; i < needle.Length; i++)
        {
            if (haystack[offset + i] != needle[i]) return false;
        }
        return haystack[offset + needle.Length] == 0;
    }
}
}
