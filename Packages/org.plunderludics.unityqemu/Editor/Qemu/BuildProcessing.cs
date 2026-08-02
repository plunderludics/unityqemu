using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityQemu {
/// <summary>
/// Packages QEMU binaries and referenced guest images into player builds.
/// Follows UnityHawk <c>BuildProcessing</c>: collect scene deps → copy into
/// <c>{exe}_Data/QemuAssets</c>, and copy <c>qemu~</c> when anything is used.
/// </summary>
public class BuildProcessing :
    IPreprocessBuildWithReport,
    IProcessSceneWithReport,
    IPostprocessBuildWithReport
{
    const string ManifestFileName = "qemu-i386.manifest";

    static readonly Dictionary<string, HashSet<CopyItem>> FilesForScene = new();
    static readonly HashSet<DiskAsset> DisksForBuild = new();
    static string _lastProcessedScenePath;
    static bool _trimQemuToI386;
    static bool _obfuscateGuestFileNames;

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        string qemuDir = GetQemuDirInBuild(report.summary.outputPath);
        if (Directory.Exists(qemuDir))
            Directory.Delete(qemuDir, recursive: true);

        string assetsDir = GetQemuAssetsDirInBuild(report.summary.outputPath);
        if (Directory.Exists(assetsDir))
            Directory.Delete(assetsDir, recursive: true);

        FilesForScene.Clear();
        DisksForBuild.Clear();
        _lastProcessedScenePath = null;
        ReadProjectBuildSettings();
    }

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (!BuildPipeline.isBuildingPlayer)
            return;

        CollectSceneFiles(scene);
        _lastProcessedScenePath = scene.path;
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        // Re-read in case settings changed, or scene process was skipped on rebuild.
        ReadProjectBuildSettings();

        string exePath = report.summary.outputPath;
        int nCopied = CopyFilesToBuild(exePath);
        Debug.Log(
            $"[UnityQemu] Copied {nCopied} guest image file(s) into build" +
            (_obfuscateGuestFileNames ? " (obfuscated names)." : "."));

        if (_obfuscateGuestFileNames && nCopied > 0)
            RebaseObfuscatedDiskChains(GetQemuAssetsDirInBuild(exePath));

        if (nCopied == 0)
        {
            Debug.LogWarning(
                "[UnityQemu] No disk/snapshot/ISO files referenced in build scenes — " +
                "skipping qemu~ copy.");
            return;
        }

        string sourceQemu = Paths.QemuDir;
        if (!Directory.Exists(sourceQemu))
        {
            throw new FileNotFoundException(
                "[UnityQemu] qemu~ directory missing — cannot package QEMU into the build.",
                sourceQemu);
        }

        string targetQemu = GetQemuDirInBuild(exePath);
        if (_trimQemuToI386)
            CopyTrimmedQemu(sourceQemu, targetQemu);
        else
            CopyFullQemu(sourceQemu, targetQemu);
    }

    static void ReadProjectBuildSettings()
    {
        var settings = UnityQemuProjectSettings.instance;
        _trimQemuToI386 = settings.TrimQemuToI386;
        _obfuscateGuestFileNames = settings.ObfuscateGuestFileNames;
    }

    static void CopyFullQemu(string sourceQemu, string targetQemu)
    {
        Debug.Log($"[UnityQemu] Copying full qemu~ from '{sourceQemu}' to '{targetQemu}'…");
        Directory.CreateDirectory(targetQemu);
        FileUtil.ReplaceDirectory(
            Path.GetFullPath(sourceQemu),
            Path.GetFullPath(targetQemu));
    }

    static void CopyTrimmedQemu(string sourceQemu, string targetQemu)
    {
        string manifestPath = ResolveManifestPath(sourceQemu);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "[UnityQemu] trimQemuToI386 is on but qemu-i386.manifest is missing. " +
                "Regenerate with Editor/Qemu/GenerateQemuI386Manifest.py, or turn trim off.",
                manifestPath);
        }

        var entries = new List<string>();
        foreach (string raw in File.ReadAllLines(manifestPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            entries.Add(line.Replace('\\', '/'));
        }

        Debug.Log(
            $"[UnityQemu] Copying trimmed qemu~ ({entries.Count} files from {ManifestFileName}) " +
            $"to '{targetQemu}'…");
        Directory.CreateDirectory(targetQemu);

        var missing = new List<string>();
        int copied = 0;
        foreach (string rel in entries)
        {
            string src = Path.Combine(sourceQemu, rel);
            if (!File.Exists(src))
            {
                missing.Add(rel);
                continue;
            }

            string dst = Path.Combine(targetQemu, rel);
            string dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite: true);
            copied++;
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"[UnityQemu] trimQemuToI386: {missing.Count} manifest entries missing under qemu~. " +
                $"First: '{missing[0]}'");
        }

        Debug.Log($"[UnityQemu] Trimmed qemu~ copy complete ({copied} files).");
    }

    static string ResolveManifestPath(string qemuDir)
    {
        // Package root is the parent of qemu~ (works for Packages/ and PackageCache/).
        string packageRoot = Directory.GetParent(Path.GetFullPath(qemuDir))?.FullName;
        if (!string.IsNullOrEmpty(packageRoot))
        {
            string candidate = Path.Combine(packageRoot, ManifestFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        // Fallback: project Packages/org.plunderludics.unityqemu/
        return Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "Packages", Paths.PackageName, ManifestFileName));
    }

    void CollectSceneFiles(Scene scene)
    {
        var disks = new HashSet<DiskAsset>();
        var items = new HashSet<CopyItem>(CollectCopyItemsFromOpenScene(disks));
        FilesForScene[scene.path] = items;
        foreach (DiskAsset disk in disks)
            DisksForBuild.Add(disk);
        Debug.Log($"[UnityQemu] Collected {items.Count} file(s) for scene '{scene.path}'.");
    }

    int CopyFilesToBuild(string exePath)
    {
        string assetsRoot = GetQemuAssetsDirInBuild(exePath);
        int nCopied = 0;

        var scenePaths = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToList();

        if (scenePaths.Count == 0)
        {
            if (string.IsNullOrEmpty(_lastProcessedScenePath))
                throw new Exception(
                    "[UnityQemu] No scenes in Build Settings and no processed scene.");
            scenePaths.Add(_lastProcessedScenePath);
            Debug.Log(
                $"[UnityQemu] No scenes in Build Settings — using last processed " +
                $"'{_lastProcessedScenePath}'.");
        }

        foreach (string scenePath in scenePaths)
        {
            if (!FilesForScene.ContainsKey(scenePath))
            {
                Debug.LogWarning(
                    $"[UnityQemu] Scene '{scenePath}' not processed yet — collecting now.");
                Scene scene = EditorSceneManager.OpenScene(scenePath);
                CollectSceneFiles(scene);
            }

            foreach (CopyItem item in FilesForScene[scenePath])
            {
                if (CopyItemToBuild(item, assetsRoot))
                    nCopied++;
            }
        }

        return nCopied;
    }

    static bool CopyItemToBuild(CopyItem item, string assetsRoot)
    {
        if (item == null || string.IsNullOrEmpty(item.SourceAbsolutePath))
            return false;
        if (!File.Exists(item.SourceAbsolutePath) && !Directory.Exists(item.SourceAbsolutePath))
        {
            Debug.LogError(
                $"[UnityQemu] Missing source for build copy: '{item.SourceAbsolutePath}'");
            return false;
        }

        string outPath = Path.Combine(assetsRoot, item.BuildRelativeLocation);
        string outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        if (item.IsDirectory)
        {
            if (Directory.Exists(outPath))
                return false;
            FileUtil.ReplaceDirectory(
                Path.GetFullPath(item.SourceAbsolutePath),
                Path.GetFullPath(outPath));
        }
        else
        {
            if (File.Exists(outPath))
                return false;
            FileUtil.ReplaceFile(
                Path.GetFullPath(item.SourceAbsolutePath),
                Path.GetFullPath(outPath));
        }

        return true;
    }

    /// <summary>
    /// After obfuscated copy, rewrite qcow2 backing headers to the hashed sibling names.
    /// </summary>
    static void RebaseObfuscatedDiskChains(string assetsRoot)
    {
        int rebased = 0;
        foreach (DiskAsset disk in DisksForBuild)
        {
            if (disk == null || disk.backingDisk == null)
                continue;

            string overlayRel = EffectiveDiskProjectPath(disk);
            string backingRel = EffectiveDiskProjectPath(disk.backingDisk);
            if (string.IsNullOrEmpty(overlayRel) || string.IsNullOrEmpty(backingRel))
                continue;

            string overlayAbs = Path.Combine(
                assetsRoot, Paths.ToObfuscatedBuildFileName(overlayRel));
            string backingAbs = Path.Combine(
                assetsRoot, Paths.ToObfuscatedBuildFileName(backingRel));
            if (!File.Exists(overlayAbs) || !File.Exists(backingAbs))
                continue;

            DiskOverlay.RebaseHeaderOnto(overlayAbs, backingAbs);
            rebased++;
        }

        if (rebased > 0)
            Debug.Log($"[UnityQemu] Rebased {rebased} obfuscated qcow2 backing header(s).");
    }

    /// <summary>
    /// Collect serialized open-scene dependencies for DiskAsset / UqsnapAsset /
    /// CdRomAsset / FloppyAsset and expand qcow2 chains.
    /// </summary>
    public static IEnumerable<CopyItem> CollectCopyItemsFromOpenScene(bool includeInactive = true) =>
        CollectCopyItemsFromOpenScene(disksOut: null, includeInactive);

    public static IEnumerable<CopyItem> CollectCopyItemsFromOpenScene(
        HashSet<DiskAsset> disksOut,
        bool includeInactive = true)
    {
        var refs = new HashSet<UnityEngine.Object>();
        var components = UnityEngine.Object.FindObjectsByType<Component>(
            includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        var roots = new List<UnityEngine.Object>();

        foreach (Component component in components)
        {
            if (component == null)
                continue;
            if (!includeInactive && component is Behaviour behaviour && !behaviour.enabled)
                continue;
            roots.Add(component);
        }

        // Explicit extras from optional QemuExtraBuildAssets.
        QemuExtraBuildAssets extras = FindExtraBuildAssets();
        if (extras != null && extras.extraAssets != null)
        {
            foreach (UnityEngine.Object extra in extras.extraAssets)
            {
                if (extra != null)
                {
                    refs.Add(extra);
                    roots.Add(extra);
                }
            }
        }

        // Let Unity traverse serialized references, including nested serializable
        // classes and ScriptableObject dependencies. Avoid reflecting over arbitrary
        // component runtime state, which may contain non-enumerable native containers.
        foreach (UnityEngine.Object dependency in
                 EditorUtility.CollectDependencies(roots.ToArray()))
        {
            ConsiderUnityObject(dependency, refs);
        }

        var items = new HashSet<CopyItem>();
        foreach (UnityEngine.Object obj in refs)
            ExpandReference(obj, items, disksOut);
        return items;
    }

    static QemuExtraBuildAssets FindExtraBuildAssets()
    {
        var all = UnityEngine.Object.FindObjectsByType<QemuExtraBuildAssets>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (all.Length == 0)
            return null;
        if (all.Length > 1)
        {
            Debug.LogWarning(
                $"[UnityQemu] {all.Length} QemuExtraBuildAssets in scene — using the first.");
        }
        return all[0];
    }

    static void ExpandReference(
        UnityEngine.Object obj,
        HashSet<CopyItem> items,
        HashSet<DiskAsset> disksOut)
    {
        switch (obj)
        {
            case UqsnapAsset snap:
                TryAddProjectFile(EffectiveUqsnapProjectPath(snap), items);
                if (snap.disk != null)
                    ExpandReference(snap.disk, items, disksOut);
                break;

            case DiskAsset disk:
                foreach (DiskAsset link in disk.GetChainFromRoot())
                {
                    disksOut?.Add(link);
                    TryAddProjectFile(EffectiveDiskProjectPath(link), items);
                }
                break;

            case CdRomAsset cd:
                TryAddProjectFile(EffectiveCdRomProjectPath(cd), items);
                break;

            case FloppyAsset floppy:
                TryAddProjectFile(EffectiveFloppyProjectPath(floppy), items);
                break;
        }
    }

    static string EffectiveDiskProjectPath(DiskAsset disk)
    {
        if (disk == null)
            return null;
        if (!string.IsNullOrEmpty(disk.projectRelativeQcow2Path))
            return Paths.NormalizeProjectRelativePath(disk.projectRelativeQcow2Path);
        string assetPath = AssetDatabase.GetAssetPath(disk);
        return DiskAsset.IsQemuImageAssetPath(assetPath)
            ? Paths.NormalizeProjectRelativePath(assetPath)
            : null;
    }

    static string EffectiveUqsnapProjectPath(UqsnapAsset snap)
    {
        if (snap == null)
            return null;
        if (!string.IsNullOrEmpty(snap.projectRelativeUqsnapPath))
            return Paths.NormalizeProjectRelativePath(snap.projectRelativeUqsnapPath);
        string assetPath = AssetDatabase.GetAssetPath(snap);
        return !string.IsNullOrEmpty(assetPath) &&
               assetPath.EndsWith(".uqsnap", StringComparison.OrdinalIgnoreCase)
            ? Paths.NormalizeProjectRelativePath(assetPath)
            : null;
    }

    static string EffectiveCdRomProjectPath(CdRomAsset cd)
    {
        if (cd == null)
            return null;
        if (!string.IsNullOrEmpty(cd.projectRelativeIsoPath))
            return Paths.NormalizeProjectRelativePath(cd.projectRelativeIsoPath);
        string assetPath = AssetDatabase.GetAssetPath(cd);
        return !string.IsNullOrEmpty(assetPath) &&
               assetPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
            ? Paths.NormalizeProjectRelativePath(assetPath)
            : null;
    }

    static string EffectiveFloppyProjectPath(FloppyAsset floppy)
    {
        if (floppy == null)
            return null;
        if (!string.IsNullOrEmpty(floppy.projectRelativeImgPath))
            return Paths.NormalizeProjectRelativePath(floppy.projectRelativeImgPath);
        string assetPath = AssetDatabase.GetAssetPath(floppy);
        if (string.IsNullOrEmpty(assetPath))
            return null;
        if (assetPath.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
            assetPath.EndsWith(".ima", StringComparison.OrdinalIgnoreCase))
            return Paths.NormalizeProjectRelativePath(assetPath);
        return null;
    }

    static void TryAddProjectFile(string projectRelativePath, HashSet<CopyItem> items)
    {
        if (string.IsNullOrEmpty(projectRelativePath))
            return;

        string abs = Paths.ResolveProjectRelativeFile(projectRelativePath);
        string loc = _obfuscateGuestFileNames
            ? Paths.ToObfuscatedBuildFileName(projectRelativePath)
            : Paths.ToBuildRelativeLocation(projectRelativePath);
        if (string.IsNullOrEmpty(abs) || string.IsNullOrEmpty(loc))
            return;

        items.Add(new CopyItem(abs, loc, isDirectory: false));
    }

    static void ConsiderUnityObject(
        UnityEngine.Object uo,
        HashSet<UnityEngine.Object> discovered)
    {
        if (uo == null)
            return;
        if (uo is DiskAsset || uo is UqsnapAsset || uo is CdRomAsset || uo is FloppyAsset)
            discovered.Add(uo);
    }

    static string GetBuildDataDir(string exePath) =>
        Path.Combine(
            Path.GetDirectoryName(exePath)!,
            $"{Path.GetFileNameWithoutExtension(exePath)}_Data");

    static string GetQemuAssetsDirInBuild(string exePath) =>
        Path.Combine(GetBuildDataDir(exePath), Paths.QemuAssetsDirName);

    static string GetQemuDirInBuild(string exePath) =>
        Path.Combine(GetBuildDataDir(exePath), Paths.PackageName, Paths.QemuDirName);

    public sealed class CopyItem : IEquatable<CopyItem>
    {
        public readonly string SourceAbsolutePath;
        public readonly string BuildRelativeLocation;
        public readonly bool IsDirectory;

        public CopyItem(string sourceAbsolutePath, string buildRelativeLocation, bool isDirectory)
        {
            SourceAbsolutePath = sourceAbsolutePath?.Replace('\\', '/');
            BuildRelativeLocation = buildRelativeLocation?.Replace('\\', '/');
            IsDirectory = isDirectory;
        }

        public bool Equals(CopyItem other) =>
            other != null &&
            string.Equals(
                BuildRelativeLocation, other.BuildRelativeLocation,
                StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => Equals(obj as CopyItem);

        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(BuildRelativeLocation ?? "");

        public override string ToString() => BuildRelativeLocation;
    }
}
}
