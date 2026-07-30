using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityQemu.Tests {
/// <summary>
/// Sparse end-to-end checks against
/// <c>Tests/Fixtures/SmokeTest.unity</c> (VirtualMachine + empty.qcow2).
/// Round-trip still writes tip/state under Library/UnityQemu/Tests.
/// </summary>
public class VirtualMachineSmokeTests
{
    public const string ScenePath =
        "Packages/org.plunderludics.unityqemu/Tests/Fixtures/SmokeTest.unity";
    public const string EmptyDiskPath =
        "Packages/org.plunderludics.unityqemu/Tests/Fixtures/empty.qcow2";

    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(120);

    [Test]
    public async Task ColdBoot_EmptyDisk_ConnectsQmp()
    {
        using (var fixture = await SmokeSceneFixture.OpenAsync())
        {
            VirtualMachine vm = fixture.Vm;
            await WaitFor(vm.StartGuestProcessAsync(), StartTimeout, "cold boot");
            Assert.IsTrue(vm.QmpConnected, "QMP should connect after cold boot");
            Assert.IsNull(vm.LastStateRestoreError);
        }
    }

    [Test]
    public async Task CaptureAndRestore_MachineState_RoundTrips()
    {
        string tipPath = null;
        string statePath = null;
        DiskAsset tipDisk = null;
        UqsnapAsset snap = null;

        using (var fixture = await SmokeSceneFixture.OpenAsync())
        {
            VirtualMachine vm = fixture.Vm;
            DiskAsset baseDisk = vm.diskAsset;
            Assert.IsNotNull(baseDisk);
            string basePath = baseDisk.GetQcow2FilesystemPath();
            Assert.IsTrue(File.Exists(basePath), basePath);

            await WaitFor(vm.StartGuestProcessAsync(), StartTimeout, "cold boot");
            Assert.IsTrue(vm.QmpConnected);

            tipPath = Path.Combine(
                TestDiskUtil.TestFixtureDirectory, $"tip-{Guid.NewGuid():N}.qcow2");
            statePath = Path.Combine(
                TestDiskUtil.TestFixtureDirectory, $"state-{Guid.NewGuid():N}.uqsnap");

            try
            {
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
                TestDiskUtil.DestroyImmediate(snap);
                TestDiskUtil.DestroyImmediate(tipDisk);
                TestDiskUtil.SafeDelete(statePath);
                TestDiskUtil.SafeDelete(tipPath);
            }
        }
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
}

/// <summary>
/// Opens <see cref="VirtualMachineSmokeTests.ScenePath"/> additively, resolves the
/// SmokeVM, and restores the prior scene setup on dispose.
/// </summary>
sealed class SmokeSceneFixture : IDisposable
{
    readonly SceneSetup[] _priorSetup;
    readonly Scene _scene;

    public VirtualMachine Vm { get; }

    SmokeSceneFixture(SceneSetup[] priorSetup, Scene scene, VirtualMachine vm)
    {
        _priorSetup = priorSetup;
        _scene = scene;
        Vm = vm;
    }

    public static Task<SmokeSceneFixture> OpenAsync()
    {
        AssetDatabase.ImportAsset(
            VirtualMachineSmokeTests.EmptyDiskPath, ImportAssetOptions.ForceUpdate);
        DiskAsset disk = AssetDatabase.LoadAssetAtPath<DiskAsset>(
            VirtualMachineSmokeTests.EmptyDiskPath);
        Assert.IsNotNull(
            disk,
            $"Missing DiskAsset at {VirtualMachineSmokeTests.EmptyDiskPath}");

        SceneSetup[] prior = EditorSceneManager.GetSceneManagerSetup();
        Scene scene = EditorSceneManager.OpenScene(
            VirtualMachineSmokeTests.ScenePath, OpenSceneMode.Additive);
        Assert.IsTrue(scene.IsValid() && scene.isLoaded, "SmokeTest scene failed to load");

        VirtualMachine vm = scene.GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<VirtualMachine>(true))
            .FirstOrDefault();
        Assert.IsNotNull(vm, "SmokeTest scene has no VirtualMachine");

        // Keep the fixture disk wired even if YAML refs lagged a fresh import.
        if (vm.diskAsset == null)
            vm.diskAsset = disk;

        vm.runVmInEditMode = false;
        vm.autoRestart = false;
        vm.playAudioInUnity = false;
        vm.showGui = false;
        vm.enableGdb = false;

        return Task.FromResult(new SmokeSceneFixture(prior, scene, vm));
    }

    public void Dispose()
    {
        // Closing the scene disables SmokeVM → VirtualMachine.OnDisable → StopQemu.
        if (_scene.IsValid() && _scene.isLoaded)
            EditorSceneManager.CloseScene(_scene, removeScene: true);
        if (_priorSetup != null && _priorSetup.Length > 0)
            EditorSceneManager.RestoreSceneManagerSetup(_priorSetup);
    }
}}
