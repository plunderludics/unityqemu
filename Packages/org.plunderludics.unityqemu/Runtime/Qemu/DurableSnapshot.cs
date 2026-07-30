using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Durable tip save/load: freeze a work layer, optionally migrate RAM/CPU to a
/// <c>.uqsnap</c>, write a thin <c>.qcow2</c> child, and wire Unity assets.
/// Editor-only asset import path; QEMU ops go through <see cref="VirtualMachine"/>.
/// </summary>
public static class DurableSnapshot
{
#if UNITY_EDITOR
    /// <summary>
    /// Capture + write thin disk tip. Always creates/updates
    /// <paramref name="qcow2ProjectPath"/>. When <paramref name="uqsnapProjectPath"/>
    /// is set and migrate succeeds, also creates the <c>.uqsnap</c>; otherwise returns
    /// the <see cref="DiskAsset"/> alone. Pass null/empty uqsnap for an explicit
    /// disk-only tip.
    /// </summary>
    public static async Task<BootableAsset> SaveAsync(
        VirtualMachine vm,
        string qcow2ProjectPath,
        string uqsnapProjectPath,
        DiskAsset immediateParent,
        bool compressMachineState = true,
        bool captureScreenshot = true,
        Action<string> progress = null)
    {
        if (vm == null)
            throw new ArgumentNullException(nameof(vm));
        if (!vm.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
        if (immediateParent == null)
            throw new ArgumentNullException(nameof(immediateParent));
        if (string.IsNullOrWhiteSpace(qcow2ProjectPath))
            throw new ArgumentException("qcow2 output path required", nameof(qcow2ProjectPath));

        bool wantMachineState = !string.IsNullOrWhiteSpace(uqsnapProjectPath);

        string parentPath = immediateParent.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(parentPath) || !File.Exists(parentPath))
            throw new FileNotFoundException(
                $"Parent '{immediateParent.name}' has no image file", parentPath);

        string qcow2Full = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", qcow2ProjectPath));
        string uqsnapFull = wantMachineState
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", uqsnapProjectPath))
            : null;
        // When overwriting a former uqsnap as disk-only, clear the stale .uqsnap beside the tip.
        string staleUqsnapFull = Path.ChangeExtension(qcow2Full, ".uqsnap");
        string pngProjectPath = wantMachineState
            ? UqsnapAsset.SiblingScreenshotProjectPath(uqsnapProjectPath)
            : null;
        string pngFull = string.IsNullOrEmpty(pngProjectPath)
            ? null
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", pngProjectPath));
        string stateTmp = wantMachineState
            ? Path.Combine(Application.temporaryCachePath, Path.GetFileName(uqsnapFull) + ".new")
            : null;

        bool wroteScreenshot = false;
        if (wantMachineState && captureScreenshot && !string.IsNullOrEmpty(pngFull))
        {
            progress?.Invoke("Capturing screenshot…");
            wroteScreenshot = TryWriteScreenshotPng(vm.Texture, pngFull);
            if (!wroteScreenshot)
            {
                Debug.LogWarning(
                    "UnityQemu: could not capture snapshot screenshot " +
                    "(no VNC frame yet?). Saving without preview.");
            }
        }

        progress?.Invoke(wantMachineState ? "Capturing state…" : "Freezing disk layer…");
        VirtualMachine.CaptureStateResult capture = await vm.CaptureStateAsync(
            stateTmp, gzip: compressMachineState, captureMachineState: wantMachineState);
        string frozenLayer = capture.FrozenLayerPath;
        bool hasMachineState = wantMachineState &&
                               capture.CapturedMachineState &&
                               !string.IsNullOrEmpty(stateTmp) &&
                               File.Exists(stateTmp) &&
                               new FileInfo(stateTmp).Length > 0;

        progress?.Invoke("Writing disk diff…");
        bool qemuStillRunning = true;
        try
        {
            try
            {
                DiskOverlay.ConvertThin(frozenLayer, parentPath, qcow2Full);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"Could not write '{qcow2Full}' while QEMU is running ({e.Message}). " +
                    "Stopping QEMU to write, then restarting into the saved state.");
                await vm.StopGuestProcessAsync();
                qemuStillRunning = false;
                DiskOverlay.ConvertThin(frozenLayer, parentPath, qcow2Full);
            }

            string uqsnapToClear = uqsnapFull ?? staleUqsnapFull;
            if (!string.IsNullOrEmpty(uqsnapToClear) && File.Exists(uqsnapToClear))
                File.Delete(uqsnapToClear);
            if (hasMachineState)
                File.Move(stateTmp, uqsnapFull);
            else if (!string.IsNullOrEmpty(stateTmp))
                try { if (File.Exists(stateTmp)) File.Delete(stateTmp); } catch { /* ignore */ }
        }
        catch
        {
            if (!string.IsNullOrEmpty(stateTmp))
                try { if (File.Exists(stateTmp)) File.Delete(stateTmp); } catch { /* ignore */ }
            throw;
        }

