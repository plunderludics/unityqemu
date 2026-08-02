using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Snap-scoped Windows XP bind cache: System EPROCESS phys + pinned process handles.
/// Valid for the matching machine RAM image (typically one <see cref="UqsnapAsset"/>);
/// not a property of the disk alone. Keep XP-specific fields off <see cref="UqsnapAsset"/>.
/// </summary>
[CreateAssetMenu(fileName = "GuestWinXpBindData", menuName = "UnityQemu/Guest WinXP Bind Data")]
public class GuestWinXpBindData : ScriptableObject
{
    [Tooltip("Cached System (PID 4) EPROCESS physical address. 0 = unknown / must scan.")]
    public long systemEprocessPhysical;

    public WinXpGuestMemory.EprocessOffsets offsets = WinXpGuestMemory.XpSp3Defaults;

    [Serializable]
    public struct PinnedProcess
    {
        public string name;
        public uint pid;
        public long eprocessPhysical;
        public uint eprocessVirtual;
        public uint directoryTableBase;
        public uint imageBase;
    }

    public List<PinnedProcess> pinnedProcesses = new List<PinnedProcess>();

    public WinXpGuestMemory.EprocessOffsets ResolvedOffsets =>
        WinXpGuestMemory.WithDefaults(offsets);

    public bool TryGetPin(string processName, out PinnedProcess pin)
    {
        pin = default;
        if (string.IsNullOrEmpty(processName))
            return false;
        for (int i = 0; i < pinnedProcesses.Count; i++)
        {
            if (!WinXpGuestMemory.ProcessNamesMatch(processName, pinnedProcesses[i].name))
                continue;
            pin = pinnedProcesses[i];
            return pin.eprocessPhysical != 0 && pin.pid != 0;
        }
        return false;
    }

    public void UpsertPin(WinXpGuestMemory.GuestProcess proc, uint imageBase = 0)
    {
        if (proc.EprocessPhysical == 0 || proc.Pid == 0)
            return;

        var pin = new PinnedProcess
        {
            name = proc.Name,
            pid = proc.Pid,
            eprocessPhysical = proc.EprocessPhysical,
            eprocessVirtual = proc.EprocessVirtual,
            directoryTableBase = proc.DirectoryTableBase,
            imageBase = imageBase,
        };

        for (int i = 0; i < pinnedProcesses.Count; i++)
        {
            if (!WinXpGuestMemory.ProcessNamesMatch(proc.Name, pinnedProcesses[i].name))
                continue;
            pinnedProcesses[i] = pin;
            return;
        }
        pinnedProcesses.Add(pin);
    }
}
}
