using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

using TriInspector;

namespace UnityQemu {
[ExecuteAlways]
[DeclareFoldoutGroup("Advanced", Expanded = false)]
public class VirtualMachine : MonoBehaviour
{
    Process _qemuProcess;
    VncClient _vncClient;
    QmpClient _qmpClient;
    GdbClient _gdbClient;
    bool _starting;

    /// <summary>Active work overlay path when using <see cref="diskAsset"/> + ephemeral overlay.</summary>
    string _workOverlayPath;

    /// <summary>
    /// Set by <see cref="PrepareBootFromDisk"/>: the next start must boot the prepared work
    /// image instead of minting a fresh one. Consumed by StartQemuAsync.
    /// </summary>
    bool _workPreparedForNextStart;

    /// <summary>If set, <c>loadvm</c> this tag after QMP connects (durable snapshot restore).</summary>
    string _pendingLoadVmTag;

    [ShowInInspector] bool VncConnected => _vncClient != null && _vncClient.IsConnected;
    [ShowInInspector] bool VncInternalClientConnected => _vncClient != null && _vncClient.IsInternalClientConnected;
    [ShowInInspector] public bool GdbConnected => _gdbClient != null && _gdbClient.IsConnected;
    [ShowInInspector] public bool GdbStopped => _gdbClient != null && _gdbClient.IsStopped;
    [ShowInInspector] public bool QmpConnected => _qmpClient != null && _qmpClient.IsConnected;

    public bool enableQmp = true;
    public bool enableGdb = true;
    [Tooltip("Log QMP connect/command traffic")]
    public bool verboseQmp = false;
    [Tooltip("Log GDB attach/interrupt/packet chatter")]
    public bool verboseGdb = false;
    [Tooltip("Also open QEMU's native SDL window (independent of snapshot launch config).")]
    public bool showGui = false;

    [Header("Input")]
    [Tooltip("If null, uses an attached InputProvider or adds a BasicInputProvider in Play mode.")]
    public InputProvider inputProvider;

    [Tooltip("Run QEMU and stream the VNC texture while the editor is not in Play mode")]
    public bool runInEditMode = false;

    [Header("Disk")]
    [Tooltip(
        "Image to boot: a plain .qcow2 DiskAsset (fresh overlay) or a .uqsnap DiskAsset " +
        "(byte-copy + loadvm when Auto Load Vm State is on).")]
    [OnValueChanged(nameof(OnDiskAssetChanged))]
    public DiskAsset diskAsset;

    [ShowIf(nameof(HasUqsnapInDiskSlot))]
    [Tooltip("When on (default), copy the .uqsnap and loadvm its embedded savevm tag on start.")]
    public bool autoLoadVmState = true;

    [ShowIf(nameof(HasUqsnapInDiskSlot))]
    [Tooltip(
        "When off (default), launch with extra QEMU args / CD / host folders from the disk's uqsnap metadata. " +
        "Turn on to edit and use the Launch Config below instead.")]
    [OnValueChanged(nameof(OnOverrideSnapshotLaunchConfigChanged))]
    public bool overrideSnapshotLaunchConfig;

    // Nested serializable foldout. Disabled when a uqsnap owns launch config.
    [DisableIf(nameof(ShowLockedSnapshotLaunchConfig))]
    [Tooltip(
        "Extra QEMU args and removable media. " +
        "With a .uqsnap and Override off, shows the snapshot's config (read-only).")]
    public LaunchConfig launchConfig = LaunchConfig.CreateDefault();

    [Group("Advanced")]
    [Tooltip(
        "Keep the assigned disk immutable by writing into a Library/ work overlay. " +
        "Leave on unless you intentionally want QEMU to write the Disk Asset file.")]
    public bool useEphemeralWorkOverlay = true;

    [SerializeField] private int vncPort = 5900;
    [SerializeField] private int qmpPort = 4444;
    [SerializeField] private int gdbPort = 1234;
    [SerializeField] private bool gdbPhysicalMemory = true;
    [SerializeField] private RenderTexture outputTexture; // This is kind of unnecessary should just use _vncClient.Texture directly, ideally..

    /// <summary>True when <see cref="diskAsset"/> is a .uqsnap (has <c>uqsnapMetadata</c>).</summary>
    public bool HasUqsnapInDiskSlot => diskAsset != null && diskAsset.HasVmState;

    bool HasVmState => HasUqsnapInDiskSlot;
    bool ShowLockedSnapshotLaunchConfig => HasUqsnapInDiskSlot && !overrideSnapshotLaunchConfig;

    /// <summary>
    /// Launch config stored on the boot .uqsnap, when it owns config (no override); else null.
    /// </summary>
    LaunchConfig SnapshotOwnedLaunchConfig =>
        HasVmState && !overrideSnapshotLaunchConfig ? diskAsset.uqsnapMetadata?.launchConfig : null;

    /// <summary>
    /// Launch config used for CD/floppy/host-folder/SMB when a uqsnap owns config; otherwise local.
    /// Extra args may still fall back via <see cref="EffectiveExtraQemuArgs"/> if the snapshot has none.
    /// </summary>
    public LaunchConfig EffectiveLaunchConfig => SnapshotOwnedLaunchConfig ?? launchConfig;

    /// <summary>
    /// Extra args actually passed to QEMU: uqsnap-stored when booting a snapshot without override
    /// (when the snapshot has args); otherwise the local <see cref="launchConfig"/>.
    /// </summary>
    public string EffectiveExtraQemuArgs
    {
        get
        {
            string snapArgs = SnapshotOwnedLaunchConfig?.extraQemuArgs;
            if (!string.IsNullOrWhiteSpace(snapArgs))
                return snapArgs;
            return launchConfig?.extraQemuArgs ?? "";
        }
    }

    public CdRomAsset[] EffectiveCdroms => EffectiveLaunchConfig?.cdroms;
    public UnityEngine.Object[] EffectiveFloppies => EffectiveLaunchConfig?.floppies;
    public UnityEngine.Object[] EffectiveHostFolders => EffectiveLaunchConfig?.hostFolders;

    /// <summary>Project folder for QEMU user-net SMB, or null when unset.</summary>
    public UnityEngine.Object EffectiveSmbShareFolder => EffectiveLaunchConfig?.smbShareFolder;

    /// <summary>
    /// Append media to the launch config that durable save will persist
    /// (<see cref="EffectiveLaunchConfig"/>): the uqsnap's in-memory metadata when locked,
    /// otherwise the local <see cref="launchConfig"/>. The inspector field is kept in sync.
    /// </summary>
    bool AddToEffectiveLaunchConfig(Func<LaunchConfig, bool> append)
    {
        LaunchConfig target;
        if (HasVmState && !overrideSnapshotLaunchConfig)
        {
            if (diskAsset.uqsnapMetadata == null)
                diskAsset.uqsnapMetadata = UqsnapMetadata.CreateEmpty();
            if (diskAsset.uqsnapMetadata.launchConfig == null)
                diskAsset.uqsnapMetadata.launchConfig = LaunchConfig.CreateDefault();
            target = diskAsset.uqsnapMetadata.launchConfig;
        }
        else
        {
            if (launchConfig == null)
                launchConfig = LaunchConfig.CreateDefault();
            target = launchConfig;
        }

        if (!append(target))
            return false;

        // Keep the inspector field in sync when we wrote snapshot metadata.
        if (launchConfig == null)
            launchConfig = LaunchConfig.CreateDefault();
        if (!ReferenceEquals(launchConfig, target))
            launchConfig.CopyFrom(target);

#if UNITY_EDITOR
        if (!ReferenceEquals(launchConfig, target))
            UnityEditor.EditorUtility.SetDirty(diskAsset);
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        return true;
    }

