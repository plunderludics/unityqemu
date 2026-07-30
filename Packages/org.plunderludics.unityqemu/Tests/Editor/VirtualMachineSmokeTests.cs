using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityQemu.Tests {
/// <summary>
/// Sparse end-to-end checks against a throwaway empty qcow2 (no OS image required):
/// cold boot → QMP, and capture/restore machine state round-trip.
/// </summary>
public class VirtualMachineSmokeTests
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);

    [Test]
    public async Task ColdBoot_EmptyDisk_ConnectsQmp()
    {
        string qcow2 = null;
        DiskAsset disk = null;
        GameObject go = null;
        VirtualMachine vm = null;
        try
        {
            (qcow2, disk, go, vm) = CreateSmokeVm("ColdBoot");
            await WaitFor(vm.StartGuestProcessAsync(), StartTimeout, "cold boot");
            Assert.IsTrue(vm.QmpConnected, "QMP should connect after cold boot");
            Assert.IsNull(vm.LastStateRestoreError);
        }
        finally
        {
            await TearDown(vm, go, disk, qcow2);
        }
    }

    [Test]
    public async Task CaptureAndRestore_MachineState_RoundTrips()
    {
        string basePath = null;
        string tipPath = null;
        string statePath = null;
        DiskAsset baseDisk = null;
        DiskAsset tipDisk = null;
        UqsnapAsset snap = null;
        GameObject go = null;
        VirtualMachine vm = null;
        try
        {
            (basePath, baseDisk, go, vm) = CreateSmokeVm("RoundTrip");
            await WaitFor(vm.StartGuestProcessAsync(), StartTimeout, "cold boot");
            Assert.IsTrue(vm.QmpConnected);

            string dir = Path.GetDirectoryName(basePath)!;
            tipPath = Path.Combine(dir, $"tip-{Guid.NewGuid():N}.qcow2");
            statePath = Path.Combine(dir, $"state-{Guid.NewGuid():N}.uqsnap");

            VirtualMachine.CaptureStateResult capture = await WaitFor(
                vm.CaptureStateAsync(statePath, gzip: true, captureMachineState: true),
                CaptureTimeout,
                "CaptureStateAsync");

            Assert.IsTrue(capture.CapturedMachineState, "migrate should capture machine state");
            Assert.IsTrue(File.Exists(statePath) && new FileInfo(statePath).Length > 0);
            Assert.IsFalse(string.IsNullOrEmpty(capture.FrozenLayerPath));

            DiskOverlay.ConvertThin(capture.FrozenLayerPath, basePath, tipPath);
            Assert.IsTrue(File.Exists(tipPath));

            tipDisk = TestDiskUtil.DiskAssetForPath(tipPath);
            tipDisk.backingDisk = baseDisk;

            snap = ScriptableObject.CreateInstance<UqsnapAsset>();
            snap.name = "RoundTripSnap";
            snap.disk = tipDisk;
            snap.projectRelativeUqsnapPath = statePath;
            snap.metadata = UqsnapMetadata.CreateEmpty();
            snap.metadata.launchConfig = vm.launchConfig.Clone();

            await vm.StopGuestProcessAsync();
            vm.PrepareBoot(snap, loadVmState: true);
            await WaitFor(vm.StartGuestProcessAsync(), StartTimeout, "restore boot");

            Assert.IsTrue(vm.QmpConnected, "QMP should connect after state restore");
            Assert.IsNull(
                vm.LastStateRestoreError,
                $"State restore failed: {vm.LastStateRestoreError}");
        }
        finally
        {
            await TearDown(vm, go, tipDisk, tipPath);
            TestDiskUtil.DestroyImmediate(snap);
            TestDiskUtil.DestroyImmediate(baseDisk);
            TestDiskUtil.SafeDelete(statePath);
            TestDiskUtil.SafeDelete(basePath);
        }
    }

    static (string qcow2, DiskAsset disk, GameObject go, VirtualMachine vm) CreateSmokeVm(
        string label)
    {
        string qcow2 = TestDiskUtil.CreateEmptyQcow2($"{label}-{Guid.NewGuid():N}.qcow2");
        DiskAsset disk = TestDiskUtil.DiskAssetForPath(qcow2);

        var go = new GameObject($"UnityQemu{label}VM");
        go.hideFlags = HideFlags.HideAndDontSave;
        VirtualMachine vm = go.AddComponent<VirtualMachine>();
        vm.runVmInEditMode = false;
        vm.autoRestart = false;
        vm.playAudioInUnity = false;
        vm.showGui = false;
        vm.enableGdb = false;
        vm.diskAsset = disk;
        vm.launchConfig = LaunchConfig.CreateDefault();
        vm.launchConfig.memoryMb = 32;
        vm.launchConfig.extraQemuArgs = "-cpu 486 -vga cirrus -usb -device usb-tablet";
        vm.launchConfig.usbEhci = false;
        return (qcow2, disk, go, vm);
    }

    static async Task<T> WaitFor<T>(Task<T> task, TimeSpan timeout, string label)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout));
        if (finished != task)
            throw new TimeoutException($"{label} exceeded {timeout.TotalSeconds}s");
        return await task;
    }

    static async Task WaitFor(Task task, TimeSpan timeout, string label)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout));
        if (finished != task)
            throw new TimeoutException($"{label} exceeded {timeout.TotalSeconds}s");
        await task;
    }

    static async Task TearDown(
        VirtualMachine vm, GameObject go, DiskAsset disk, string qcow2)
    {
        if (vm != null)
        {
            try { await vm.StopGuestProcessAsync(); }
            catch (Exception e) { Debug.LogWarning($"Smoke stop: {e.Message}"); }
        }
        if (go != null)
            Object.DestroyImmediate(go);
        TestDiskUtil.DestroyImmediate(disk);
        TestDiskUtil.SafeDelete(qcow2);
    }
}
}