        AssetDatabase.ImportAsset(qcow2ProjectPath, ImportAssetOptions.ForceUpdate);
        AssetImporter diskImporter = AssetImporter.GetAtPath(qcow2ProjectPath);
        if (diskImporter == null)
            throw new Exception($"No AssetImporter for '{qcow2ProjectPath}'");
        var diskSo = new SerializedObject(diskImporter);
        SerializedProperty backingProp = diskSo.FindProperty("backingDisk");
        if (backingProp == null)
            throw new Exception($"qcow2 importer missing backingDisk on '{qcow2ProjectPath}'");
        backingProp.objectReferenceValue = immediateParent;
        diskSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(diskImporter);
        AssetDatabase.WriteImportSettingsIfDirty(qcow2ProjectPath);
        diskImporter.SaveAndReimport();

        DiskAsset diskAsset = AssetDatabase.LoadAssetAtPath<DiskAsset>(qcow2ProjectPath);
        if (diskAsset == null)
            throw new Exception($"No DiskAsset at '{qcow2ProjectPath}'");

        if (wroteScreenshot && !string.IsNullOrEmpty(pngProjectPath))
            ImportScreenshotPng(pngProjectPath);

        BootableAsset saved;
        if (hasMachineState)
        {
            AssetDatabase.ImportAsset(uqsnapProjectPath, ImportAssetOptions.ForceUpdate);
            AssetImporter snapImporter = AssetImporter.GetAtPath(uqsnapProjectPath);
            if (snapImporter == null)
                throw new Exception($"No AssetImporter for '{uqsnapProjectPath}'");

            LaunchConfig effective = vm.EffectiveLaunchConfig;
            LaunchConfig toStore = effective != null
                ? effective.Clone()
                : LaunchConfig.CreateDefault();

            var snapSo = new SerializedObject(snapImporter);
            snapSo.FindProperty("disk").objectReferenceValue = diskAsset;
            SerializedProperty metaProperty = snapSo.FindProperty("metadata");
            if (metaProperty == null)
                throw new Exception($"uqsnap importer missing metadata on '{uqsnapProjectPath}'");

            metaProperty.FindPropertyRelative("createdAt").stringValue = DateTime.UtcNow.ToString("o");
            metaProperty.FindPropertyRelative("vmstateUncompressed").boolValue = !compressMachineState;
            metaProperty.FindPropertyRelative("qemuVersion").stringValue =
                VirtualMachine.QueryBundledQemuVersion();
            metaProperty.FindPropertyRelative("unityQemuVersion").stringValue =
                VirtualMachine.QueryUnityQemuPackageVersion();

            SerializedProperty launchConfigProperty = metaProperty.FindPropertyRelative("launchConfig");
            if (launchConfigProperty != null)
            {
                launchConfigProperty.FindPropertyRelative("memoryMb").intValue = toStore.memoryMb;
                launchConfigProperty.FindPropertyRelative("usbEhci").boolValue = toStore.usbEhci;
                launchConfigProperty.FindPropertyRelative("usbEhciId").stringValue =
                    toStore.usbEhciId ?? LaunchConfig.DefaultUsbEhciId;
                launchConfigProperty.FindPropertyRelative("usbEhciPciAddr").stringValue =
                    toStore.usbEhciPciAddr ?? "";
                launchConfigProperty.FindPropertyRelative("extraQemuArgs").stringValue =
                    toStore.extraQemuArgs ?? "";
                SetObjectReferenceArray(
                    launchConfigProperty.FindPropertyRelative("cdroms"), toStore.cdroms);
                SetObjectReferenceArray(
                    launchConfigProperty.FindPropertyRelative("floppies"), toStore.floppies);
            }

            snapSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(snapImporter);
            AssetDatabase.WriteImportSettingsIfDirty(uqsnapProjectPath);
            snapImporter.SaveAndReimport();

            var snapAsset = AssetDatabase.LoadAssetAtPath<UqsnapAsset>(uqsnapProjectPath);
            if (snapAsset == null)
                throw new Exception($"No UqsnapAsset at '{uqsnapProjectPath}'");

            Debug.Log(
                $"Durable snapshot saved: {uqsnapProjectPath} + {qcow2ProjectPath} " +
                $"(backing={immediateParent.name}, memoryMb={toStore.memoryMb})");
            saved = snapAsset;
        }
        else
        {
            string staleUqsnapProject = Path.ChangeExtension(qcow2ProjectPath, ".uqsnap");
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(staleUqsnapProject)))
                AssetDatabase.DeleteAsset(staleUqsnapProject);
            if (wantMachineState &&
                !string.IsNullOrEmpty(uqsnapProjectPath) &&
                !string.Equals(uqsnapProjectPath, staleUqsnapProject, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(uqsnapProjectPath)))
                AssetDatabase.DeleteAsset(uqsnapProjectPath);

            Debug.Log(
                $"Disk-only tip saved: {qcow2ProjectPath} " +
                $"(backing={immediateParent.name}; no .uqsnap)");
            saved = diskAsset;
        }

        if (!qemuStillRunning)
        {
            if (saved is UqsnapAsset snap)
                vm.PrepareBoot(snap, loadVmState: true);
            else
                vm.PrepareBoot(diskAsset);
            await vm.StartGuestProcessAsync();
        }
        else
        {
            // Session keeps running on the new work layer; update session tip only
            // (boot-config Snapshot / Disk slots stay as configured).
            vm.SetSessionCurrent(saved);
        }

        return saved;
    }

    /// <summary>Stop the guest, prepare the uqsnap, and start into that tip.</summary>
    public static async Task LoadAsync(VirtualMachine vm, UqsnapAsset snap)
    {
        if (vm == null)
            throw new ArgumentNullException(nameof(vm));
        if (snap == null)
            throw new ArgumentNullException(nameof(snap));
        if (snap.disk == null)
            throw new InvalidOperationException($"'{snap.name}' has no linked disk");

        await vm.StopGuestProcessAsync();
        vm.PrepareBoot(snap, loadVmState: true);
        await vm.StartGuestProcessAsync();
    }

    /// <summary>Reload the in-session quick-save written during durable capture.</summary>
    public static async Task ReloadSessionStateAsync(VirtualMachine vm)
    {
        if (vm == null)
            throw new ArgumentNullException(nameof(vm));
        await vm.RunHumanMonitorCommandOrThrowAsync(
            $"loadvm {DiskOverlay.DurableSaveVmTag}");
    }

    /// <summary>Project-relative path for an absolute filesystem path under the project root.</summary>
    public static string MakeProjectRelative(string fullPath)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
            + Path.DirectorySeparatorChar;
        fullPath = Path.GetFullPath(fullPath);
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return fullPath.Substring(root.Length).Replace('\\', '/');

        DiskAsset found = DiskAsset.FindByFilesystemPath(fullPath);
        if (found != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(found);
            if (!string.IsNullOrEmpty(assetPath))
                return assetPath.Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }

    static void SetObjectReferenceArray(SerializedProperty property, UnityEngine.Object[] values)
    {
        if (property == null || !property.isArray)
            return;

        values ??= Array.Empty<UnityEngine.Object>();
        property.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    static void ImportScreenshotPng(string pngProjectPath)
    {
        AssetDatabase.ImportAsset(pngProjectPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(pngProjectPath) as TextureImporter;
        if (importer == null)
            return;

        bool dirty = false;
        if (importer.npotScale != TextureImporterNPOTScale.None)
        {
            importer.npotScale = TextureImporterNPOTScale.None;
            dirty = true;
        }
        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            dirty = true;
        }
        if (importer.maxTextureSize < 4096)
        {
            importer.maxTextureSize = 4096;
            dirty = true;
        }
        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            dirty = true;
        }
        if (!dirty)
            return;

        EditorUtility.SetDirty(importer);
        importer.SaveAndReimport();
    }

    static bool TryWriteScreenshotPng(Texture2D source, string absolutePngPath)
    {
        if (source == null || source.width <= 0 || source.height <= 0)
            return false;
        if (string.IsNullOrEmpty(absolutePngPath))
            return false;

        Texture2D copy = null;
        try
        {
            copy = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            copy.SetPixels32(source.GetPixels32());
            copy.Apply(false, false);
            byte[] png = copy.EncodeToPNG();
            if (png == null || png.Length == 0)
                return false;

            string dir = Path.GetDirectoryName(absolutePngPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(absolutePngPath, png);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"UnityQemu: screenshot write failed ({absolutePngPath}): {e.Message}");
            return false;
        }
        finally
        {
            if (copy != null)
                UnityEngine.Object.DestroyImmediate(copy);
        }
    }
#endif
}
}