    public bool AddCdRomToEffectiveLaunchConfig(CdRomAsset asset) =>
        asset != null && AddToEffectiveLaunchConfig(cfg => cfg.AddCdRom(asset));

    public bool AddHostFolderToEffectiveLaunchConfig(UnityEngine.Object folder) =>
        folder != null && AddToEffectiveLaunchConfig(cfg => cfg.AddHostFolder(folder));

    public bool AddFloppyToEffectiveLaunchConfig(UnityEngine.Object source) =>
        source != null && AddToEffectiveLaunchConfig(cfg => cfg.AddFloppy(source));

    void OnOverrideSnapshotLaunchConfigChanged()
    {
        if (!HasVmState)
            return;
        SyncLaunchConfigFromDiskMetadata();
    }

    void SyncLaunchConfigFromDiskMetadata()
    {
        if (!HasVmState)
            return;
        if (launchConfig == null)
            launchConfig = LaunchConfig.CreateDefault();
        launchConfig.CopyFrom(
            diskAsset.uqsnapMetadata.launchConfig ?? LaunchConfig.CreateDefault());
    }

    void OnDiskAssetChanged()
    {
        if (HasVmState && !overrideSnapshotLaunchConfig)
            SyncLaunchConfigFromDiskMetadata();
    }

    bool ShouldRun => Application.isPlaying || runInEditMode;
    bool IsRunning => _qemuProcess != null && !_qemuProcess.HasExited;

    string ResolveDiskImagePath()
    {
        if (diskAsset == null)
            return null;

        DiskOverlay.EnsureBackingChain(diskAsset);
        if (ShouldByteCopyBoot)
            return EnsureWorkOverlayForBoot();

        if (useEphemeralWorkOverlay)
        {
            string work = EnsureWorkOverlayForBoot();
            if (!string.IsNullOrEmpty(work))
                return work;
        }

        return diskAsset.GetQcow2FilesystemPath();
    }

    /// <summary>
    /// .uqsnap images always byte-copy into the work file (never a thin overlay on the
    /// .uqsnap). <see cref="autoLoadVmState"/> only controls whether loadvm runs after boot.
    /// </summary>
    bool ShouldByteCopyBoot => HasVmState;

    /// <summary>Current -hda path (work overlay or configured disk).</summary>
    public string ActiveDiskPath =>
        !string.IsNullOrEmpty(_workOverlayPath) ? _workOverlayPath : ResolveDiskImagePath();

    public string WorkOverlayPath => _workOverlayPath;

    /// <summary>The configured boot image (plain disk or .uqsnap).</summary>
    public DiskAsset ActiveDiskAsset => diskAsset;

    /// <summary>
    /// Canonical backing path for a correctly prepared work image:
    /// byte-copied .uqsnap → its <see cref="DiskAsset.backingDisk"/>;
    /// thin overlay → the boot disk itself.
    /// </summary>
    public string ExpectedWorkBackingFilesystemPath
    {
        get
        {
            if (diskAsset == null)
                return null;
            if (ShouldByteCopyBoot)
            {
                return diskAsset.backingDisk != null
                    ? diskAsset.backingDisk.GetQcow2FilesystemPath()
                    : null;
            }
            return diskAsset.GetQcow2FilesystemPath();
        }
    }

    string WorkSessionId => $"{gameObject.name}-{GetInstanceID()}";

    /// <summary>Filesystem path to the bundled qemu-system-i386 binary.</summary>
    public static string ResolveQemuExecutablePath()
    {
        string qemuExe = Path.Combine(
            "Packages", "org.plunderludics.unityqemu", "qemu~", "qemu-system-i386.exe");
        return Path.GetFullPath(qemuExe);
    }

    /// <summary>First line of <c>qemu-system-… --version</c>, or empty on failure.</summary>
    public static string QueryBundledQemuVersion()
    {
        try
        {
            string exe = ResolveQemuExecutablePath();
            if (!File.Exists(exe))
                return "";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null)
                    return "";
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (string.IsNullOrWhiteSpace(stdout))
                    return "";
                string line = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                return line.Trim();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"Could not query QEMU version: {e.Message}");
            return "";
        }
    }

#if UNITY_EDITOR
    /// <summary>UnityQemu package version from <c>package.json</c>, or empty.</summary>
    public static string QueryUnityQemuPackageVersion()
    {
        try
        {
            string path = Path.GetFullPath(Path.Combine(
                "Packages", "org.plunderludics.unityqemu", "package.json"));
            if (!File.Exists(path))
                return "";
            var jo = JObject.Parse(File.ReadAllText(path));
            return jo["version"]?.Value<string>() ?? "";
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"Could not read UnityQemu package version: {e.Message}");
            return "";
        }
    }
#else
    public static string QueryUnityQemuPackageVersion() => "";
