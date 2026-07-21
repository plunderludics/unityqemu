using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using TriInspector;
using UnityEngine;
using UnityQemu;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
public enum GuestMemoryScanScope { ActiveProcess, FullPhysicalRam }

/// <summary>
/// Windows XP guest process list + per-process RAM search (gdbstub physical memory).
/// Typical flow: Refresh processes → Regions on a row → New search = value → Keep changed → poke.
/// </summary>
[DeclareFoldoutGroup("Processes")]
[DeclareFoldoutGroup("RAM search")]
public class QemuProcessList : MonoBehaviour
{
    public QemuEmulator qemu;

    [Group("Processes")]
    [Tooltip("How many MiB of physical RAM to scan when auto-finding System (clamped to guest RAM from QMP)")]
    public int systemScanMaxMiB = 64;

    [Group("Processes")]
    [ReadOnly]
    [Tooltip("Filled from QMP query-memory-size-summary")]
    public int guestRamMiB;

    [Group("Processes")]
    [Tooltip("If 0, scan RAM to find the System EPROCESS automatically")]
    public long systemEprocessPhysical;

    [Group("Processes")]
    public Win32X86GuestMemory.EprocessOffsets offsets = Win32X86GuestMemory.XpSp3Defaults;

    [Group("Processes")]
    [Tooltip("Live display filter (case-insensitive substring)")]
    [OnValueChanged(nameof(OnProcessNameFilterChanged))]
    public string processNameFilter = "";

    [Group("Processes")]
    [ShowInInspector, ReadOnly]
    bool GdbReady => qemu != null && qemu.GdbConnected;

