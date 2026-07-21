using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityQemu;

namespace UnityQemu.Editor {
/// <summary>
/// Editor-only shortcuts for QEMU guests.
/// Hotkeys use MenuItem suffix syntax: % = Ctrl/Cmd, # = Shift, &amp; = Alt.
/// </summary>
static class EditorShortcuts
{
    static bool _paused;

    // Ctrl+Shift+P
    [MenuItem("UnityQemu/Toggle Pause All Virtual Machines %#p")]
    static void TogglePauseAllVirtualMachines()
    {
        Debug.Log("TogglePauseAllVirtualMachines");
        _ = TogglePauseAllVirtualMachinesAsync();
    }

    static async Task TogglePauseAllVirtualMachinesAsync()
    {
        VirtualMachine[] machines = UnityEngine.Object.FindObjectsByType<VirtualMachine>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);

        bool pause = !_paused;
        int acted = 0;
        int skipped = 0;

        foreach (VirtualMachine machine in machines)
        {
            if (machine == null || !machine.QmpConnected)
            {
                skipped++;
                continue;
            }

            try
            {
                if (pause)
                    await machine.PauseAsync();
                else
                    await machine.ResumeAsync();
                acted++;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[QEMU] Failed to {(pause ? "pause" : "resume")} '{machine.name}': {e.Message}",
                    machine);
            }
        }

        if (acted == 0)
        {
            Debug.LogWarning(
                "[QEMU] No VirtualMachine with QMP connected to pause/resume " +
                $"(found {machines.Length} in scene, {skipped} without QMP).");
            return;
        }

        _paused = pause;
        Debug.Log($"[QEMU] {(pause ? "Paused" : "Resumed")} {acted} virtual machine(s) (Ctrl+Shift+P)");
    }
}
}