#endif

    void WarnIfSnapshotLaunchMetadataMismatched()
    {
        if (!HasVmState || overrideSnapshotLaunchConfig)
            return;

        UqsnapMetadata meta = diskAsset.uqsnapMetadata;
        string snapArgs = meta?.launchConfig != null ? meta.launchConfig.extraQemuArgs : null;
        if (string.IsNullOrWhiteSpace(snapArgs))
        {
            UnityEngine.Debug.LogWarning(
                $"Disk '{diskAsset.name}' has no stored extra QEMU args — " +
                "using the VirtualMachine Launch Config. Re-save the snapshot to record args.");
            return;
        }

        string currentQemu = QueryBundledQemuVersion();
        if (!string.IsNullOrEmpty(meta.qemuVersion) &&
            !string.IsNullOrEmpty(currentQemu) &&
            !string.Equals(meta.qemuVersion, currentQemu, StringComparison.Ordinal))
        {
            UnityEngine.Debug.LogWarning(
                $"Disk '{diskAsset.name}' was saved with QEMU '{meta.qemuVersion}', " +
                $"but this project has '{currentQemu}'. loadvm may fail if the versions are incompatible.");
        }
    }

    /// <summary>
    /// Ensure a work image exists for the configured disk (.uqsnap → byte-copy; else thin overlay).
    /// </summary>
    public string EnsureWorkOverlayForBoot()
    {
        if (diskAsset == null)
            return null;

        if (ShouldByteCopyBoot)
        {
            if (!IsValidByteCopyWorkImage())
                PrepareBootFromDisk(diskAsset, loadVmState: autoLoadVmState);
            else
                RepairWorkBackingHeader();
            return _workOverlayPath;
        }

        DiskOverlay.EnsureBackingChain(diskAsset);
        string basePath = diskAsset.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(basePath) || !File.Exists(basePath))
        {
            UnityEngine.Debug.LogError(
                $"DiskAsset '{diskAsset.name}' has no readable image at '{basePath}'");
            return null;
        }

        if (!IsValidThinWorkOverlay(basePath))
        {
            _workOverlayPath = DiskOverlay.CreateWorkOverlay(basePath, WorkSessionId);
            UnityEngine.Debug.Log($"Work overlay: {_workOverlayPath} (base={basePath})");
        }

        return _workOverlayPath;
    }

    /// <summary>
    /// Thin work overlay must back onto the boot disk. Reject leftover work from a prior
    /// .uqsnap session (byte-copy that backs onto a different parent) so switching back to
    /// a plain base doesn't keep showing that child's guest disk.
    /// </summary>
    bool IsValidThinWorkOverlay(string expectedBasePath)
    {
        if (string.IsNullOrEmpty(_workOverlayPath) || !File.Exists(_workOverlayPath))
            return false;
        if (string.IsNullOrEmpty(expectedBasePath))
            return false;

        try
        {
            string workBacking = DiskOverlay.GetBackingPath(_workOverlayPath);
            if (DiskOverlay.PathsEqual(workBacking, expectedBasePath))
                return true;

            UnityEngine.Debug.Log(
                $"UnityQemu: discarding work image (backs onto '{workBacking ?? "<none>"}', " +
                $"expected thin overlay on '{expectedBasePath}').");
            return false;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu: could not inspect work overlay; recreating. {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the work file is a byte-copy of the boot .uqsnap: it must back onto
    /// <see cref="DiskAsset.backingDisk"/>, not onto the boot .uqsnap itself (thin overlay).
    /// </summary>
    bool IsValidByteCopyWorkImage()
    {
        if (string.IsNullOrEmpty(_workOverlayPath) || !File.Exists(_workOverlayPath))
            return false;

        string expectedParent = ExpectedWorkBackingFilesystemPath;
        if (string.IsNullOrEmpty(expectedParent))
            return false;

        try
        {
            string workBacking = DiskOverlay.GetBackingPath(_workOverlayPath);
            if (DiskOverlay.PathsEqual(workBacking, expectedParent))
                return true;

            string bootPath = diskAsset.GetQcow2FilesystemPath();
            if (DiskOverlay.PathsEqual(workBacking, bootPath))
            {
                UnityEngine.Debug.LogWarning(
                    $"UnityQemu: work image is a thin overlay on '{diskAsset.name}'. " +
                    "Replacing with a byte-copy.");
            }
            return false;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu: could not inspect work overlay; recreating. {e.Message}");
            return false;
        }
    }

    void RepairWorkBackingHeader()
    {
        string expected = ExpectedWorkBackingFilesystemPath;
        if (string.IsNullOrEmpty(expected) || !File.Exists(expected))
            return;
        try
        {
            DiskOverlay.EnsureBackingMatches(_workOverlayPath, expected);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu: could not repair work backing header: {e.Message}");
        }
    }

    /// <summary>
    /// Replace the work image with a byte-copy of <paramref name="disk"/> and optionally request
    /// loadvm after next start. Call while QEMU is stopped.
    /// </summary>
    public void PrepareBootFromDisk(DiskAsset disk, bool loadVmState = true)
    {
        if (disk == null)
            throw new ArgumentNullException(nameof(disk));

        string imagePath = disk.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Disk image not found", imagePath);

        DiskOverlay.EnsureBackingChain(disk);
        string expectedBacking = disk.backingDisk != null
            ? disk.backingDisk.GetQcow2FilesystemPath()
            : null;
        _workOverlayPath = DiskOverlay.ReplaceWorkOverlayFromCopy(
            imagePath, WorkSessionId, expectedBacking);
        _workPreparedForNextStart = true;
        _pendingLoadVmTag = loadVmState && disk.HasVmState
            ? DiskOverlay.DurableSaveVmTag
            : null;
        diskAsset = disk;
        if (disk.HasVmState && !overrideSnapshotLaunchConfig)
            SyncLaunchConfigFromDiskMetadata();
        UnityEngine.Debug.Log(
            $"Prepared boot from '{disk.name}' → {_workOverlayPath}" +
            (_pendingLoadVmTag != null ? $" (loadvm {_pendingLoadVmTag})" : ""));
    }

    /// <summary>Queue a loadvm tag for the next successful QMP connect (cleared after attempt).</summary>
    public void RequestLoadVmOnReady(string tag)
    {
        _pendingLoadVmTag = tag;
    }

#if UNITY_EDITOR
    static string ResolveObjectFilesystemPath(UnityEngine.Object obj)
    {
        if (obj == null)
            return null;
        string assetPath = AssetDatabase.GetAssetPath(obj);
        if (string.IsNullOrEmpty(assetPath))
            return null;
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }

    /// <summary>
    /// Folder → <c>fat:floppy:ro:...</c> (must be ro — drive is readonly for savevm);
    /// otherwise filesystem path to the asset.
    /// </summary>
    static string ResolveFloppyFileSpec(UnityEngine.Object source)
    {
        if (source == null)
            return null;

        string assetPath = AssetDatabase.GetAssetPath(source);
        if (string.IsNullOrEmpty(assetPath))
        {
            UnityEngine.Debug.LogWarning($"Floppy source '{source.name}' has no asset path.");
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        if (AssetDatabase.IsValidFolder(assetPath))
            return $"fat:floppy:ro:{full.Replace('\\', '/')}";

        return full;
    }

    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources)
    {
        int index = 0;
        if (sources != null)
        {
            foreach (var source in sources)
            {
                string fileSpec = ResolveFloppyFileSpec(source);
                if (string.IsNullOrEmpty(fileSpec))
                    continue;
                // Must use if=floppy (A:/B:), not if=ide — IDE index 0 is already -hda.
                // readonly so savevm/loadvm are not blocked by raw/vvfat.
                args.Add("-drive");
                args.Add($"file={fileSpec},if=floppy,index={index},format=raw,readonly=on");
                index++;
            }
        }

        // No launch-config floppy: reserve an empty tray so PeripheralsUI can hotplug via
        // HMP `change`. null-co (1.44MB of zeros) satisfies QEMU 10's file= requirement;
        // readonly so savevm/loadvm work.
        if (index == 0)
        {
            args.Add("-drive");
            args.Add(
                $"id={EmptyFloppyDriveId},if=floppy,index=0,format=raw,readonly=on," +
                "file.driver=null-co,file.size=1474560");
        }
    }

    /// <summary>
    /// Folder → <c>fat:rw:...</c> vvfat disk (large FAT volume). Non-folders are ignored.
    /// </summary>
    static string ResolveHostFolderFileSpec(UnityEngine.Object source)
    {
        if (source == null)
            return null;

        string assetPath = AssetDatabase.GetAssetPath(source);
        if (string.IsNullOrEmpty(assetPath))
        {
            UnityEngine.Debug.LogWarning($"Host folder '{source.name}' has no asset path.");
            return null;
        }

        if (!AssetDatabase.IsValidFolder(assetPath))
        {
            UnityEngine.Debug.LogWarning(
                $"Host folder entry '{assetPath}' is not a folder — drag a project folder for vvfat.");
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        return $"fat:rw:{full.Replace('\\', '/')}";
    }

    /// <summary>
    /// Attach host folders as extra IDE disks (vvfat). Skips index 0 (-hda) and any
    /// indices already taken by CD-ROMs.
    /// </summary>
    static void AppendHostFolderArgs(
        System.Collections.Generic.IList<string> args,
        UnityEngine.Object[] sources,
        System.Collections.Generic.HashSet<int> usedIdeIndices)
    {
        if (sources == null)
            return;

        // Prefer leftover classic IDE units, then higher indices if QEMU exposes them.
        int[] ideIndices = { 1, 3, 2, 4, 5 };
        int attached = 0;
        foreach (var source in sources)
        {
            string fileSpec = ResolveHostFolderFileSpec(source);
            if (string.IsNullOrEmpty(fileSpec))
                continue;

            int index = -1;
            foreach (int candidate in ideIndices)
            {
                if (usedIdeIndices != null && usedIdeIndices.Contains(candidate))
                    continue;
                index = candidate;
                break;
            }

            if (index < 0)
            {
                UnityEngine.Debug.LogWarning(
                    "UnityQemu: no free IDE index left for host folder " +
                    $"'{source.name}' (CDs/disk already occupy the bus).");
                continue;
            }

            usedIdeIndices?.Add(index);
            args.Add("-drive");
            args.Add($"file={fileSpec},if=ide,index={index},format=raw");
            attached++;
        }

        if (sources.Length > 0 && attached == 0)
        {
            UnityEngine.Debug.LogWarning(
                "UnityQemu: no host folders attached — IDE slots exhausted by disk/CDs.");
        }
    }
#else
    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources) { }
    static void AppendHostFolderArgs(
        System.Collections.Generic.IList<string> args,
        UnityEngine.Object[] sources,
        System.Collections.Generic.HashSet<int> usedIdeIndices) { }

    static string InjectSmbShareIntoExtraArgs(string extraArgs, UnityEngine.Object smbFolder) =>
        extraArgs ?? "";
#endif

#if UNITY_EDITOR
    /// <summary>
    /// Inject <c>smb=</c> into an existing <c>-netdev user,...</c> line, or append a user netdev.
    /// Guest maps <c>\\10.0.2.4\qemu</c>.
    /// </summary>
    static string InjectSmbShareIntoExtraArgs(string extraArgs, UnityEngine.Object smbFolder)
    {
        if (smbFolder == null)
            return extraArgs ?? "";

        string assetPath = AssetDatabase.GetAssetPath(smbFolder);
        if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsValidFolder(assetPath))
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu SMB share '{smbFolder.name}' must be a project folder.");
            return extraArgs ?? "";
        }

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath))
            .Replace('\\', '/');
        if (full.IndexOfAny(new[] { ' ', '\t' }) >= 0)
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu SMB path has spaces ('{full}'); QEMU arg splitting may break. " +
                "Use a folder path without spaces.");
        }

        string extra = extraArgs ?? "";
        var netdevUser = new Regex(@"-netdev\s+(user,[^\s\r\n]+)", RegexOptions.IgnoreCase);
        Match m = netdevUser.Match(extra);
        if (m.Success)
        {
            string opts = m.Groups[1].Value;
            if (opts.IndexOf("smb=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                opts = Regex.Replace(
                    opts, @"smb=[^,]*", "smb=" + full, RegexOptions.IgnoreCase);
            }
            else
            {
                opts += ",smb=" + full;
            }

            return extra.Substring(0, m.Groups[1].Index) +
                   opts +
                   extra.Substring(m.Groups[1].Index + m.Groups[1].Length);
        }

        UnityEngine.Debug.LogWarning(
            "UnityQemu: SMB share set but Extra Qemu Args has no '-netdev user,...'. " +
            "Appending user netdev + rtl8139.");
        return extra.TrimEnd() +
               $"\n-netdev user,id=net0,smb={full}\n-device rtl8139,netdev=net0\n";
    }
