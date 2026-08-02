using System;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// On-demand Windows XP guest process bind + liveness probe.
/// Call <see cref="Probe"/> / <see cref="RediscoverAsync"/> from your own code; no background polling.
/// Optional <see cref="GuestWinXpBindData"/> skips System RAM scan / process walk in builds.
/// </summary>
public class WinXpGuestProcessTarget : MonoBehaviour
{
    public VirtualMachine virtualMachine;

    [Tooltip("Optional snap-scoped cache (System EPROCESS + pinned handles). Ship this in builds.")]
    public GuestWinXpBindData bindData;

    [Tooltip("Guest ImageFileName to track (e.g. Starcraft.exe). Extension optional.")]
    public string processName = "Starcraft.exe";

    [Tooltip("On enable: load a matching handle from bindData if present (does not probe).")]
    public bool bindFromAssetOnEnable = true;

    [Tooltip("MiB of guest RAM to scan when System EPROCESS is unknown (clamped via QMP when available).")]
    public int systemScanMaxMiB = 64;

    [ShowInInspector, ReadOnly]
    int pid;

    [ShowInInspector, ReadOnly]
    long eprocessPhysical;

    [ShowInInspector, ReadOnly]
    string status = "Idle";

    /// <summary>Current bound process, or default if unbound.</summary>
    public WinXpGuestMemory.GuestProcess Current { get; private set; }

    public uint Pid => Current.Pid;
    public long EprocessPhysical => Current.EprocessPhysical;
    public bool IsBound => Current.EprocessPhysical != 0 && Current.Pid != 0;

    bool _rediscoverBusy;
    long _lastSystemEprocessPhysical;

