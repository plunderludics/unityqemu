using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Saved physical memory map for a guest process (from VAD / page-table walk).
/// Assign to <see cref="WinXpRamSearch"/> to reuse without rescanning.
/// </summary>
[CreateAssetMenu(fileName = "GuestWinXpProcessMemoryMap", menuName = "UnityQemu/Guest WinXP Process Memory Map")]
public class GuestWinXpProcessMemoryMap : ScriptableObject
{
    public string processName;
    public uint pid;
    public uint directoryTableBase;
    public long eprocessVirtual;
    [Tooltip("Main EXE image base VA (SectionBaseAddress) when the map was saved")]
    public uint imageBase;
    [Tooltip("Cached System EPROCESS physical address (skips RAM scan on refresh)")]
    public long systemEprocessPhysical;

    [Serializable]
    public struct Range
    {
        public long start;
        public int length;
    }

    public List<Range> ranges = new List<Range>();

    /// <summary>
    /// BizHawk ram-watch style saved address.
    /// </summary>
    [Serializable]
    public struct Watch
    {
        [Tooltip("Guest physical address")]
        public long address;
        [Tooltip("Value size in bytes (1 / 2 / 4)")]
        public int byteCount;
        public string note;
    }

    public List<Watch> watches = new List<Watch>();

    /// <summary>
    /// Adds a watch unless one with the same address and size already exists.
    /// Returns false if it was a duplicate.
    /// </summary>
    public bool AddWatch(long address, int byteCount, string note = "")
    {
        for (int i = 0; i < watches.Count; i++)
        {
            if (watches[i].address == address && watches[i].byteCount == byteCount)
                return false;
        }
        watches.Add(new Watch { address = address, byteCount = byteCount, note = note });
        return true;
    }

    public long TotalBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < ranges.Count; i++)
                total += ranges[i].length;
            return total;
        }
    }

    public void SetFrom(
        WinXpGuestMemory.GuestProcess proc,
        IReadOnlyList<WinXpGuestMemory.PhysicalRange> physicalRanges,
        long systemEprocessPhys = 0,
        uint imageBaseVa = 0)
    {
        processName = proc.Name;
        pid = proc.Pid;
        directoryTableBase = proc.DirectoryTableBase;
        eprocessVirtual = proc.EprocessVirtual;
        imageBase = imageBaseVa;
        systemEprocessPhysical = systemEprocessPhys;
        ranges.Clear();
        for (int i = 0; i < physicalRanges.Count; i++)
        {
            var r = physicalRanges[i];
            ranges.Add(new Range { start = r.Start, length = r.Length });
        }
    }

    public List<WinXpGuestMemory.PhysicalRange> ToPhysicalRanges()
    {
        var list = new List<WinXpGuestMemory.PhysicalRange>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            list.Add(new WinXpGuestMemory.PhysicalRange { Start = r.start, Length = r.length });
        }
        return list;
    }
}
}