    [Group("Processes")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";

    [Group("Processes")]
    [ReadOnly]
    public string activeProcess = "(none — click Regions on a process)";

    [Group("Processes")]
    [ReadOnly, TextArea(6, 14)]
    public string regionSummary = "";

    [Group("Processes")]
    [ListDrawerSettings(
        Draggable = false,
        HideAddButton = true,
        HideRemoveButton = true,
        AlwaysExpanded = true,
        ShowElementLabels = false)]
    [SerializeField]
    List<ProcessEntry> processes = new List<ProcessEntry>();

    [Group("Processes")]
    [Tooltip("Asset to save/load the active process physical memory map")]
    public GuestProcessMemoryMap memoryMapAsset;

    [Group("RAM search")]
    [Tooltip("Default: scan only the active process memory map")]
    public GuestMemoryScanScope scanScope = GuestMemoryScanScope.ActiveProcess;

    [Group("RAM search")]
    [ShowIf(nameof(scanScope), GuestMemoryScanScope.FullPhysicalRam)]
    [Tooltip("Guest physical address when scanning full RAM")]
    public long scanStart;

    [Group("RAM search")]
    [ShowIf(nameof(scanScope), GuestMemoryScanScope.FullPhysicalRam)]
    [Tooltip("Bytes to scan when scanning full RAM")]
    public int scanLength = 1024 * 1024;

    [Group("RAM search")]
    [Tooltip("1 / 2 / 4 byte values")]
    public int valueSize = 4;

    [Group("RAM search")]
    public bool littleEndian = true;

    [Group("RAM search")]
    [Tooltip("Value for 'New = value' / 'Keep = value'")]
    public long searchValue;

    [Group("RAM search")]
    [Tooltip("Value written by Poke on the selected candidate")]
    public long pokeValue;

    [Group("RAM search")]
    [Tooltip("When ≤10 candidates, periodically re-read displayed values")]
    public bool autoRefreshCandidates = true;

    [Group("RAM search")]
    [ShowIf(nameof(autoRefreshCandidates))]
    [Tooltip("Seconds between auto-refreshes (only while ≤10 candidates)")]
    public float autoRefreshInterval = 0.5f;

    [Group("RAM search")]
    [ShowInInspector, ReadOnly]
    int candidateCount;

    [Group("RAM search")]
    [ShowInInspector, ReadOnly]
    [ShowIf(nameof(CandidatesListCollapsed))]
    string candidateListNote = "";

    [Group("RAM search")]
    [ListDrawerSettings(
        Draggable = false,
        HideAddButton = true,
        HideRemoveButton = true,
        AlwaysExpanded = true,
        ShowElementLabels = false)]
    [ShowIf(nameof(CandidatesListExpanded))]
    [SerializeField]
    List<CandidateEntry> candidates = new List<CandidateEntry>();

    const int MaxCandidates = 200_000;
    const int MaxListedCandidates = 10;

    readonly List<Win32X86GuestMemory.GuestProcess> _processes = new List<Win32X86GuestMemory.GuestProcess>();
    readonly List<Win32X86GuestMemory.PhysicalRange> _selectedRegions = new List<Win32X86GuestMemory.PhysicalRange>();
    readonly List<Candidate> _cands = new List<Candidate>();
    int _activeIndex = -1;
    float _nextCandidateRefresh;

    public IReadOnlyList<Win32X86GuestMemory.GuestProcess> Processes => _processes;
    public IReadOnlyList<Win32X86GuestMemory.PhysicalRange> SelectedPhysicalRegions => _selectedRegions;

    bool HasActiveRegions => _selectedRegions.Count > 0;
    bool HasCandidates => _cands.Count > 0;
    bool CanScan => GdbReady && (scanScope == GuestMemoryScanScope.FullPhysicalRam || HasActiveRegions);
    bool CanFilterCandidates => GdbReady && HasCandidates;
    bool CandidatesListExpanded => _cands.Count > 0 && _cands.Count <= MaxListedCandidates;
    bool CandidatesListCollapsed => _cands.Count > MaxListedCandidates;
    bool ShowManualRefreshButton => !autoRefreshCandidates;
    bool ShouldAutoRefreshCandidates =>
        autoRefreshCandidates && GdbReady && CandidatesListExpanded;

    struct Candidate
    {
        public long Address;
        public uint LastValue;
    }

    void Awake()
    {
        if (memoryMapAsset != null && memoryMapAsset.ranges.Count > 0)
            LoadMemoryMapFromAsset();
    }

    void OnEnable()
    {
        if (qemu == null)
            qemu = FindFirstObjectByType<QemuEmulator>();
        _nextCandidateRefresh = 0f;
    }

    void Update()
    {
        if (!ShouldAutoRefreshCandidates)
            return;
        if (Time.unscaledTime < _nextCandidateRefresh)
            return;

        float interval = Mathf.Max(0.05f, autoRefreshInterval);
        _nextCandidateRefresh = Time.unscaledTime + interval;
        RefreshCandidateValues(silent: true);
    }

    void OnValidate()
    {
        if (_processes.Count > 0)
            ApplyProcessNameFilter();
        autoRefreshInterval = Mathf.Max(0.05f, autoRefreshInterval);
    }

    void OnProcessNameFilterChanged()
    {
        if (_processes.Count > 0)
            ApplyProcessNameFilter();
    }

    // --- Process list ---

    [Group("Processes")]
    [Button("Refresh process list")]
    [EnableIf(nameof(GdbReady))]
    public async void RefreshProcessList()
    {
        if (!Ready(out string err)) { status = err; return; }

        int scanMiB = systemScanMaxMiB;
        try
        {
            if (qemu.QmpConnected)
            {
                long ramBytes = await qemu.GetGuestRamBytesAsync();
                guestRamMiB = (int)Math.Max(1, ramBytes / (1024 * 1024));
                if (scanMiB <= 0 || scanMiB > guestRamMiB)
                    scanMiB = guestRamMiB;
            }
        }
        catch (Exception e)
        {
            status = $"QMP RAM query failed: {e.Message}";
            if (scanMiB <= 0)
            {
                status = "QMP RAM query failed and systemScanMaxMiB is unset";
                return;
            }
        }

        long systemEproc = systemEprocessPhysical;
        var swTotal = Stopwatch.StartNew();
        long findMs = 0;
        long walkMs = 0;
        bool didFind = systemEproc == 0;

        using (qemu.BeginMemorySession())
        {
            if (systemEproc == 0)
            {
                status = "Finding System EPROCESS...";
                var swFind = Stopwatch.StartNew();
                if (!Win32X86GuestMemory.TryFindSystemEprocess(
                        ReadPhys, 0, scanMiB * 1024 * 1024, offsets,
                        out systemEproc, out err))
                {
                    status = err;
                    return;
                }
                findMs = swFind.ElapsedMilliseconds;
                systemEprocessPhysical = systemEproc;
            }

            var swWalk = Stopwatch.StartNew();
            _processes.Clear();
            _processes.AddRange(Win32X86GuestMemory.WalkProcessList(ReadPhys, systemEproc, offsets));
            walkMs = swWalk.ElapsedMilliseconds;
        }
        swTotal.Stop();

        _activeIndex = -1;
        _selectedRegions.Clear();
        regionSummary = "";
        activeProcess = "(none — click Regions on a process)";
        string ramNote = guestRamMiB > 0 ? $", guest RAM {guestRamMiB} MiB" : "";
        string findNote = didFind ? $"find {findMs} ms" : "find skipped (cached)";
        status = $"Found {_processes.Count} processes (System @ phys 0x{systemEproc:X}{ramNote}) — {findNote}, walk {walkMs} ms, total {swTotal.ElapsedMilliseconds} ms";
        Debug.Log(
            $"[QemuProcessList] RefreshProcessList: {findNote}, walk {walkMs} ms, " +
            $"total {swTotal.ElapsedMilliseconds} ms, processes={_processes.Count}, scanMiB={scanMiB}");
        ApplyProcessNameFilter();
    }

    public Win32X86GuestMemory.GuestProcess GetSelectedProcess()
    {
        if (_activeIndex < 0 || _activeIndex >= _processes.Count) return default;
        return _processes[_activeIndex];
    }

    void BuildRegionsForEntry(ProcessEntry entry)
    {
        if (!Ready(out string err)) { status = err; return; }
        if (entry == null || !entry.TryGetProcessIndex(out int index))
        {
            status = "Invalid process entry — refresh the list";
            return;
        }

        _activeIndex = index;
        var proc = _processes[_activeIndex];
        activeProcess = $"{proc.Name} (PID {proc.Pid})";
        if (proc.EprocessVirtual == 0)
        {
            status = "EPROCESS virtual address unknown — refresh the process list";
            return;
        }
        if (!TryGetKernelDirectoryTableBase(out uint kernelDtb))
        {
            status = "System (PID 4) not in process list — refresh first";
            return;
        }

        status = $"Walking VADs for {proc.Name} (PID {proc.Pid})...";

        _selectedRegions.Clear();
        using (qemu.BeginMemorySession())
        {
            _selectedRegions.AddRange(
                Win32X86GuestMemory.EnumerateUserPhysicalRanges(
                    ReadPhys, proc.DirectoryTableBase, kernelDtb, proc.EprocessVirtual, offsets));
        }

        long totalBytes = _selectedRegions.Sum(r => (long)r.Length);
        regionSummary = FormatRegions(proc, _selectedRegions, totalBytes);
        scanScope = GuestMemoryScanScope.ActiveProcess;
        status = $"{proc.Name}: {_selectedRegions.Count} physical ranges, {totalBytes / 1024} KiB mapped";
        RebuildProcessEntries();
    }

    // --- Memory map asset ---

    [Group("Processes")]
    [Button("Save active map to asset")]
    [EnableIf(nameof(HasActiveRegions))]
    public void SaveMemoryMapToAsset()
    {
        if (memoryMapAsset == null)
        {
            status = "Assign memoryMapAsset first";
            return;
        }
        if (!HasActiveRegions)
        {
            status = "No active memory map — click Regions on a process";
            return;
        }

        var proc = GetSelectedProcess();
        memoryMapAsset.SetFrom(proc, _selectedRegions, systemEprocessPhysical);
#if UNITY_EDITOR
        EditorUtility.SetDirty(memoryMapAsset);
        AssetDatabase.SaveAssets();
#endif
        status = $"Saved {proc.Name} ({_selectedRegions.Count} ranges, {memoryMapAsset.TotalBytes / 1024} KiB) → {memoryMapAsset.name}";
    }

    [Group("Processes")]
    [Button("Load map from asset")]
    public void LoadMemoryMapFromAsset()
    {
        if (memoryMapAsset == null)
        {
            status = "Assign memoryMapAsset first";
            return;
        }
        if (memoryMapAsset.ranges.Count == 0)
        {
            status = $"Memory map asset '{memoryMapAsset.name}' is empty";
            return;
        }

        _selectedRegions.Clear();
        _selectedRegions.AddRange(memoryMapAsset.ToPhysicalRanges());
        _activeIndex = -1;
        if (memoryMapAsset.systemEprocessPhysical != 0)
            systemEprocessPhysical = memoryMapAsset.systemEprocessPhysical;
        activeProcess = $"{memoryMapAsset.processName} (PID {memoryMapAsset.pid}) [saved map]";
        long totalBytes = memoryMapAsset.TotalBytes;
        regionSummary = FormatSavedMapSummary(memoryMapAsset, totalBytes);
        scanScope = GuestMemoryScanScope.ActiveProcess;
        string eprocNote = memoryMapAsset.systemEprocessPhysical != 0
            ? $", System EPROCESS 0x{memoryMapAsset.systemEprocessPhysical:X}"
            : "";
        status = $"Loaded {memoryMapAsset.processName}: {_selectedRegions.Count} ranges, {totalBytes / 1024} KiB{eprocNote}";
        RebuildProcessEntries();
    }

    // --- RAM search ---

    [Group("RAM search")]
    [Button("New search: = value")]
    [EnableIf(nameof(CanScan))]
    public void NewSearchEquals()
    {
        if (!SearchReady(out string err)) { status = err; return; }

        uint target = (uint)searchValue;
        _cands.Clear();
        int step = Mathf.Clamp(valueSize, 1, 4);

        using (qemu.BeginMemorySession())
        {
            foreach (var window in GetScanWindows())
            {
                if (!TryReadWindow(window.start, window.length, out byte[] mem, out err))
                {
                    status = err;
                    RebuildCandidateEntries();
                    return;
                }

                for (int off = 0; off + step <= mem.Length; off += step)
                {
                    uint v = ReadValue(mem, off, step);
                    if (v != target) continue;
                    _cands.Add(new Candidate { Address = window.start + off, LastValue = v });
                    if (_cands.Count >= MaxCandidates)
                    {
                        status = $"Hit MaxCandidates ({MaxCandidates}); narrow the scan range";
                        RebuildCandidateEntries();
                        return;
                    }
                }
            }
        }

        status = DescribeScanResult($"New = {searchValue}");
        RebuildCandidateEntries();
    }

    [Group("RAM search")]
    [Button("New search: unknown")]
    [EnableIf(nameof(CanScan))]
    public void NewSearchUnknown()
    {
        if (!SearchReady(out string err)) { status = err; return; }
        int step = Mathf.Clamp(valueSize, 1, 4);

        _cands.Clear();
        using (qemu.BeginMemorySession())
        {
            foreach (var window in GetScanWindows())
            {
                long alignedCount = Math.Max(0, (window.length - step) / step + 1);
                if (_cands.Count + alignedCount > MaxCandidates)
                {
                    status = $"Range too large for unknown search ({_cands.Count + alignedCount} > {MaxCandidates})";
                    return;
                }
                if (!TryReadWindow(window.start, window.length, out byte[] mem, out err))
                {
                    status = err;
                    return;
                }

                for (int off = 0; off + step <= mem.Length; off += step)
                {
                    _cands.Add(new Candidate
                    {
                        Address = window.start + off,
                        LastValue = ReadValue(mem, off, step),
                    });
                }
            }
        }

        status = DescribeScanResult("Unknown snapshot");
        RebuildCandidateEntries();
    }

    [Group("RAM search")]
    [Button("Keep unchanged")]
    [EnableIf(nameof(CanFilterCandidates))]
    public void KeepUnchanged() => FilterCandidates((cur, last) => cur == last, "unchanged");

    [Group("RAM search")]
    [Button("Keep changed")]
    [EnableIf(nameof(CanFilterCandidates))]
    public void KeepChanged() => FilterCandidates((cur, last) => cur != last, "changed");

    [Group("RAM search")]
    [Button("Keep = value")]
    [EnableIf(nameof(CanFilterCandidates))]
    public void KeepEqualsValue()
    {
        uint target = (uint)searchValue;
        FilterCandidates((cur, _) => cur == target, $"= {searchValue}");
    }

    [Group("RAM search")]
    [Button("Keep != value")]
    [EnableIf(nameof(CanFilterCandidates))]
    public void KeepNotEqualsValue()
    {
        uint target = (uint)searchValue;
        FilterCandidates((cur, _) => cur != target, $"!= {searchValue}");
    }

    [Group("RAM search")]
    [Button("Refresh displayed values")]
    [ShowIf(nameof(ShowManualRefreshButton))]
    [EnableIf(nameof(CanFilterCandidates))]
    public void RefreshCandidateValuesButton() => RefreshCandidateValues(silent: false);

    void RefreshCandidateValues(bool silent)
    {
        if (!SearchReady(out string err))
        {
            if (!silent) status = err;
            return;
        }
        int size = Mathf.Clamp(valueSize, 1, 4);
        int updated = 0;

        using (qemu.BeginMemorySession())
        {
            for (int i = 0; i < _cands.Count; i++)
            {
                var c = _cands[i];
                try
                {
                    byte[] mem = qemu.ReadBytes(c.Address, size);
                    c.LastValue = ReadValue(mem, 0, size);
                    _cands[i] = c;
                    updated++;
                }
                catch
                {
                    // address no longer readable — leave stale value
                }
            }
        }

        if (!silent)
            status = $"Refreshed {updated} candidate value(s)";

        // Prefer in-place label updates during auto-refresh to avoid inspector thrash.
        if (silent && candidates.Count == _cands.Count && _cands.Count <= MaxListedCandidates)
        {
            for (int i = 0; i < candidates.Count; i++)
                candidates[i].label = FormatCandidateLabel(_cands[i], i);
            candidateCount = _cands.Count;
            return;
        }

        RebuildCandidateEntries();
    }

    [Group("RAM search")]
    [Button("Clear candidates")]
    [EnableIf(nameof(HasCandidates))]
    public void ClearCandidates()
    {
        _cands.Clear();
        RebuildCandidateEntries();
        status = "Cleared candidates";
    }

    void FilterCandidates(Func<uint, uint, bool> pred, string label)
    {
        if (!SearchReady(out string err)) { status = err; return; }
        if (_cands.Count == 0)
        {
            status = "No candidates — run a New search first";
            return;
        }

        int size = Mathf.Clamp(valueSize, 1, 4);
        var next = new List<Candidate>(_cands.Count);

        using (qemu.BeginMemorySession())
        {
            // Candidates are physical addresses and often scattered — don't read min..max.
            // Group by 4 KiB page and read each page once.
            const int pageSize = 0x1000;
            var byPage = new Dictionary<long, List<int>>();
            for (int i = 0; i < _cands.Count; i++)
            {
                long page = _cands[i].Address & ~(long)(pageSize - 1);
                if (!byPage.TryGetValue(page, out var list))
                {
                    list = new List<int>();
                    byPage[page] = list;
                }
                list.Add(i);
            }

            foreach (var kv in byPage)
            {
                long page = kv.Key;
                byte[] mem;
                try
                {
                    mem = qemu.ReadBytes(page, pageSize);
                }
                catch (Exception e)
                {
                    status = $"Read failed @ 0x{page:X}: {e.Message}";
                    return;
                }

                foreach (int i in kv.Value)
                {
                    var c = _cands[i];
                    int off = (int)(c.Address - page);
                    if (off < 0 || off + size > mem.Length) continue;
                    uint cur = ReadValue(mem, off, size);
                    if (!pred(cur, c.LastValue)) continue;
                    c.LastValue = cur;
                    next.Add(c);
                }
            }
        }

        _cands.Clear();
        _cands.AddRange(next);
        status = $"Keep {label}: {_cands.Count} left";
        RebuildCandidateEntries();
    }

    void PokeCandidate(CandidateEntry entry)
    {
        if (!SearchReady(out string err)) { status = err; return; }
        if (entry == null || !entry.TryGetCandidateIndex(out int index))
        {
            status = "Invalid candidate — refresh search";
            return;
        }

        int size = Mathf.Clamp(valueSize, 1, 4);
        try
        {
            using (qemu.BeginMemorySession())
                qemu.WriteUnsigned(_cands[index].Address, (uint)pokeValue, size, !littleEndian);
            var c = _cands[index];
            c.LastValue = (uint)pokeValue;
            _cands[index] = c;
            status = $"Poked 0x{c.Address:X} = {pokeValue}";
            RebuildCandidateEntries();
        }
        catch (Exception e)
        {
            status = $"Poke failed: {e.Message}";
        }
    }

    void RemoveCandidate(CandidateEntry entry)
    {
        if (entry == null || !entry.TryGetCandidateIndex(out int index))
        {
            status = "Invalid candidate — refresh search";
            return;
        }

        long addr = _cands[index].Address;
        _cands.RemoveAt(index);
        status = $"Removed 0x{addr:X} ({_cands.Count} left)";
        RebuildCandidateEntries();
    }

    void RebuildCandidateEntries()
    {
        candidates.Clear();
        candidateCount = _cands.Count;
        if (_cands.Count > MaxListedCandidates)
        {
            candidateListNote =
                $"{_cands.Count} candidates remaining — list shown when ≤ {MaxListedCandidates}";
            return;
        }

        candidateListNote = "";
        for (int i = 0; i < _cands.Count; i++)
        {
            var c = _cands[i];
            candidates.Add(new CandidateEntry
            {
                owner = this,
                candidateIndex = i,
                label = FormatCandidateLabel(c, i),
            });
        }
        foreach (var entry in candidates)
            entry.owner = this;
    }

    IEnumerable<(long start, int length)> GetScanWindows()
    {
        if (scanScope == GuestMemoryScanScope.ActiveProcess && HasActiveRegions)
        {
            foreach (var r in _selectedRegions)
                yield return (r.Start, r.Length);
            yield break;
        }

        yield return (scanStart, scanLength);
    }

    string DescribeScanResult(string prefix)
    {
        if (scanScope == GuestMemoryScanScope.ActiveProcess && HasActiveRegions)
            return $"{prefix}: {_cands.Count} hits in {_selectedRegions.Count} region(s) for {activeProcess}";
        return $"{prefix}: {_cands.Count} hits in 0x{scanStart:X}+0x{scanLength:X}";
    }

    bool SearchReady(out string err)
    {
        if (!Ready(out err)) return false;
        if (scanScope == GuestMemoryScanScope.ActiveProcess && !HasActiveRegions)
        {
            err = "No process memory map — click Regions or load a saved map";
            return false;
        }
        if (scanScope == GuestMemoryScanScope.FullPhysicalRam && scanLength <= 0)
        {
            err = "scanLength must be > 0";
            return false;
        }
        if (valueSize != 1 && valueSize != 2 && valueSize != 4)
        {
            err = "valueSize must be 1, 2, or 4";
            return false;
        }
        err = null;
        return true;
    }

    bool TryReadWindow(long start, int length, out byte[] mem, out string err)
    {
        mem = null;
        try
        {
            mem = qemu.ReadBytes(start, length);
            err = null;
            return true;
        }
        catch (Exception e)
        {
            err = $"Read failed @ 0x{start:X}: {e.Message}";
            return false;
        }
    }

    uint ReadValue(byte[] mem, int offset, int size)
    {
        if (littleEndian)
        {
            uint v = mem[offset];
            if (size >= 2) v |= (uint)mem[offset + 1] << 8;
            if (size >= 4)
            {
                v |= (uint)mem[offset + 2] << 16;
                v |= (uint)mem[offset + 3] << 24;
            }
            return size == 1 ? v : size == 2 ? (ushort)v : v;
        }

        uint be = 0;
        for (int i = 0; i < size; i++)
            be = (be << 8) | mem[offset + i];
        return be;
    }

    static string FormatCandidateLabel(Candidate c, int index) =>
        $"[{index}]  0x{c.Address:X8}  = {c.LastValue}  (0x{c.LastValue:X})";

    // --- Shared helpers ---

    void ApplyProcessNameFilter()
    {
        RebuildProcessEntries();
        string filter = processNameFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
            return;
        status = processes.Count == 0
            ? $"No process matching '{filter}'"
            : $"Showing {processes.Count} of {_processes.Count} processes";
    }

    void RebuildProcessEntries()
    {
        processes.Clear();
        string filter = processNameFilter?.Trim();
        for (int i = 0; i < _processes.Count; i++)
        {
            var p = _processes[i];
            if (!string.IsNullOrEmpty(filter) &&
                p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            processes.Add(new ProcessEntry
            {
                owner = this,
                processIndex = i,
                label = FormatProcessLabel(p, i == _activeIndex),
            });
        }
        foreach (var entry in processes)
            entry.owner = this;
    }

    byte[] ReadPhys(long address, int length) => qemu.ReadBytes(address, length);

    bool TryGetKernelDirectoryTableBase(out uint dtb)
    {
        for (int i = 0; i < _processes.Count; i++)
        {
            if (_processes[i].Pid == 4)
            {
                dtb = _processes[i].DirectoryTableBase;
                return true;
            }
        }
        dtb = 0;
        return false;
    }

    bool Ready(out string err)
    {
        if (qemu == null)
        {
            err = "No QemuEmulator assigned";
            return false;
        }
        if (!qemu.GdbConnected)
        {
            err = "GDB not connected";
            return false;
        }
        err = null;
        return true;
    }

    static string FormatProcessLabel(Win32X86GuestMemory.GuestProcess p, bool active)
    {
        string marker = active ? "▶ " : "  ";
        return $"{marker}{p.Name}   PID {p.Pid}   DTB 0x{p.DirectoryTableBase:X8}";
    }

    static string FormatRegions(
        Win32X86GuestMemory.GuestProcess proc,
        List<Win32X86GuestMemory.PhysicalRange> regions,
        long totalBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{proc.Name} (PID {proc.Pid}) — {regions.Count} ranges, {totalBytes / 1024} KiB");
        AppendRegionLines(sb, regions);
        return sb.ToString();
    }

    static string FormatSavedMapSummary(GuestProcessMemoryMap map, long totalBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{map.processName} (PID {map.pid}) [saved] — {map.ranges.Count} ranges, {totalBytes / 1024} KiB");
        var temp = new List<Win32X86GuestMemory.PhysicalRange>(map.ranges.Count);
        foreach (var r in map.ranges)
            temp.Add(new Win32X86GuestMemory.PhysicalRange { Start = r.start, Length = r.length });
        AppendRegionLines(sb, temp);
        return sb.ToString();
    }

    static void AppendRegionLines(StringBuilder sb, List<Win32X86GuestMemory.PhysicalRange> regions)
    {
        int n = Math.Min(regions.Count, 32);
        for (int i = 0; i < n; i++)
        {
            var r = regions[i];
            sb.AppendLine($"  0x{r.Start:X8} + 0x{r.Length:X} ({r.Length / 1024} KiB)");
        }
        if (regions.Count > n)
            sb.AppendLine($"  ... +{regions.Count - n} more");
    }

    [Serializable]
    [DeclareHorizontalGroup("actions")]
    public class ProcessEntry
    {
        [HideLabel, DisplayAsString]
        public string label;

        [NonSerialized] public QemuProcessList owner;
        [NonSerialized] public int processIndex = -1;

        public bool TryGetProcessIndex(out int index)
        {
            index = processIndex;
            return owner != null && index >= 0 && index < owner._processes.Count;
        }

        [Group("actions")]
        [Button("Regions")]
        public void BuildRegions()
        {
            if (owner == null)
            {
                Debug.LogWarning("Process entry has no owner (refresh the list)");
                return;
            }
            owner.BuildRegionsForEntry(this);
        }
    }

    [Serializable]
    [DeclareHorizontalGroup("cactions")]
    public class CandidateEntry
    {
        [HideLabel, DisplayAsString]
        public string label;

        [NonSerialized] public QemuProcessList owner;
        [NonSerialized] public int candidateIndex = -1;

        public bool TryGetCandidateIndex(out int index)
        {
            index = candidateIndex;
            return owner != null && index >= 0 && index < owner._cands.Count;
        }

        [Group("cactions")]
        [Button("Poke")]
        public void Poke()
        {
            if (owner == null) return;
            owner.PokeCandidate(this);
        }

        [Group("cactions")]
        [GUIColor(1.0f, 0.55f, 0.55f)]
        [Button("×")]
        public void Remove()
        {
            if (owner == null) return;
            owner.RemoveCandidate(this);
        }
    }
}
}