    bool GdbReady => virtualMachine != null && virtualMachine.GdbConnected;

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
        if (virtualMachine == null)
            virtualMachine = FindFirstObjectByType<VirtualMachine>();
        if (bindFromAssetOnEnable)
            TryBindFromAsset();
    }

    /// <summary>Apply a matching handle from <see cref="bindData"/> (does not probe).</summary>
    public bool TryBindFromAsset()
    {
        if (bindData == null || string.IsNullOrEmpty(processName))
            return false;
        if (!bindData.TryGetPin(processName, out var pin))
        {
            status = $"No saved process for '{processName}' in {bindData.name}";
            return false;
        }

        SetHandle(new WinXpGuestMemory.GuestProcess
        {
            Name = pin.name,
            Pid = pin.pid,
            EprocessPhysical = pin.eprocessPhysical,
            EprocessVirtual = pin.eprocessVirtual,
            DirectoryTableBase = pin.directoryTableBase,
        });
        status = $"Bound from asset: {pin.name} PID {pin.pid} @ phys 0x{pin.eprocessPhysical:X}";
        return true;
    }

    /// <summary>
    /// Cheap liveness check for the bound process.
    /// Tries <see cref="TryBindFromAsset"/> if unbound. Does not walk the process list.
    /// </summary>
    public WinXpGuestMemory.ProcessProbeResult Probe()
    {
        if (!GdbReady)
        {
            status = "GDB not connected";
            return WinXpGuestMemory.ProcessProbeResult.Gone;
        }
        if (!IsBound && !TryBindFromAsset())
        {
            status = "Not bound — call Rediscover or assign bindData";
            return WinXpGuestMemory.ProcessProbeResult.Gone;
        }

        var off = Offsets;
        WinXpGuestMemory.ProcessProbeResult result;
        using (virtualMachine.BeginMemorySession())
        {
            result = WinXpGuestMemory.ProbeProcess(
                ReadPhys, Current.EprocessPhysical, Current.Pid, processName, off);
        }

        switch (result)
        {
            case WinXpGuestMemory.ProcessProbeResult.Alive:
                status = $"Alive: {Current.Name} PID {Current.Pid}";
                break;
            case WinXpGuestMemory.ProcessProbeResult.Exited:
            case WinXpGuestMemory.ProcessProbeResult.Gone:
                status = $"{result}: {processName} (was PID {Current.Pid})";
                ClearBind();
                break;
        }
        return result;
    }

    [Button("Probe")]
    [EnableIf(nameof(GdbReady))]
    void ProbeButton() => Probe();

    [Button("Rediscover")]
    [EnableIf(nameof(GdbReady))]
    void RediscoverButton() => _ = RediscoverAsync();

    [Button("Save process to bind data")]
    [EnableIf(nameof(CanSaveProcess))]
    public void SaveProcessToBindData()
    {
        if (!CanSaveProcess)
        {
            status = "Need bindData + GDB + a bound process that Probe reports Alive";
            return;
        }

        if (Probe() != WinXpGuestMemory.ProcessProbeResult.Alive)
        {
            status = "Not saving — process is not alive";
            return;
        }

        uint imageBase = 0;
        var off = Offsets;
        using (virtualMachine.BeginMemorySession())
        {
            try
            {
                byte[] b = virtualMachine.ReadBytes(
                    Current.EprocessPhysical + off.SectionBaseAddress, 4);
                imageBase = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }
            catch
            {
                // optional
            }
        }

        if (bindData.systemEprocessPhysical == 0 && _lastSystemEprocessPhysical != 0)
            bindData.systemEprocessPhysical = _lastSystemEprocessPhysical;
        bindData.offsets = Offsets;
        bindData.UpsertPin(Current, imageBase);
#if UNITY_EDITOR
        EditorUtility.SetDirty(bindData);
        AssetDatabase.SaveAssets();
#endif
        status = $"Saved {Current.Name} PID {Current.Pid} → {bindData.name}";
    }

    bool CanSaveProcess => bindData != null && IsBound && GdbReady;

    /// <summary>
    /// Find <see cref="processName"/> via process list (System EPROCESS from bindData or RAM scan).
    /// </summary>
    public async Task<bool> RediscoverAsync()
    {
        if (_rediscoverBusy || !GdbReady || string.IsNullOrEmpty(processName))
            return false;

        _rediscoverBusy = true;
        status = $"Rediscovering '{processName}'...";
        try
        {
            var off = Offsets;
            long systemEproc = bindData != null ? bindData.systemEprocessPhysical : 0;
            if (systemEproc == 0)
                systemEproc = _lastSystemEprocessPhysical;

            int scanMiB = Mathf.Max(1, systemScanMaxMiB);
            try
            {
                if (virtualMachine.QmpConnected)
                {
                    long ramBytes = await virtualMachine.GetGuestRamBytesAsync();
                    int guestMiB = (int)Math.Max(1, ramBytes / (1024 * 1024));
                    if (scanMiB > guestMiB)
                        scanMiB = guestMiB;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WinXpGuestProcessTarget] QMP RAM query failed: {e.Message}");
            }

            WinXpGuestMemory.GuestProcess found = default;
            bool ok;
            using (virtualMachine.BeginMemorySession())
            {
                if (systemEproc == 0)
                {
                    status = "Scanning for System EPROCESS...";
                    if (!WinXpGuestMemory.TryFindSystemEprocess(
                            ReadPhys, 0, scanMiB * 1024 * 1024, off,
                            out systemEproc, out string err))
                    {
                        status = err;
                        return false;
                    }
                }

                _lastSystemEprocessPhysical = systemEproc;
                if (bindData != null && bindData.systemEprocessPhysical == 0)
                {
                    bindData.systemEprocessPhysical = systemEproc;
#if UNITY_EDITOR
                    EditorUtility.SetDirty(bindData);
#endif
                }

                ok = WinXpGuestMemory.TryFindProcessByName(
                    ReadPhys, systemEproc, processName, off, out found);
            }

            if (!ok)
            {
                ClearBind();
                status = $"Process '{processName}' not found";
                return false;
            }

            SetHandle(found);
            status = $"Found {found.Name} PID {found.Pid} @ phys 0x{found.EprocessPhysical:X}";
            return true;
        }
        catch (Exception e)
        {
            status = $"Rediscover failed: {e.Message}";
            Debug.LogException(e);
            return false;
        }
        finally
        {
            _rediscoverBusy = false;
        }
    }

    void SetHandle(WinXpGuestMemory.GuestProcess proc)
    {
        Current = proc;
        pid = (int)proc.Pid;
        eprocessPhysical = proc.EprocessPhysical;
    }

    void ClearBind()
    {
        Current = default;
        pid = 0;
        eprocessPhysical = 0;
    }

    WinXpGuestMemory.EprocessOffsets Offsets =>
        bindData != null
            ? bindData.ResolvedOffsets
            : WinXpGuestMemory.XpSp3Defaults;

    byte[] ReadPhys(long address, int length) => virtualMachine.ReadBytes(address, length);
}
}
