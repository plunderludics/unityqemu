using System;
using System.Collections.Generic;
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

    // Ctrl+Shift+Alt+P
    [MenuItem("UnityQemu/Toggle Pause All Virtual Machines %#&p")]
    static void TogglePauseAllVirtualMachines()
    {
        _ = TogglePauseAllVirtualMachinesAsync();
    }

    static async Task TogglePauseAllVirtualMachinesAsync()
    {
        VirtualMachine[] machines = FindVirtualMachines();

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
        Debug.Log(
            $"[QEMU] {(pause ? "Paused" : "Resumed")} {acted} virtual machine(s) (Ctrl+Shift+Alt+P)");
    }

    // Ctrl+Shift+Alt+R
    [MenuItem("UnityQemu/Reload Current State %#&r")]
    static void ReloadCurrentState()
    {
        _ = ReloadCurrentStateAsync();
    }

    static async Task ReloadCurrentStateAsync()
    {
        VirtualMachine[] machines = FindVirtualMachines();
        if (machines.Length == 0)
        {
            Debug.LogWarning("[QEMU] Reload Current State: no VirtualMachine in the scene.");
            return;
        }

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
                if (ResolveCurrentSnapshot(machine) != null)
                {
                    await machine.RunHumanMonitorCommandOrThrowAsync(
                        $"loadvm {DiskOverlay.DurableSaveVmTag}");
                    Debug.Log(
                        $"[QEMU] Reloaded session state on '{machine.name}' (Ctrl+Shift+Alt+R)",
                        machine);
                }
                else
                {
                    await machine.RebootAsync();
                    Debug.Log(
                        $"[QEMU] No snapshot — rebooted '{machine.name}' (Ctrl+Shift+Alt+R)",
                        machine);
                }
                acted++;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[QEMU] Reload Current State failed on '{machine.name}': {e.Message}",
                    machine);
            }
        }

        if (acted == 0)
        {
            Debug.LogWarning(
                $"[QEMU] Reload Current State: no QMP-connected VirtualMachine " +
                $"(found {machines.Length} in scene, {skipped} without QMP).");
        }
    }

    // Ctrl+Shift+Alt+A — CD / floppy / vvfat hotplug
    [MenuItem("UnityQemu/Peripherals… %#&a")]
    static void OpenPeripherals()
    {
        var targets = CollectRunningPeripheralTargets();
        if (targets.Count == 0)
        {
            Debug.LogWarning(
                "[QEMU] Peripherals: no running VirtualMachine with QMP and PeripheralsUI " +
                "(stopped guests are ignored).");
            return;
        }

        PeripheralsDialog.Result dialog = PeripheralsDialog.Prompt(targets.ToArray());
        if (dialog.Choice == PeripheralsDialog.Choice.Cancelled || dialog.Target == null)
            return;

        PeripheralsUI ui = dialog.Target;
        ui.alsoAddToLaunchConfig = dialog.AlsoAddToLaunchConfig;
        EditorUtility.SetDirty(ui);

        switch (dialog.Choice)
        {
            case PeripheralsDialog.Choice.InsertCd:
                ui.InsertIsoButton();
                break;
            case PeripheralsDialog.Choice.EjectCd:
                ui.EjectCdromButton();
                break;
            case PeripheralsDialog.Choice.InsertFloppy:
                ui.InsertFloppyButton();
                break;
            case PeripheralsDialog.Choice.EjectFloppy:
                ui.EjectFloppyButton();
                break;
            case PeripheralsDialog.Choice.AttachVvfat:
                ui.AttachVvfatDriveButton();
                break;
            case PeripheralsDialog.Choice.DetachVvfat:
                ui.DetachVvfatDriveButton();
                break;
        }
    }

    // Ctrl+Shift+Alt+C — pick Save child / Save sibling / Overwrite
    [MenuItem("UnityQemu/Save Snapshot… %#&c")]
    static void SaveSnapshot()
    {
        _ = SaveSnapshotAsync();
    }

    static async Task SaveSnapshotAsync()
    {
        var saveTargets = CollectRunningSaveTargets();
        if (saveTargets.Count == 0)
        {
            Debug.LogWarning(
                "[QEMU] Save Snapshot: no running VirtualMachine with QMP and SnapshotUI " +
                "(stopped guests are ignored).");
            return;
        }

        var wePaused = new List<VirtualMachine>();
        try
        {
            // Freeze every saveable guest at shortcut time, not only the one picked later.
            foreach (SnapshotUI targetUi in saveTargets)
            {
                VirtualMachine machine = targetUi.virtualMachine;
                if (machine == null || !machine.QmpConnected)
                    continue;

                if (!await machine.IsPausedAsync())
                {
                    await machine.PauseAsync();
                    wePaused.Add(machine);
                }
            }

            SnapshotSaveDialog.Result dialog = SnapshotSaveDialog.Prompt(saveTargets.ToArray());
            if (dialog.Choice == SnapshotSaveDialog.Choice.Cancelled || dialog.Target == null)
                return;

            SnapshotUI ui = dialog.Target;
            ui.includeMachineState = dialog.IncludeMachineState;
            ui.captureScreenshot = dialog.CaptureScreenshot;
            ui.compressMachineState = dialog.CompressMachineState;
            EditorUtility.SetDirty(ui);

            bool completed = false;
            switch (dialog.Choice)
            {
                case SnapshotSaveDialog.Choice.SaveChild:
                    completed = await ui.SaveChildSnapshotAsync();
                    break;
                case SnapshotSaveDialog.Choice.SaveSibling:
                    completed = await ui.SaveSiblingSnapshotAsync();
                    break;
                case SnapshotSaveDialog.Choice.Overwrite:
                    completed = await ui.OverwriteCurrentSnapshotAsync();
                    break;
            }

            if (!completed)
                Debug.Log("[QEMU] Save Snapshot cancelled or failed — resuming paused guests.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QEMU] Save Snapshot failed: {e.Message}");
        }
        finally
        {
            await ResumePausedByShortcutAsync(wePaused);
        }
    }

    /// <summary>
    /// Running guests that have a SnapshotUI (QMP connected). Stopped VMs are skipped.
    /// </summary>
    static List<SnapshotUI> CollectRunningSaveTargets()
    {
        var targets = new List<SnapshotUI>();
        foreach (VirtualMachine machine in FindVirtualMachines())
        {
            if (machine == null || !machine.QmpConnected)
                continue;

            SnapshotUI ui = FindSnapshotUI(machine);
            if (ui == null)
            {
                Debug.LogWarning(
                    $"[QEMU] Save Snapshot: '{machine.name}' is running but has no SnapshotUI — skipped.",
                    machine);
                continue;
            }

            targets.Add(ui);
        }

        return targets;
    }

    /// <summary>
    /// Running guests that have a PeripheralsUI (QMP connected). Stopped VMs are skipped.
    /// </summary>
    static List<PeripheralsUI> CollectRunningPeripheralTargets()
    {
        var targets = new List<PeripheralsUI>();
        foreach (VirtualMachine machine in FindVirtualMachines())
        {
            if (machine == null || !machine.QmpConnected)
                continue;

            PeripheralsUI ui = FindPeripheralsUI(machine);
            if (ui == null)
            {
                Debug.LogWarning(
                    $"[QEMU] Peripherals: '{machine.name}' is running but has no PeripheralsUI — skipped.",
                    machine);
                continue;
            }

            targets.Add(ui);
        }

        return targets;
    }

    static SnapshotUI FindSnapshotUI(VirtualMachine machine) =>
        FindBoundUi(
            machine,
            (SnapshotUI ui) => ui.virtualMachine,
            (ui, vm) => ui.virtualMachine = vm);

    static PeripheralsUI FindPeripheralsUI(VirtualMachine machine) =>
        FindBoundUi(
            machine,
            (PeripheralsUI ui) => ui.virtualMachine,
            (ui, vm) => ui.virtualMachine = vm);

    static T FindBoundUi<T>(
        VirtualMachine machine,
        Func<T, VirtualMachine> getVm,
        Action<T, VirtualMachine> setVm)
        where T : Component
    {
        if (machine == null)
            return null;

        T onSameObject = machine.GetComponent<T>();
        if (onSameObject != null)
        {
            if (getVm(onSameObject) == null)
                setVm(onSameObject, machine);
            return onSameObject;
        }

        foreach (T candidate in UnityEngine.Object.FindObjectsByType<T>(
                     FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None))
        {
            if (candidate == null)
                continue;
            if (getVm(candidate) == null)
                setVm(candidate, candidate.GetComponent<VirtualMachine>());
            if (ReferenceEquals(getVm(candidate), machine))
                return candidate;
        }

        return null;
    }

    static async Task ResumePausedByShortcutAsync(List<VirtualMachine> wePaused)
    {
        if (wePaused == null || wePaused.Count == 0)
            return;

        foreach (VirtualMachine machine in wePaused)
        {
            if (machine == null)
                continue;

            try
            {
                if (await machine.IsPausedAsync())
                    await machine.ResumeAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[QEMU] Save Snapshot: failed to resume '{machine.name}': {e.Message}",
                    machine);
            }
        }
    }

    /// <summary>
    /// Session uqsnap when set, else boot-config snapshot. Null when disk-only.
    /// </summary>
    static UqsnapAsset ResolveCurrentSnapshot(VirtualMachine machine)
    {
        if (machine.sessionCurrent is UqsnapAsset sessionSnap)
            return sessionSnap;
        return machine.HasSnapshot ? machine.snapshot : null;
    }

    static VirtualMachine[] FindVirtualMachines() =>
        UnityEngine.Object.FindObjectsByType<VirtualMachine>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
}
}
