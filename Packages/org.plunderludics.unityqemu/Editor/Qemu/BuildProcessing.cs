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
/// the player data folder under <c>QemuAssets</c>, and copy the host-matched
/// <c>qemu~</c> tree when anything is used.
/// </summary>
public class BuildProcessing :
    IPreprocessBuildWithReport,
    IProcessSceneWithReport,
    IPostprocessBuildWithReport
{
    const string ManifestFileName = "qemu-i386.manifest";

    static readonly HashSet<string> PlatformSubdirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        Paths.QemuHostSubdirWindows,
        Paths.QemuHostSubdirMacOS,
        Paths.QemuHostSubdirMacOSX64,
        Paths.QemuHostSubdirLinux,
    };

    static readonly Dictionary<string, HashSet<CopyItem>> FilesForScene = new();
    static readonly HashSet<DiskAsset> DisksForBuild = new();
    static string _lastProcessedScenePath;
    static bool _trimQemuToI386;
    static bool _obfuscateGuestFileNames;

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        string qemuDir = GetQemuDirInBuild(report.summary.outputPath, report.summary.platform);
        if (Directory.Exists(qemuDir))
            Directory.Delete(qemuDir, recursive: true);

        string assetsDir = GetQemuAssetsDirInBuild(report.summary.outputPath, report.summary.platform);
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

        BuildTarget target = report.summary.platform;
        string exePath = report.summary.outputPath;
        int nCopied = CopyFilesToBuild(exePath, target);
        Debug.Log(
            $"[UnityQemu] Copied {nCopied} guest image file(s) into build" +
            (_obfuscateGuestFileNames ? " (obfuscated names)." : "."));

        if (_obfuscateGuestFileNames && nCopied > 0)
            RebaseObfuscatedDiskChains(GetQemuAssetsDirInBuild(exePath, target));

        if (nCopied == 0)
        {
            Debug.LogWarning(
                "[UnityQemu] No disk/snapshot/ISO files referenced in build scenes — " +
                "skipping qemu~ copy.");
            return;
        }

        if (!TryGetQemuHostKind(target, out QemuHostKind hostKind))
        {
            throw new BuildFailedException(
                $"[UnityQemu] Unsupported player platform '{target}'. " +
                "Supported: Standalone Windows / macOS / Linux.");
        }

        string targetQemu = GetQemuDirInBuild(exePath, target);

        if (hostKind == QemuHostKind.MacOS)
        {
            PackageMacQemu(targetQemu);
        }
        else
        {
            string sourceQemu = Paths.ResolveEditorQemuDir(hostKind);
            if (!Paths.HasQemuSystemBinary(sourceQemu, hostKind))
            {
                throw new FileNotFoundException(
                    $"[UnityQemu] No QEMU binaries for {hostKind} at '{sourceQemu}'. " +
                    $"Place a {Paths.HostSubdirName(hostKind)} tree under Packages/{Paths.PackageName}/{Paths.QemuDirName}/ " +
                    "(see docs/host-qemu.md). Windows may also use a legacy flat qemu~ layout.",
                    sourceQemu);
            }

            bool canTrim = hostKind == QemuHostKind.Windows && _trimQemuToI386;
            if (_trimQemuToI386 && !canTrim)
            {
                Debug.LogWarning(
                    "[UnityQemu] Trim QEMU To i386 applies to Windows PE builds only; " +
                    $"copying the full {hostKind} qemu tree.");
            }

            if (canTrim)
                CopyTrimmedQemu(sourceQemu, targetQemu);
            else
                CopyFullQemu(sourceQemu, targetQemu, excludePlatformSiblings: IsLegacyFlatWindowsRoot(sourceQemu, hostKind));
        }

        if (hostKind != QemuHostKind.Windows)
        {
            Debug.LogWarning(
                "[UnityQemu] macOS/Linux QEMU packaging is best-effort. After copying to a " +
                "Mac/Linux machine, ensure binaries are executable (chmod +x) and clear " +
                "quarantine on macOS if Gatekeeper blocks launch (xattr -cr). " +
                "See docs/host-qemu.md.");
        }
    }

    enum MacQemuPackMode
    {
        Arm64Flat,
        X64Flat,
        UniversalNested,
    }

    static void PackageMacQemu(string targetQemu)
    {
        MacQemuPackMode mode = GetMacQemuPackMode();
        // Use exact arch trees (no cross-arch fallback) so universal builds can't
        // accidentally ship the same tree twice.
        string armSrc = Path.Combine(Paths.QemuRootDir, Paths.QemuHostSubdirMacOS);
        string x64Src = Path.Combine(Paths.QemuRootDir, Paths.QemuHostSubdirMacOSX64);

        switch (mode)
        {
            case MacQemuPackMode.Arm64Flat:
                RequireMacSource(armSrc, "Apple Silicon (macos/)");
                CopyFullQemu(armSrc, targetQemu, excludePlatformSiblings: false);
                break;
            case MacQemuPackMode.X64Flat:
                RequireMacSource(x64Src, "Intel (macos-x64/)");
                CopyFullQemu(x64Src, targetQemu, excludePlatformSiblings: false);
                break;
            case MacQemuPackMode.UniversalNested:
                RequireMacSource(armSrc, "Apple Silicon (macos/)");
                RequireMacSource(x64Src, "Intel (macos-x64/)");
                Directory.CreateDirectory(targetQemu);
                CopyFullQemu(
                    armSrc,
                    Path.Combine(targetQemu, Paths.QemuPlayerArchArm64),
                    excludePlatformSiblings: false);
                CopyFullQemu(
                    x64Src,
                    Path.Combine(targetQemu, Paths.QemuPlayerArchX64),
                    excludePlatformSiblings: false);
                Debug.Log(
                    "[UnityQemu] Universal Mac build: packaged both arm64/ and x64/ QEMU trees.");
                break;
        }
    }

    static void RequireMacSource(string sourceQemu, string label)
    {
        if (!Paths.HasQemuSystemBinary(sourceQemu, QemuHostKind.MacOS))
        {
            throw new FileNotFoundException(
                $"[UnityQemu] No QEMU binaries for {label} at '{sourceQemu}'. " +
                "See docs/host-qemu.md.",
                sourceQemu);
        }
    }

    /// <summary>
    /// Reads the Mac player Architecture build setting (Intel / Apple Silicon / Universal).
    /// Defaults to Apple Silicon when unset.
    /// </summary>
    static MacQemuPackMode GetMacQemuPackMode()
    {
        try
        {
            // Unity 6+: UnityEditor.OSXStandalone.UserBuildSettings.architecture
            var arch = UnityEditor.OSXStandalone.UserBuildSettings.architecture;
            string name = arch.ToString();
            if (name.IndexOf("x64ARM64", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0)
                return MacQemuPackMode.UniversalNested;
            if (string.Equals(name, "x64", StringComparison.OrdinalIgnoreCase))
                return MacQemuPackMode.X64Flat;
            return MacQemuPackMode.Arm64Flat;
        }
        catch (Exception e)
        {
            // Fallback when OSXStandalone is unavailable (older modules / odd editors).
            string raw = EditorUserBuildSettings.GetPlatformSettings(
                "Standalone", "OSXUniversal", "Architecture");
            Debug.LogWarning(
                $"[UnityQemu] Could not read OSXStandalone architecture ({e.GetType().Name}); " +
                $"platform setting='{raw}'. Defaulting via string parse.");
            if (string.Equals(raw, "x64ARM64", StringComparison.OrdinalIgnoreCase))
                return MacQemuPackMode.UniversalNested;
            if (string.Equals(raw, "x64", StringComparison.OrdinalIgnoreCase))
                return MacQemuPackMode.X64Flat;
            return MacQemuPackMode.Arm64Flat;
        }
    }

    static bool TryGetQemuHostKind(BuildTarget target, out QemuHostKind kind)
    {
        switch (target)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
                kind = QemuHostKind.Windows;
                return true;
            case BuildTarget.StandaloneOSX:
                kind = QemuHostKind.MacOS;
                return true;
            case BuildTarget.StandaloneLinux64:
                kind = QemuHostKind.Linux;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    static bool IsLegacyFlatWindowsRoot(string sourceQemu, QemuHostKind kind)
    {
        if (kind != QemuHostKind.Windows)
            return false;
        string root = Paths.QemuRootDir;
        return string.Equals(
            Path.GetFullPath(sourceQemu).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    // NOTE: the following methods continue below — placeholder removed
    static void ReadProjectBuildSettings()
    {
        var settings = UnityQemuProjectSettings.instance;
        _trimQemuToI386 = settings.TrimQemuToI386;
        _obfuscateGuestFileNames = settings.ObfuscateGuestFileNames;
    }

    static void CopyFullQemu(string sourceQemu, string targetQemu, bool excludePlatformSiblings)
    {
        Debug.Log($"[UnityQemu] Copying full qemu~ from '{sourceQemu}' to '{targetQemu}'…");
        Directory.CreateDirectory(targetQemu);
        if (!excludePlatformSiblings)
        {
            FileUtil.ReplaceDirectory(
                Path.GetFullPath(sourceQemu),
                Path.GetFullPath(targetQemu));
            return;
        }

        // Legacy flat Windows root may also hold macos/linux sibling trees — don't ship them.
        CopyDirectoryFiltered(sourceQemu, targetQemu, skipDirNames: PlatformSubdirNames);
    }

    static void CopyDirectoryFiltered(string sourceDir, string destDir, HashSet<string> skipDirNames)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destDir, name), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir);
            if (skipDirNames != null && skipDirNames.Contains(name))
                continue;
            CopyDirectoryFiltered(dir, Path.Combine(destDir, name), skipDirNames: null);
        }
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
        // Manifest lives at the package root (sibling of qemu~), not inside win/.
        string full = Path.GetFullPath(qemuDir);
        string dir = full;
        // Walk up until we find the package folder containing the manifest.
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, ManifestFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

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

    int CopyFilesToBuild(string exePath, BuildTarget target)
    {
        string assetsRoot = GetQemuAssetsDirInBuild(exePath, target);
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

    static string GetBuildDataDir(string outputPath, BuildTarget target)
    {
        // Mac player: Application.dataPath is <App>.app/Contents
        if (target == BuildTarget.StandaloneOSX)
            return Path.Combine(outputPath, "Contents");

        // Windows / Linux: {name}_Data next to the executable
        return Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}_Data");
    }

    static string GetQemuAssetsDirInBuild(string outputPath, BuildTarget target) =>
        Path.Combine(GetBuildDataDir(outputPath, target), Paths.QemuAssetsDirName);

    static string GetQemuDirInBuild(string outputPath, BuildTarget target) =>
        Path.Combine(GetBuildDataDir(outputPath, target), Paths.PackageName, Paths.QemuDirName);

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