#endif

    static void AppendCdromArgs(
        System.Collections.Generic.IList<string> args,
        CdRomAsset[] sources,
        System.Collections.Generic.HashSet<int> usedIdeIndices)
    {
        // Classic IDE: 0 = -hda. CDs prefer secondary channel (2, 3), then primary slave (1).
        int[] ideIndices = { 2, 3, 1 };
        int added = 0;
        foreach (var source in sources ?? Array.Empty<CdRomAsset>())
        {
            if (source == null)
                continue;
            string path = source.GetIsoFilesystemPath();
            if (string.IsNullOrEmpty(path))
            {
                UnityEngine.Debug.LogWarning(
                    $"CdRomAsset '{source.name}' has no readable ISO path");
                continue;
            }
            if (!File.Exists(path))
            {
                UnityEngine.Debug.LogWarning(
                    $"CdRomAsset '{source.name}' ISO missing at '{path}'");
                continue;
            }
            if (added >= ideIndices.Length)
            {
                UnityEngine.Debug.LogWarning(
                    "UnityQemu: at most 3 IDE CD drives (indices 2, 3, 1) with -hda on 0; " +
                    $"skipping '{source.name}'.");
                break;
            }

            int index = ideIndices[added];
            usedIdeIndices?.Add(index);
            args.Add("-drive");
            args.Add(
                $"id=unityqemu-cd{added},file={path},if=ide,index={index}," +
                "media=cdrom,readonly=on");
            added++;
        }

        // No launch-config CD: reserve an empty tray so PeripheralsUI can hotplug via
        // HMP `change` without a restart.
        if (added == 0)
        {
            usedIdeIndices?.Add(ideIndices[0]);
            args.Add("-drive");
            args.Add($"id={EmptyCdromDriveId},if=ide,index={ideIndices[0]},media=cdrom,readonly=on");
        }
    }

    /// <summary>Block-device id of the empty CD tray reserved when launch config has no ISOs.
    /// (Also the id of the first launch-config CD.)</summary>
    public const string EmptyCdromDriveId = "unityqemu-cd0";

    /// <summary>Block-device id of the empty floppy tray reserved when launch config has no floppies.</summary>
    public const string EmptyFloppyDriveId = "unityqemu-fd0";

    /// <summary>
    /// Fired on the main thread after QEMU has started and VNC/QMP/GDB setup has finished
    /// (clients may still have failed individually — check QmpConnected / GdbConnected / VncConnected).
    /// </summary>
    public event Action OnReady;



    public Texture2D Texture => _vncClient?.Texture;

    public int Width => _vncClient?.Texture?.width ?? -1;
    public int Height => _vncClient?.Texture?.height ?? -1;

    [Button]
    public async void Restart() {
        try
        {
#if UNITY_EDITOR
            // Async state machines from a mid-compile assembly can throw InvalidProgramException.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                UnityEngine.Debug.LogWarning("QEMU Restart ignored — editor is compiling/updating.");
                return;
            }
#endif
            await StopQemuAsync();
            await StartQemuAsync();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e);
        }
    }

    /// <summary>Stop the QEMU process (used by durable snapshot save/load).</summary>
    public Task StopGuestProcessAsync() => StopQemuAsync();

    /// <summary>Start the QEMU process (used by durable snapshot save/load).</summary>
    public Task StartGuestProcessAsync() => StartQemuAsync();

    [Button("Pause guest")]
    [EnableIf(nameof(CanPauseResume))]
    public async void PauseGuest()
    {
        try { await PauseAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Pause guest: {e.Message}"); }
    }

    [Button("Resume guest")]
    [EnableIf(nameof(CanPauseResume))]
    public async void ResumeGuest()
    {
        try { await ResumeAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Resume guest: {e.Message}"); }
    }

    [Button("Reboot guest")]
    [EnableIf(nameof(QmpConnected))]
    [Tooltip(
        "Hard-reset the guest via QMP system_reset (keeps the QEMU process). " +
        "Does not loadvm — boots from the current disk/work image.")]
    public async void RebootGuest()
    {
        try { await RebootAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Reboot guest: {e.Message}"); }
    }

    bool CanPauseResume => QmpConnected || GdbConnected;

    void OnEnable()
    {
        if (ShowLockedSnapshotLaunchConfig)
            SyncLaunchConfigFromDiskMetadata();
#if UNITY_EDITOR
        // Always drive edit-mode ticks while enabled; only skip auto-start during transitions.
        EditorApplication.update -= EditorTick;
        EditorApplication.update += EditorTick;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
#endif
        TryAutoStart();
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        StopQemu();
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        // Defer so we don't start processes during the OnValidate call stack.
        EditorApplication.delayCall -= OnValidateDeferred;
        EditorApplication.delayCall += OnValidateDeferred;
#endif
    }

#if UNITY_EDITOR
    void OnValidateDeferred()
    {
        if (this == null)
            return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        if (!runInEditMode && !Application.isPlaying && IsRunning)
        {
            StopQemu();
            return;
        }

        TryAutoStart();
    }

    void EditorTick()
    {
        if (Application.isPlaying || !runInEditMode || !enabled || !gameObject.activeInHierarchy)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
        Tick();
    }
#endif

    void TryAutoStart()
    {
#if UNITY_EDITOR
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
#endif
        if (!ShouldRun || !enabled || !gameObject.activeInHierarchy)
            return;
        if (IsRunning || _starting)
            return;
        _ = StartQemuAsync();
    }

    async Task StartQemuAsync()
    {
        if (_starting)
            return;
        _starting = true;
        try
        {
        // Ensure a previous instance isn't still holding VNC/QMP/GDB ports.
        if (_qemuProcess != null)
        {
            await StopQemuAsync();
        }

        // Reclaim work images left behind by previous editor sessions (session ids embed
        // instance ids, which change across restarts). Locked files (running QEMU) are skipped.
        CleanupOrphanedWorkOverlays();
        // Use Path.Combine to take advantage of unity's dark magic (somehow redirects to the actual package location in packagecache if needed)
        var qemuExe = ResolveQemuExecutablePath();
        // UnityEngine.Debug.Log($"QEMU executable: {qemuExe}");

        WarnIfSnapshotLaunchMetadataMismatched();

        var process = new Process();
        process.StartInfo.FileName = qemuExe;

        // Memory from effective launch config; extra args via EffectiveExtraQemuArgs so an
        // empty uqsnap extraQemuArgs still falls back to the VM Launch Config (incl. sb16).
        int memoryMb = EffectiveLaunchConfig?.ResolvedMemoryMb ?? LaunchConfig.DefaultMemoryMb;
        string argsToUse = LaunchConfig.StripMemoryArgs(EffectiveExtraQemuArgs);

        argsToUse = InjectSmbShareIntoExtraArgs(argsToUse, EffectiveSmbShareFolder);

        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(memoryMb.ToString());

        foreach (var arg in argsToUse.Split(
                     new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (showGui)
        {
            process.StartInfo.ArgumentList.Add("-display");
            process.StartInfo.ArgumentList.Add("sdl");
        }

        // Every start boots a freshly minted work image (thin overlay or .uqsnap byte-copy).
        // Exception: a boot prepared by the save/load pipeline (PrepareBootFromDisk) — that
        // work file must be used as-is. Discarding the path forces recreation in
        // EnsureWorkOverlayForBoot.
        if (!_workPreparedForNextStart)
            _workOverlayPath = null;
        string hdaPath = ResolveDiskImagePath();
        _workPreparedForNextStart = false;
        if (!string.IsNullOrEmpty(hdaPath))
        {
            process.StartInfo.ArgumentList.Add("-hda");
            process.StartInfo.ArgumentList.Add(hdaPath);
        }

        // IDE index 0 is -hda; CDs and host folders share the remaining units.
        var usedIdeIndices = new System.Collections.Generic.HashSet<int> { 0 };
        AppendCdromArgs(process.StartInfo.ArgumentList, EffectiveCdroms, usedIdeIndices);
        AppendFloppyArgs(process.StartInfo.ArgumentList, EffectiveFloppies);
        AppendHostFolderArgs(process.StartInfo.ArgumentList, EffectiveHostFolders, usedIdeIndices);

        // Add VNC display - :0 means display 0, which is port 5900
        // Format: -display vnc=:0
        process.StartInfo.ArgumentList.Add("-display");
        process.StartInfo.ArgumentList.Add($"vnc=:{vncPort - 5900}");
        
        // Add QMP socket for command control
        // Format: -qmp tcp:host:port,server,nowait
        // -qmp replaces the default HMP monitor, so keep readline HMP on the VC explicitly
        // (Ctrl+Alt+2 in the SDL/GTK window) when we also want interactive monitor access.
        if (enableQmp) {
            process.StartInfo.ArgumentList.Add("-qmp");
            process.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{qmpPort},server,nowait");
            process.StartInfo.ArgumentList.Add("-monitor");
            process.StartInfo.ArgumentList.Add("vc");
        }

        // GDB stub for memory peek/poke (-s is shorthand for tcp::1234)
        if (enableGdb) {
            process.StartInfo.ArgumentList.Add("-gdb");
            process.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{gdbPort},server,nowait");
        }
        
        // Redirect output to see if QEMU has any errors
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        UnityEngine.Debug.Log($"{qemuExe} {string.Join(' ', process.StartInfo.ArgumentList)}");

        process.Start();
        _qemuProcess = process;

        UnityEngine.Debug.Log($"Started QEMU process (PID: {process.Id}) with VNC on port {vncPort}");
        
        // Log QEMU stdout/stderr (null Data = stream closed / async reader sentinel — ignore)
        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                UnityEngine.Debug.Log($"QEMU output: {e.Data}");
        };
        process.ErrorDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                UnityEngine.Debug.LogWarning($"QEMU error: {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait a moment for QEMU to start and QMP socket to be ready
        await Task.Delay(1000);

        // Connect VNC client
        await ConnectVncAsync();
        
        if (enableQmp) {
            // Connect QMP client
            await ConnectQmpAsync();

            string loadTag = _pendingLoadVmTag;
            _pendingLoadVmTag = null;
            if (string.IsNullOrEmpty(loadTag) && HasVmState && autoLoadVmState)
                loadTag = DiskOverlay.DurableSaveVmTag;

            if (!string.IsNullOrEmpty(loadTag))
            {
                await LoadSaveStateAsync(loadTag);
            }
        }

        if (enableGdb) {
            ConnectGdb();
            // gdbstub leaves the VM in DEBUG/stopped on attach; GDB 'c' should resume, but
            // also poke QMP cont so a missed continue doesn't leave the guest frozen.
            if (enableQmp && _qmpClient != null && _qmpClient.IsConnected)
            {
                try { await RunQmpAsync("cont"); }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"QMP cont after GDB attach: {e.Message}"); }
            }
        }

        try { OnReady?.Invoke(); }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e);
        }
        finally
        {
            _starting = false;
        }
    }

    void Start() {
        // Play mode entry — OnEnable may have skipped during the transition.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        EnsureInputProvider();
        TryAutoStart();
    }

    void EnsureInputProvider()
    {
        if (!Application.isPlaying || inputProvider != null)
            return;

        inputProvider = GetComponent<InputProvider>();
        if (inputProvider == null)
            inputProvider = gameObject.AddComponent<BasicInputProvider>();
    }

    void Update()
    {
        // Play mode: normal player loop. Edit mode relies on EditorTick (Update is sparse).
        if (Application.isPlaying)
            Tick();
    }

    void Tick()
    {
        if (_vncClient == null)
            return;

        _vncClient.Update();

        Texture2D src = _vncClient.Texture;
        if (src != null)
        {
            EnsureOutputTexture(src.width, src.height);
            if (outputTexture != null)
            {
                Graphics.Blit(src, outputTexture);
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    // Keep Scene/Game views and RT previews refreshing while QEMU runs in edit mode.
                    EditorApplication.QueuePlayerLoopUpdate();
                    SceneView.RepaintAll();
                }
#endif
            }
        }

        // Unity Input is unreliable outside play mode.
        if (Application.isPlaying)
            inputProvider?.ProcessInput(this);
    }

    void EnsureOutputTexture(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        if (outputTexture == null)
        {
            outputTexture = new RenderTexture(width, height, 0);
            outputTexture.name = "QEMU Output";
            outputTexture.Create();
            return;
        }

        if (outputTexture.width == width && outputTexture.height == height)
        {
            if (!outputTexture.IsCreated())
                outputTexture.Create();
            return;
        }

        outputTexture.Release();
        outputTexture.width = width;
        outputTexture.height = height;
        outputTexture.Create();
    }

    // x and y are pixel coordinates from top-left, in actual display resolution (or does the VNC framebuffer have different resolution?)
    public void SendMouseEvent(int x, int y, bool leftButton, bool middleButton, bool rightButton) {
        if (_vncClient == null || _vncClient.Texture == null) {
            UnityEngine.Debug.LogWarning("VNC client not connected");
            return;
        }
        _vncClient.SendMouseEvent(x, y, leftButton, middleButton, rightButton);
    }

    public void SendKeyEvent(KeyCode key, bool down) {
        int keysym = UnityKeyCodeToVncKeysym(key);
        if (keysym == 0) {
            // Mouse buttons are handled via SendMouseEvent, not VNC keysyms.
            if (key < KeyCode.Mouse0 || key > KeyCode.Mouse6)
                UnityEngine.Debug.LogWarning($"Unknown key: {key}");
            return;
        }

        SendKeyEvent(keysym, down);
    }

    /// <summary>Send a raw VNC/X11 keysym.</summary>
    public void SendKeyEvent(int keysym, bool down) {
        if (_vncClient == null || _vncClient.Texture == null) {
            UnityEngine.Debug.LogWarning("VNC client not connected");
            return;
        }

        _vncClient.SendKeyEvent(keysym, down);
    }

    void OnDestroy()
    {
        // Fire-and-forget sync stop on destroy (can't await here reliably).
        StopQemu();
        // Session is over — reclaim the work image (these can be GBs). StopQemu waits up
        // to 3s for exit; if the file is somehow still locked, the delete silently fails
        // and the orphan sweep at the next start picks it up.
        DiskOverlay.TryDeleteWorkFile(_workOverlayPath);
        _workOverlayPath = null;
    }

    /// <summary>
    /// Delete work images that belong to no VirtualMachine currently loaded (any scene,
    /// active or not). Live VMs' files are kept even while stopped — a prepared boot
    /// (save/load pipeline) must survive until StartQemuAsync consumes it.
    /// </summary>
    static void CleanupOrphanedWorkOverlays()
    {
        try
        {
            var activeSessionIds = new System.Collections.Generic.List<string>();
            foreach (var vm in FindObjectsByType<VirtualMachine>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                activeSessionIds.Add(vm.WorkSessionId);
            }
            DiskOverlay.CleanupOrphanedWorkFiles(activeSessionIds);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"UnityQemu: work-image cleanup failed: {e.Message}");
        }
    }

    Task ConnectVncAsync()
    {
        _vncClient = new VncClient();
        EnsureOutputTexture(640, 480);
        return ConnectVncCoreAsync(_vncClient, vncPort - 5900);
    }

    static async Task ConnectVncCoreAsync(VncClient client, int display)
    {
        try
        {
            await client.ConnectAsync("127.0.0.1", display);
            UnityEngine.Debug.Log("VNC client connected!");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to connect VNC client: {e.Message}");
        }
    }

    Task ConnectQmpAsync()
    {
        _qmpClient = new QmpClient { Verbose = verboseQmp };
        return ConnectQmpCoreAsync(_qmpClient, qmpPort, verboseQmp);
    }

    static async Task ConnectQmpCoreAsync(QmpClient client, int port, bool verbose)
    {
        try
        {
            await client.ConnectAsync("127.0.0.1", port);
            if (verbose)
                UnityEngine.Debug.Log("QMP client connected!");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to connect QMP client: {e.Message}");
        }
    }

    async Task LoadSaveStateAsync(string tag)
    {
        try
        {
            string result = await RunHumanMonitorCommandAsync($"loadvm {tag}");
            UnityEngine.Debug.Log(string.IsNullOrWhiteSpace(result) ? $"loadvm {tag} OK" : $"loadvm {tag}: {result}");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to load save state '{tag}': {e.Message}");
        }
    }

    /// <summary>
    /// Run an HMP command via the connected QMP session (e.g. savevm / loadvm / info snapshots).
    /// </summary>
    public async Task<string> RunHumanMonitorCommandAsync(string commandLine)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
        {
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");
        }
        return await _qmpClient.RunHumanMonitorCommandAsync(commandLine);
    }

    /// <summary>
    /// Removable / CD-like block device names from QMP <c>query-block</c>
    /// (for HMP <c>change</c> / <c>eject</c>).
    /// </summary>
    public Task<string[]> QueryCdromDeviceNamesAsync() =>
        QueryBlockDeviceNamesAsync(
            include: (device, removable) =>
                removable ||
                string.Equals(device, EmptyCdromDriveId, StringComparison.OrdinalIgnoreCase) ||
                device.IndexOf("cd", StringComparison.OrdinalIgnoreCase) >= 0,
            score: device =>
            {
                if (string.Equals(device, EmptyCdromDriveId, StringComparison.OrdinalIgnoreCase))
                    return 0;
                return device.IndexOf("cd", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2;
            });

    /// <summary>
    /// Floppy block device names from QMP <c>query-block</c>
    /// (for HMP <c>change</c> / <c>eject</c>).
    /// </summary>
    public Task<string[]> QueryFloppyDeviceNamesAsync() =>
        QueryBlockDeviceNamesAsync(
            include: (device, removable) =>
                string.Equals(device, EmptyFloppyDriveId, StringComparison.OrdinalIgnoreCase) ||
                device.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                device.StartsWith("fd", StringComparison.OrdinalIgnoreCase),
            score: device =>
            {
                if (string.Equals(device, EmptyFloppyDriveId, StringComparison.OrdinalIgnoreCase))
                    return 0;
                return device.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2;
            });

    /// <summary>
    /// Filtered device names from QMP <c>query-block</c>, best match first.
    /// </summary>
    async Task<string[]> QueryBlockDeviceNamesAsync(
        Func<string, bool, bool> include, Func<string, int> score)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
        {
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");
        }

        JObject response = await _qmpClient.ExecuteCommandAsync("query-block");
        JArray arr = response["return"] as JArray;
        if (arr == null)
            return Array.Empty<string>();

        var names = new System.Collections.Generic.List<string>();
        foreach (JToken entry in arr)
        {
            string device = entry["device"]?.Value<string>();
            if (string.IsNullOrEmpty(device))
                continue;

            bool removable = entry["removable"]?.Value<bool>() ?? false;
            if (!include(device, removable))
                continue;

            if (!names.Contains(device))
                names.Add(device);
        }

        names.Sort((a, b) =>
        {
            int cmp = score(a).CompareTo(score(b));
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        return names.ToArray();
    }

    /// <summary>
    /// Guest RAM size in bytes from QMP <c>query-memory-size-summary</c>
    /// (<c>base-memory</c> + optional <c>plugged-memory</c>).
    /// </summary>
    public async Task<long> GetGuestRamBytesAsync()
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
        {
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");
        }

        JObject response = await _qmpClient.ExecuteCommandAsync("query-memory-size-summary");
        JToken ret = response["return"];
        if (ret == null)
            throw new Exception($"query-memory-size-summary returned no data: {response}");

        long baseMemory = ret["base-memory"]?.Value<long>()
            ?? throw new Exception("query-memory-size-summary missing base-memory");
        long plugged = ret["plugged-memory"]?.Value<long>() ?? 0;
        return baseMemory + plugged;
    }

    /// <summary>Pause the guest (QMP <c>stop</c>, or GDB interrupt if QMP unavailable).</summary>
    public async Task PauseAsync()
    {
        if (QmpConnected)
            await RunQmpAsync("stop");
        else if (_gdbClient != null && _gdbClient.IsConnected)
            _gdbClient.Interrupt();
        else
            throw new InvalidOperationException("Neither QMP nor GDB connected");

        // So GDB memory sessions don't auto-continue over a QMP pause.
        _gdbClient?.NotifyStoppedExternally();
    }

    /// <summary>Resume the guest (QMP <c>cont</c>, or GDB continue if QMP unavailable).</summary>
    public async Task ResumeAsync()
    {
        if (QmpConnected)
            await RunQmpAsync("cont");
        else if (_gdbClient != null && _gdbClient.IsConnected)
            _gdbClient.Continue();
        else
            throw new InvalidOperationException("Neither QMP nor GDB connected");

        _gdbClient?.NotifyRunningExternally();
    }

    /// <summary>
    /// Hard-reset the guest without killing QEMU (QMP <c>system_reset</c>).
    /// Does not <c>loadvm</c> — the guest reboots from the current disk/work image.
    /// </summary>
    public async Task RebootAsync()
    {
        await RunQmpAsync("system_reset");
        _gdbClient?.NotifyRunningExternally();
    }

    async Task RunQmpAsync(string command)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
        {
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");
        }
        await _qmpClient.ExecuteCommandAsync(command);
    }

    async Task StopQemuAsync()
    {
        StopQemu();
        // Windows can take a beat to release listen sockets after Kill.
        await WaitForPortsFreeAsync(2000);
    }

    void StopQemu()
    {
        _vncClient?.Dispose();
        _vncClient = null;
        
        _qmpClient?.Dispose();
        _qmpClient = null;

        _gdbClient?.Dispose();
        _gdbClient = null;
        
        if (_qemuProcess != null)
        {
            try
            {
                if (!_qemuProcess.HasExited)
                {
                    try { _qemuProcess.CancelOutputRead(); } catch { /* ignore */ }
                    try { _qemuProcess.CancelErrorRead(); } catch { /* ignore */ }
                    _qemuProcess.Kill();
                    if (!_qemuProcess.WaitForExit(3000))
                    {
                        UnityEngine.Debug.LogWarning("QEMU process did not exit within 3s after Kill()");
                    }
                    else
                    {
                        UnityEngine.Debug.Log("Stopped QEMU process");
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"Error stopping QEMU: {e.Message}");
            }
            finally
            {
                try { _qemuProcess.Dispose(); } catch { /* ignore */ }
                _qemuProcess = null;
            }
        }
    }

    async Task WaitForPortsFreeAsync(int timeoutMs)
    {
        int[] ports = enableQmp
            ? (enableGdb ? new[] { vncPort, qmpPort, gdbPort } : new[] { vncPort, qmpPort })
            : (enableGdb ? new[] { vncPort, gdbPort } : new[] { vncPort });

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool allFree = true;
            foreach (int port in ports)
            {
                if (!IsPortFree(port))
                {
                    allFree = false;
                    break;
                }
            }
            if (allFree)
                return;
            await Task.Delay(50);
        }
        UnityEngine.Debug.LogWarning(
            $"Timed out waiting for QEMU ports to free (vnc={vncPort}, qmp={qmpPort}, gdb={gdbPort}). " +
            "Restart may fail if an old qemu-system process is still running.");
    }

    static bool IsPortFree(int port)
    {
        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* ignore */ }
        }
    }

    void ConnectGdb()
    {
        try
        {
            _gdbClient?.Dispose();
            _gdbClient = new GdbClient { Verbose = verboseGdb };
            _gdbClient.Connect("127.0.0.1", gdbPort, gdbPhysicalMemory);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to connect GDB client: {e.Message}");
            _gdbClient?.Dispose();
            _gdbClient = null;
        }
    }

    /// <summary>
    /// Read an unsigned integer from guest memory via the gdbstub.
    /// Addresses are physical when gdbPhysicalMemory is enabled (default), otherwise virtual.
    /// </summary>
    public uint ReadUnsigned(long address, int size, bool isBigEndian)
    {
        if (size != 1 && size != 2 && size != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "size must be 1, 2, or 4");
        }
        if (_gdbClient == null || !_gdbClient.IsConnected)
        {
            throw new InvalidOperationException("GDB client not connected");
        }

        byte[] bytes = _gdbClient.ReadMemory(address, size);
        uint value = 0;
        if (isBigEndian)
        {
            for (int i = 0; i < size; i++)
            {
                value = (value << 8) | bytes[i];
            }
        }
        else
        {
            for (int i = size - 1; i >= 0; i--)
            {
                value = (value << 8) | bytes[i];
            }
        }
        return value;
    }

    /// <summary>
    /// Write an unsigned integer to guest memory via the gdbstub.
    /// </summary>
    public void WriteUnsigned(long address, uint value, int size, bool isBigEndian)
    {
        if (size != 1 && size != 2 && size != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "size must be 1, 2, or 4");
        }
        if (_gdbClient == null || !_gdbClient.IsConnected)
        {
            throw new InvalidOperationException("GDB client not connected");
        }

        byte[] bytes = new byte[size];
        if (isBigEndian)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xff);
                value >>= 8;
            }
        }
        else
        {
            for (int i = 0; i < size; i++)
            {
                bytes[i] = (byte)(value & 0xff);
                value >>= 8;
            }
        }
        _gdbClient.WriteMemory(address, bytes);
    }

    /// <summary>
    /// Read a raw byte range from guest memory (batched; preferred over per-byte ReadUnsigned).
    /// </summary>
    public byte[] ReadBytes(long address, int length)
    {
        if (_gdbClient == null || !_gdbClient.IsConnected)
        {
            throw new InvalidOperationException("GDB client not connected");
        }
        return _gdbClient.ReadMemory(address, length);
    }

    /// <summary>
    /// Pause the guest once for multiple memory operations, then resume on dispose.
    /// </summary>
    public GdbMemorySession BeginMemorySession() => new GdbMemorySession(_gdbClient);

    /// <summary>Scoped GDB pause for batched memory reads/writes.</summary>
    public readonly struct GdbMemorySession : IDisposable
    {
        readonly GdbClient _client;

        internal GdbMemorySession(GdbClient client)
        {
            _client = client ?? throw new InvalidOperationException("GDB client not connected");
            _client.BeginMemorySession();
        }

        public void Dispose() => _client?.EndMemorySession();
    }

    /// <summary>
    /// Write a raw byte range to guest memory.
    /// </summary>
    public void WriteBytes(long address, byte[] data)
    {
        if (_gdbClient == null || !_gdbClient.IsConnected)
        {
            throw new InvalidOperationException("GDB client not connected");
        }
        _gdbClient.WriteMemory(address, data);
    }

    
    /// <summary>
    /// Convert a printable character to a VNC/X11 keysym.
    /// Latin-1 chars map 1:1; common Unicode punctuation gets a Latin-1 stand-in where possible.
    /// </summary>
    public static int CharToVncKeysym(char c)
    {
        if (c >= 0x20 && c <= 0xff)
            return c;
        return 0;
    }

    /// <summary>
    /// Convert Unity KeyCode to VNC/X11 keysym for non-printable / keypad / function keys.
    /// Printable punctuation is normally sent via <see cref="CharToVncKeysym"/> from inputString;
    /// KeyCode mappings below remain as a fallback for direct SendKeyEvent calls.
    /// </summary>
    int UnityKeyCodeToVncKeysym(KeyCode key)
    {
        // Letters (A-Z) — unshifted Latin keysyms; guest uses Shift modifier from SpecialKeyCodes.
        if (key >= KeyCode.A && key <= KeyCode.Z)
            return 'a' + (key - KeyCode.A);

        // Top-row digits
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            return '0' + (key - KeyCode.Alpha0);

        switch (key)
        {
            case KeyCode.Space: return 0x0020;
            case KeyCode.Return: case KeyCode.KeypadEnter: return 0xFF0D; // XK_Return
            case KeyCode.Escape: return 0xFF1B;
            case KeyCode.Backspace: return 0xFF08;
            case KeyCode.Tab: return 0xFF09;
            case KeyCode.Delete: return 0xFFFF; // XK_Delete

            case KeyCode.Comma: return 0x002C;      // ,
            case KeyCode.Period: return 0x002E;     // .
            case KeyCode.Slash: return 0x002F;      // /
            case KeyCode.Backslash: return 0x005C;  // \
            case KeyCode.Quote: return 0x0027;      // '
            case KeyCode.Semicolon: return 0x003B;  // ;
            case KeyCode.LeftBracket: return 0x005B;  // [
            case KeyCode.RightBracket: return 0x005D; // ]
            case KeyCode.Minus: return 0x002D;      // -
            case KeyCode.Equals: return 0x003D;     // =
            case KeyCode.BackQuote: return 0x0060;  // `

            // Shifted punctuation KeyCodes Unity sometimes reports directly
            case KeyCode.Colon: return 0x003A;      // :
            case KeyCode.Question: return 0x003F;   // ?
            case KeyCode.DoubleQuote: return 0x0022;
            case KeyCode.Less: return 0x003C;
            case KeyCode.Greater: return 0x003E;
            case KeyCode.Exclaim: return 0x0021;
            case KeyCode.At: return 0x0040;
            case KeyCode.Hash: return 0x0023;
            case KeyCode.Dollar: return 0x0024;
            case KeyCode.Percent: return 0x0025;
            case KeyCode.Caret: return 0x005E;
            case KeyCode.Ampersand: return 0x0026;
            case KeyCode.Asterisk: return 0x002A;
            case KeyCode.LeftParen: return 0x0028;
            case KeyCode.RightParen: return 0x0029;
            case KeyCode.Underscore: return 0x005F;
            case KeyCode.Plus: return 0x002B;
            case KeyCode.LeftCurlyBracket: return 0x007B;
            case KeyCode.RightCurlyBracket: return 0x007D;
            case KeyCode.Pipe: return 0x007C;
            case KeyCode.Tilde: return 0x007E;

            case KeyCode.LeftShift: return 0xFFE1;
            case KeyCode.RightShift: return 0xFFE2;
            case KeyCode.LeftControl: return 0xFFE3;
            case KeyCode.RightControl: return 0xFFE4;
            case KeyCode.LeftAlt: return 0xFFE9;
            case KeyCode.RightAlt: return 0xFFEA;
            case KeyCode.LeftCommand: return 0xFFEB; // Super_L
            case KeyCode.RightCommand: return 0xFFEC; // Super_R
            case KeyCode.CapsLock: return 0xFFE5;
            case KeyCode.Numlock: return 0xFF7F;

            case KeyCode.UpArrow: return 0xFF52;
            case KeyCode.DownArrow: return 0xFF54;
            case KeyCode.LeftArrow: return 0xFF51;
            case KeyCode.RightArrow: return 0xFF53;
            case KeyCode.Insert: return 0xFF63;
            case KeyCode.Home: return 0xFF50;
            case KeyCode.End: return 0xFF57;
            case KeyCode.PageUp: return 0xFF55;
            case KeyCode.PageDown: return 0xFF56;
            case KeyCode.Print: return 0xFF61;
            case KeyCode.ScrollLock: return 0xFF14;
            case KeyCode.Pause: return 0xFF13;

            case KeyCode.F1: return 0xFFBE;
            case KeyCode.F2: return 0xFFBF;
            case KeyCode.F3: return 0xFFC0;
            case KeyCode.F4: return 0xFFC1;
            case KeyCode.F5: return 0xFFC2;
            case KeyCode.F6: return 0xFFC3;
            case KeyCode.F7: return 0xFFC4;
            case KeyCode.F8: return 0xFFC5;
            case KeyCode.F9: return 0xFFC6;
            case KeyCode.F10: return 0xFFC7;
            case KeyCode.F11: return 0xFFC8;
            case KeyCode.F12: return 0xFFC9;

            case KeyCode.Keypad0: return 0xFFB0;
            case KeyCode.Keypad1: return 0xFFB1;
            case KeyCode.Keypad2: return 0xFFB2;
            case KeyCode.Keypad3: return 0xFFB3;
            case KeyCode.Keypad4: return 0xFFB4;
            case KeyCode.Keypad5: return 0xFFB5;
            case KeyCode.Keypad6: return 0xFFB6;
            case KeyCode.Keypad7: return 0xFFB7;
            case KeyCode.Keypad8: return 0xFFB8;
            case KeyCode.Keypad9: return 0xFFB9;
            case KeyCode.KeypadPeriod: return 0xFFAE;
            case KeyCode.KeypadDivide: return 0xFFAF;
            case KeyCode.KeypadMultiply: return 0xFFAA;
            case KeyCode.KeypadMinus: return 0xFFAD;
            case KeyCode.KeypadPlus: return 0xFFAB;

            default: return 0;
        }
    }
}
}