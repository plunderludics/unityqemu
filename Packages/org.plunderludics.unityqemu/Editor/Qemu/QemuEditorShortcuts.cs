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
static class QemuEditorShortcuts
{
    static bool _paused;

    // Ctrl+Shift+P
    [MenuItem("UnityQemu/Toggle Pause All Emulators %#p")]
    static void TogglePauseAllEmulators()
    {
        Debug.Log("TogglePauseAllEmulators");
        _ = TogglePauseAllEmulatorsAsync();
    }

    static async Task TogglePauseAllEmulatorsAsync()
    {
        QemuEmulator[] emulators = UnityEngine.Object.FindObjectsByType<QemuEmulator>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);

        bool pause = !_paused;
        int acted = 0;
        int skipped = 0;

        foreach (QemuEmulator emu in emulators)
        {
            if (emu == null || !emu.QmpConnected)
            {
                skipped++;
                continue;
            }

            try
            {
                if (pause)
                    await emu.PauseAsync();
                else
                    await emu.ResumeAsync();
                acted++;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[QEMU] Failed to {(pause ? "pause" : "resume")} '{emu.name}': {e.Message}",
                    emu);
            }
        }

        if (acted == 0)
        {
            Debug.LogWarning(
                "[QEMU] No QemuEmulator with QMP connected to pause/resume " +
                $"(found {emulators.Length} in scene, {skipped} without QMP).");
            return;
        }

        _paused = pause;
        Debug.Log($"[QEMU] {(pause ? "Paused" : "Resumed")} {acted} emulator(s) (Ctrl+Shift+P)");
    }
}
}
