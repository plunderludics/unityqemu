using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Inspector helpers for hot-plugging removable media into a running guest.
/// Functionality lives on <see cref="VirtualMachine"/>; this component only
/// exposes buttons, file pickers, and the launch-config preference.
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("cdrom/actions")]
[DeclareHorizontalGroup("floppy/actions")]
[DeclareHorizontalGroup("vvfat/actions")]
public class PeripheralsUI : MonoBehaviour
{
    public VirtualMachine virtualMachine;

    [Tooltip(
        "If the chosen media is already a project CdRomAsset / FloppyAsset, " +
        "also append/remove it on EffectiveLaunchConfig (uqsnap metadata when locked, " +
        "otherwise the VM launchConfig) so the next durable save records the insert/eject. " +
        "Paths outside the project are hotplugged by path only.")]
    public bool alsoAddToLaunchConfig = true;

#if UNITY_EDITOR
    bool QmpReady => virtualMachine != null && virtualMachine.QmpConnected;

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    static string MediaPickerDirectory => UnityQemuProjectSettings.GetPickerDirectory();

    [Group("cdrom/actions")]
    [Button("Insert CD")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertIsoButton() =>
        await PromptInsertIsoAsync(virtualMachine, alsoAddToLaunchConfig);

    [Group("cdrom/actions")]
    [Button("Eject CD")]
    [EnableIf(nameof(QmpReady))]
    public async void EjectCdromButton() =>
        await PromptEjectCdromAsync(virtualMachine, alsoAddToLaunchConfig);

    [Group("floppy/actions")]
    [Button("Insert floppy")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertFloppyButton() =>
        await PromptInsertFloppyAsync(virtualMachine, alsoAddToLaunchConfig);

    [Group("floppy/actions")]
    [Button("Eject floppy")]
    [EnableIf(nameof(QmpReady))]
    public async void EjectFloppyButton() =>
        await PromptEjectFloppyAsync(virtualMachine, alsoAddToLaunchConfig);

    [Group("vvfat/actions")]
    [Button("Attach vvfat drive")]
    [EnableIf(nameof(QmpReady))]
    public async void AttachVvfatDriveButton() =>
        await PromptAttachVvfatAsync(virtualMachine);

    [Group("vvfat/actions")]
    [Button("Detach vvfat drive")]
    [EnableIf(nameof(QmpReady))]
    public async void DetachVvfatDriveButton() =>
        await PromptDetachVvfatAsync(virtualMachine);

    /// <summary>Editor UX — works without a PeripheralsUI component.</summary>
    public static Task PromptInsertIsoAsync(VirtualMachine vm, bool alsoAddToLaunchConfig = true) =>
        RunAsync(vm, async () =>
        {
            string path = EditorUtility.OpenFilePanel("Choose ISO", MediaPickerDirectory, "iso");
            if (string.IsNullOrEmpty(path))
                return;
            await vm.InsertIsoAsync(path, alsoAddToLaunchConfig);
        });

    public static Task PromptEjectCdromAsync(VirtualMachine vm, bool alsoUpdateLaunchConfig = true) =>
        RunAsync(vm, async () =>
        {
            await vm.EjectCdromAsync(alsoUpdateLaunchConfig);
            Debug.Log("CD ejected");
        });

    public static Task PromptInsertFloppyAsync(VirtualMachine vm, bool alsoAddToLaunchConfig = true) =>
        RunAsync(vm, async () =>
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "Insert floppy",
                "Choose an image file (.img/.ima) or a project folder (vvfat ~1.44MB).",
                "Image file",
                "Cancel",
                "Folder");
            if (choice == 1)
                return;

            if (choice == 0)
            {
                string path = EditorUtility.OpenFilePanel(
                    "Choose floppy image", MediaPickerDirectory, "img,ima");
                if (string.IsNullOrEmpty(path))
                    return;
                await vm.InsertFloppyImageAsync(path, alsoAddToLaunchConfig);
            }
            else
            {
                string folder = EditorUtility.OpenFolderPanel(
                    "Choose floppy folder (vvfat ~1.44MB)", MediaPickerDirectory, "");
                if (string.IsNullOrEmpty(folder))
                    return;
                await vm.InsertFloppyFolderAsync(folder);
            }
        });

    public static Task PromptEjectFloppyAsync(VirtualMachine vm, bool alsoUpdateLaunchConfig = true) =>
        RunAsync(vm, async () =>
        {
            await vm.EjectFloppyAsync(alsoUpdateLaunchConfig);
            Debug.Log("Floppy ejected");
        });

    public static Task PromptAttachVvfatAsync(VirtualMachine vm) =>
        RunAsync(vm, async () =>
        {
            string folder = EditorUtility.OpenFolderPanel(
                "Choose folder for vvfat drive (USB)", MediaPickerDirectory, "");
            if (string.IsNullOrEmpty(folder))
                return;
            await vm.AttachVvfatDriveAsync(folder);
        });

    public static async Task PromptDetachVvfatAsync(VirtualMachine vm)
    {
        if (vm == null)
            return;

        IReadOnlyList<VirtualMachine.HotpluggedVvfatInfo> drives;
        try
        {
            drives = await vm.GetHotpluggedVvfatDrivesAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"vvfat tracking reconcile failed: {e.Message}");
            return;
        }

        if (drives.Count == 0)
        {
            Debug.LogWarning("No hotplugged vvfat drives in this session.");
            return;
        }

        VirtualMachine.HotpluggedVvfatInfo target = drives[drives.Count - 1];
        if (drives.Count > 1)
        {
            var labels = new string[drives.Count];
            for (int i = 0; i < drives.Count; i++)
                labels[i] = $"{drives[i].Id}: {Path.GetFileName(drives[i].FolderPath)}";

            int pick = EditorUtility.DisplayDialogComplex(
                "Detach vvfat drive",
                "Multiple vvfat drives are attached. Detach the most recent?\n\n" +
                string.Join("\n", labels),
                "Detach latest",
                "Cancel",
                "Detach oldest");
            if (pick == 1)
                return;
            target = pick == 0 ? drives[drives.Count - 1] : drives[0];
        }

        await RunAsync(vm, () => vm.DetachVvfatDriveAsync(target.Id));
    }

    static async Task RunAsync(VirtualMachine vm, Func<Task> action)
    {
        if (vm == null)
            return;
        try { await action(); }
        catch (Exception e) { Debug.LogException(e); }
    }
#endif
}
}
