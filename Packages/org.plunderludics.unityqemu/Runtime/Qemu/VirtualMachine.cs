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
using UnityEngine.Serialization;
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

    /// <summary>Active work overlay path when using the boot disk + ephemeral overlay.</summary>
    string _workOverlayPath;

    /// <summary>
    /// Set by <see cref="PrepareBoot"/>: the next start must boot the prepared work
    /// image instead of minting a fresh one. Consumed by StartQemuAsync.
    /// </summary>
    bool _workPreparedForNextStart;

    /// <summary>
    /// If set, the next start launches with <c>-incoming tcp:</c> and this machine-state
    /// stream (<c>.uqsnap</c>) is fed in after QMP connects.
    /// </summary>
    string _pendingIncomingStatePath;

    /// <summary>
    /// Whether <see cref="_pendingIncomingStatePath"/> is gzip-compressed.
    /// Default true matches older snapshots that omit the metadata flag.
    /// </summary>
    bool _pendingIncomingGzip = true;

    /// <summary>Loopback port QEMU listens on for <c>-incoming</c> when a state feed is pending.</summary>
    int _incomingPort;

    /// <summary>
    /// Work layers frozen by D4 saves this session (`blockdev-snapshot-sync` leaves the
    /// old active layer behind as backing). Deleted with the session.
    /// </summary>
    readonly System.Collections.Generic.List<string> _sessionLayerPaths =
        new System.Collections.Generic.List<string>();

    /// <summary>Counter for naming extra work layers created by D4 saves.</summary>
    int _workLayerCounter;

    /// <summary>Block device name of the <c>-hda</c> drive on the pc machine type.</summary>
    public const string HdaBlockDeviceName = "ide0-hd0";

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
    [FormerlySerializedAs("runInEditMode")]
    public bool runVmInEditMode = false;

    [Header("Disk")]
    [Tooltip(
        "Durable snapshot (.uqsnap) to boot on Play / auto-start. " +
        "Not changed by SnapshotUI Load/Save — those update Session Current only.")]
    [OnValueChanged(nameof(OnSnapshotChanged))]
    public UqsnapAsset snapshot;

    [Tooltip(
        "Disk tip (.qcow2) for a cold boot when Snapshot is empty. " +
        "Filled from Snapshot when one is assigned in this config.")]
    [DisableIf(nameof(HasSnapshot))]
    public DiskAsset diskAsset;

    [ShowIf(nameof(HasSnapshot))]
    [Tooltip(
        "When on (default), restore the snapshot's saved machine state on start. " +
        "Turn off to boot only its disk contents.")]
    public bool autoLoadVmState = true;

    [ShowIf(nameof(HasSnapshot))]
    [Tooltip(
        "When off (default), launch with the memory / extra QEMU args / media that were " +
        "saved with the snapshot. Turn on to edit and use the Launch Config below instead.")]
    [OnValueChanged(nameof(OnOverrideSnapshotLaunchConfigChanged))]
    public bool overrideSnapshotLaunchConfig;

    // Nested serializable foldout. Disabled when a uqsnap owns launch config.
    [DisableIf(nameof(ShowLockedSnapshotLaunchConfig))]
    [Tooltip(
        "Extra QEMU args and removable media. " +
        "With a snapshot and Override off, shows the snapshot's config (read-only).")]
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

    /// <summary>
    /// Tip this session is on (loaded/saved, or set from boot config at start).
    /// Not serialized — does not alter <see cref="snapshot"/> / <see cref="diskAsset"/>.
    /// </summary>
    [NonSerialized] BootableAsset _sessionCurrent;

    [ShowInInspector, ReadOnly]
    [LabelText("Session Current")]
    [PropertyTooltip(
        "Live tip for this QEMU session (after Load/Save, or the boot config once started). " +
        "Independent of the Snapshot / Disk slots above.")]
    public BootableAsset sessionCurrent => _sessionCurrent;

    /// <summary>True when boot-config <see cref="snapshot"/> is assigned.</summary>
    public bool HasSnapshot => snapshot != null;

    /// <summary>Disk tip from boot config only (Snapshot.disk or Disk Asset).</summary>
    public DiskAsset ConfiguredDiskAsset =>
        snapshot != null && snapshot.disk != null ? snapshot.disk : diskAsset;

    /// <summary>
    /// Disk tip for -hda / overlays / save parents: session tip when set, else boot config.
    /// </summary>
    public DiskAsset ActiveDiskAsset
    {
        get
        {
            DiskAsset tip = _sessionCurrent != null ? _sessionCurrent.DiskTip : null;
            return tip != null ? tip : ConfiguredDiskAsset;
        }
    }

    bool ShowLockedSnapshotLaunchConfig => HasSnapshot && !overrideSnapshotLaunchConfig;

    /// <summary>
    /// Uqsnap that owns launch config for this session: session current if it is a
    /// snapshot, otherwise the boot-config snapshot. Null when override is on.
    /// </summary>
    UqsnapAsset LaunchConfigOwnerSnap
    {
        get
        {
            if (overrideSnapshotLaunchConfig)
                return null;
            if (_sessionCurrent is UqsnapAsset sessionSnap)
                return sessionSnap;
            return HasSnapshot ? snapshot : null;
        }
    }

    /// <summary>
    /// Launch config stored on the owning uqsnap, when it owns config (no override); else null.
    /// </summary>
    LaunchConfig SnapshotOwnedLaunchConfig =>
        LaunchConfigOwnerSnap != null
            ? LaunchConfigOwnerSnap.GetStoredLaunchConfig()
            : null;

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
        UqsnapAsset ownerSnap = LaunchConfigOwnerSnap;
        if (ownerSnap != null)
        {
            if (ownerSnap.metadata == null)
                ownerSnap.metadata = UqsnapMetadata.CreateEmpty();
            if (ownerSnap.metadata.launchConfig == null)
                ownerSnap.metadata.launchConfig = LaunchConfig.CreateDefault();
            target = ownerSnap.metadata.launchConfig;
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
        if (!ReferenceEquals(launchConfig, target) && ownerSnap != null)
            UnityEditor.EditorUtility.SetDirty(ownerSnap);
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
        if (!HasSnapshot)
            return;
        SyncLaunchConfigFromSnapshotMetadata();
    }

    void SyncLaunchConfigFromSnapshotMetadata()
    {
        if (!HasSnapshot || snapshot == null)
            return;
        if (launchConfig == null)
            launchConfig = LaunchConfig.CreateDefault();
        launchConfig.CopyFrom(
            snapshot.GetStoredLaunchConfig() ?? LaunchConfig.CreateDefault());
    }

    void OnSnapshotChanged()
    {
        SyncDiskFromSnapshot();
        if (HasSnapshot && !overrideSnapshotLaunchConfig)
            SyncLaunchConfigFromSnapshotMetadata();
    }

    /// <summary>Keep <see cref="diskAsset"/> mirrored from <see cref="snapshot"/> when set.</summary>
    void SyncDiskFromSnapshot()
    {
        if (snapshot == null)
            return;
        diskAsset = snapshot.disk;
    }

    bool ShouldRun => Application.isPlaying || runVmInEditMode;
    bool IsRunning => _qemuProcess != null && !_qemuProcess.HasExited;

    /// <summary>
    /// Error text from the most recent boot's incoming state restore, or null when
    /// it succeeded or no restore was requested. The boot itself deliberately survives
    /// a failed restore (cold boot of the disk contents), so tooling can check this.
    /// </summary>
    public string LastStateRestoreError { get; private set; }

    string ResolveDiskImagePath()
    {
        DiskAsset disk = ActiveDiskAsset;
        if (disk == null)
            return null;

        DiskOverlay.EnsureBackingChain(disk);

        if (useEphemeralWorkOverlay)
        {
            string work = EnsureWorkOverlayForBoot();
            if (!string.IsNullOrEmpty(work))
                return work;
        }

        return disk.GetQcow2FilesystemPath();
    }

    /// <summary>Current -hda path (work overlay or configured disk).</summary>
    public string ActiveDiskPath =>
        !string.IsNullOrEmpty(_workOverlayPath) ? _workOverlayPath : ResolveDiskImagePath();

    public string WorkOverlayPath => _workOverlayPath;


    /// <summary>Canonical backing path for a thin work overlay: the boot disk tip.</summary>
    public string ExpectedWorkBackingFilesystemPath =>
        ActiveDiskAsset != null ? ActiveDiskAsset.GetQcow2FilesystemPath() : null;

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
        if (!HasSnapshot || overrideSnapshotLaunchConfig || snapshot == null)
            return;

        UqsnapMetadata meta = snapshot.metadata;
        string snapArgs = meta?.launchConfig != null ? meta.launchConfig.extraQemuArgs : null;
        if (string.IsNullOrWhiteSpace(snapArgs))
        {
            UnityEngine.Debug.LogWarning(
                $"Snapshot '{snapshot.name}' has no stored extra QEMU args — " +
                "using the VirtualMachine Launch Config. Re-save the snapshot to record args.");
            return;
        }

        string currentQemu = QueryBundledQemuVersion();
        if (!string.IsNullOrEmpty(meta.qemuVersion) &&
            !string.IsNullOrEmpty(currentQemu) &&
            !string.Equals(meta.qemuVersion, currentQemu, StringComparison.Ordinal))
        {
            UnityEngine.Debug.LogWarning(
                $"Snapshot '{snapshot.name}' was saved with QEMU '{meta.qemuVersion}', " +
                $"but this project has '{currentQemu}'. Restoring saved state may fail if " +
                "the versions are incompatible.");
        }
    }

    /// <summary>Ensure a thin work overlay exists for the configured boot disk.</summary>
    public string EnsureWorkOverlayForBoot()
    {
        DiskAsset disk = ActiveDiskAsset;
        if (disk == null)
            return null;

        DiskOverlay.EnsureBackingChain(disk);
        string basePath = disk.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(basePath) || !File.Exists(basePath))
        {
            UnityEngine.Debug.LogError(
                $"DiskAsset '{disk.name}' has no readable image at '{basePath}'");
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
    /// session that backs onto a different parent so switching boot tips doesn't keep
    /// showing that child's guest disk.
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
    /// Prepare the work image for booting <paramref name="snap"/> and queue its state
    /// restore for the next start. Updates <see cref="sessionCurrent"/> only — does not
    /// change boot-config <see cref="snapshot"/> / <see cref="diskAsset"/>.
    /// Call while QEMU is stopped.
    /// </summary>
    public void PrepareBoot(UqsnapAsset snap, bool loadVmState = true)
    {
        if (snap == null)
            throw new ArgumentNullException(nameof(snap));
        if (snap.disk == null)
            throw new InvalidOperationException(
                $"Snapshot '{snap.name}' has no linked disk tip to boot");

        SetSessionCurrent(snap);
        PrepareBootDisk(snap.disk, loadVmState && snap.HasMachineState ? snap : null);
    }

    /// <summary>
    /// Prepare a cold boot of a plain disk. Updates <see cref="sessionCurrent"/> only —
    /// does not clear boot-config <see cref="snapshot"/>.
    /// Call while QEMU is stopped.
    /// </summary>
    public void PrepareBoot(DiskAsset disk)
    {
        if (disk == null)
            throw new ArgumentNullException(nameof(disk));
        SetSessionCurrent(disk);
        PrepareBootDisk(disk, state: null);
    }

    void PrepareBootDisk(DiskAsset disk, UqsnapAsset state)
    {
        string imagePath = disk.GetQcow2FilesystemPath();
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Disk image not found", imagePath);

        DiskOverlay.EnsureBackingChain(disk);
        ResetSessionLayers();

        _workOverlayPath = DiskOverlay.CreateWorkOverlay(imagePath, WorkSessionId);
        if (state != null)
        {
            _pendingIncomingStatePath = state.GetMachineStateFilesystemPath();
            _pendingIncomingGzip = state.MachineStateIsCompressed;
        }
        else
        {
            _pendingIncomingStatePath = null;
            _pendingIncomingGzip = true;
        }

        _workPreparedForNextStart = true;
        UnityEngine.Debug.Log(
            $"Prepared boot from '{(state != null ? state.name : disk.name)}' → {_workOverlayPath}" +
            (_pendingIncomingStatePath != null ? $" (incoming {_pendingIncomingStatePath})" : ""));
    }

    /// <summary>
    /// Point session current at <paramref name="asset"/> without changing boot config
    /// or rebuilding the work overlay (e.g. after a durable save while QEMU keeps running).
    /// </summary>
    public void SetSessionCurrent(BootableAsset asset)
    {
        _sessionCurrent = asset;
    }

    /// <summary>
    /// Delete extra work layers frozen by D4 saves and reset the layer counter.
    /// Call only while QEMU is stopped (or on a fresh start after StopQemuAsync).
    /// </summary>
    void ResetSessionLayers()
    {
        foreach (string layer in _sessionLayerPaths)
            DiskOverlay.TryDeleteWorkFile(layer);
        _sessionLayerPaths.Clear();
        _workLayerCounter = 0;
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

    /// <summary>
    /// Fired each tick after the guest framebuffer <see cref="Texture"/> is updated
    /// (and the optional output RenderTexture blit), and before input is processed.
    /// Subscribe to present or post-process the frame (e.g. chroma blit) so results are
    /// ready for the same-frame input / hit-test path. Does not fire when no texture exists yet.
    /// </summary>
    public event Action OnTextureUpdated;

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
        "Hard-reset the guest (keeps the emulator process). " +
        "Does not restore a saved snapshot — boots from the current disk.")]
    public async void RebootGuest()
    {
        try { await RebootAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Reboot guest: {e.Message}"); }
    }

    bool CanPauseResume => QmpConnected || GdbConnected;

    void OnEnable()
    {
        if (ShowLockedSnapshotLaunchConfig)
            SyncLaunchConfigFromSnapshotMetadata();
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
        SyncDiskFromSnapshot();
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

        if (!runVmInEditMode && !Application.isPlaying && IsRunning)
        {
            StopQemu();
            return;
        }

        TryAutoStart();
    }

    void EditorTick()
    {
        if (Application.isPlaying || !runVmInEditMode || !enabled || !gameObject.activeInHierarchy)
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
        LastStateRestoreError = null;
        try
        {
        // When a D4 state restore kills QEMU (e.g. overridden RAM), retry once without
        // -incoming so the guest still boots with the snapshot's disk contents.
        bool coldBootAfterFailedState = false;
        for (int attempt = 0; ; attempt++)
        {
        // Ensure a previous instance isn't still holding VNC/QMP/GDB ports.
        if (_qemuProcess != null)
        {
            await StopQemuAsync();
        }

        // Reclaim work images left behind by previous editor sessions (session ids embed
        // instance ids, which change across restarts). Locked files (running QEMU) are skipped.
        // Skip on cold-boot retry — the thin work overlay from the failed attempt is still good.
        if (!coldBootAfterFailedState)
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

        // Every start boots a freshly minted thin work overlay.
        // Exception: a boot prepared by the save/load pipeline (PrepareBoot) — that
        // work file must be used as-is. Discarding the path forces recreation in
        // EnsureWorkOverlayForBoot. Cold-boot retry also keeps the prepared work image.
        bool preparedBoot = _workPreparedForNextStart || coldBootAfterFailedState;
        if (!preparedBoot)
        {
            _workOverlayPath = null;
            _pendingIncomingStatePath = null;
            _pendingIncomingGzip = true;
            ResetSessionLayers();
            // Session follows boot config on a normal start.
            _sessionCurrent = snapshot != null ? (BootableAsset)snapshot : diskAsset;
        }
        else if (coldBootAfterFailedState)
        {
            _pendingIncomingStatePath = null;
            _pendingIncomingGzip = true;
        }
        string hdaPath = ResolveDiskImagePath();
        _workPreparedForNextStart = false;
        // Fresh start of a snapshot: state comes from the .uqsnap migration stream via -incoming.
        if (!preparedBoot && string.IsNullOrEmpty(_pendingIncomingStatePath) &&
            HasSnapshot && autoLoadVmState && snapshot != null && snapshot.HasMachineState)
        {
            _pendingIncomingStatePath = snapshot.GetMachineStateFilesystemPath();
            _pendingIncomingGzip = snapshot.MachineStateIsCompressed;
        }
        if (!string.IsNullOrEmpty(hdaPath))
        {
            process.StartInfo.ArgumentList.Add("-hda");
            process.StartInfo.ArgumentList.Add(hdaPath);
        }

        _incomingPort = 0;
        if (!string.IsNullOrEmpty(_pendingIncomingStatePath))
        {
            if (!enableQmp)
            {
                // The state feed needs QMP (completion polling, quick-save, cont);
                // launching -incoming without it would wait forever.
                UnityEngine.Debug.LogWarning(
                    "UnityQemu: snapshot state restore requires QMP (enableQmp) — booting cold.");
                _pendingIncomingStatePath = null;
            }
            else if (File.Exists(_pendingIncomingStatePath))
            {
                _incomingPort = MigrationRelay.GetFreePort();
                process.StartInfo.ArgumentList.Add("-incoming");
                process.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{_incomingPort}");
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    $"UnityQemu: machine-state file missing at '{_pendingIncomingStatePath}' — booting cold.");
                _pendingIncomingStatePath = null;
            }
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

            string incomingStatePath = _pendingIncomingStatePath;
            bool incomingGzip = _pendingIncomingGzip;
            _pendingIncomingStatePath = null;
            _pendingIncomingGzip = true;
            if (!string.IsNullOrEmpty(incomingStatePath))
            {
                if (!IsRunning && attempt == 0)
                {
                    LastStateRestoreError =
                        "emulator exited before state could be restored";
                    UnityEngine.Debug.LogWarning(
                        "Unable to load machine state from snapshot — starting with disk contents from boot.");
                    coldBootAfterFailedState = true;
                    continue;
                }

                // Stream the .uqsnap migration file into -incoming, quick-save, resume.
                await FeedIncomingStateAsync(incomingStatePath, incomingGzip);
                if (!string.IsNullOrEmpty(LastStateRestoreError) && attempt == 0)
                {
                    UnityEngine.Debug.LogWarning(
                        "Unable to load machine state from snapshot — starting with disk contents from boot.");
                    coldBootAfterFailedState = true;
                    continue;
                }
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
        break;
        } // cold-boot retry loop
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

            // Present / post-process before input so same-frame hit tests see this tick's frame.
            OnTextureUpdated?.Invoke();
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
        // Session is over — reclaim the work images (these can be GBs). StopQemu waits up
        // to 3s for exit; if a file is somehow still locked, the delete silently fails
        // and the orphan sweep at the next start picks it up.
        DiskOverlay.TryDeleteWorkFile(_workOverlayPath);
        _workOverlayPath = null;
        ResetSessionLayers();
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
            if (string.IsNullOrWhiteSpace(result))
            {
                UnityEngine.Debug.Log($"loadvm {tag} OK");
            }
            else
            {
                // HMP reports loadvm failures as output text, not an error response.
                LastStateRestoreError = result.Trim();
                UnityEngine.Debug.LogWarning($"Unable to load vm state from snapshot (loadvm {tag}): {result}");
            }
        }
        catch (Exception e)
        {
            LastStateRestoreError = e.Message;
            UnityEngine.Debug.LogWarning($"Unable to load vm state from snapshot '{tag}': {e.Message}");
        }
    }

    /// <summary>
    /// D4 restore: stream a .uqsnap machine-state file into a QEMU launched with
    /// <c>-incoming tcp:</c>, wait for the load, quick-save into the fresh work overlay
    /// (so Reload with no prior quick-save rewinds to the just-loaded state), resume.
    /// </summary>
    async Task FeedIncomingStateAsync(string vmstatePath, bool gzip)
    {
        try
        {
            UnityEngine.Debug.Log(
                $"Restoring machine state from '{vmstatePath}' " +
                $"({(gzip ? "gzip" : "raw")}, port {_incomingPort})…");
            await MigrationRelay.SendFromFileAsync(_incomingPort, vmstatePath, gzip);
            await WaitForRunStateToLeaveAsync("inmigrate", TimeSpan.FromSeconds(60));

            // Resume first: while paused after an incoming migration the block
            // devices are still inactive, so savevm would be refused.
            await RunQmpAsync("cont");

            string quickSave = await RunHumanMonitorCommandAsync(
                $"savevm {DiskOverlay.DurableSaveVmTag}");
            if (!string.IsNullOrWhiteSpace(quickSave))
                UnityEngine.Debug.LogWarning($"Post-restore quick-save: {quickSave.Trim()}");

            UnityEngine.Debug.Log("machine state restored");
        }
        catch (Exception e)
        {
            LastStateRestoreError = e.Message;
            bool qemuDied = _qemuProcess == null || _qemuProcess.HasExited;
            UnityEngine.Debug.LogWarning(
                $"Unable to load vm state from snapshot '{vmstatePath}': {e.Message}" +
                (qemuDied
                    ? " (emulator exited — often a launch-config or QEMU-version mismatch)"
                    : ""));
        }
    }

    /// <summary>Poll QMP <c>query-status</c> until the runstate is no longer <paramref name="state"/>.</summary>
    async Task WaitForRunStateToLeaveAsync(string state, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            JObject response = await _qmpClient.ExecuteCommandAsync("query-status");
            string status = response["return"]?["status"]?.Value<string>();
            if (!string.Equals(status, state, StringComparison.Ordinal))
                return;
            if (sw.Elapsed > timeout)
                throw new TimeoutException(
                    $"Guest still in runstate '{state}' after {timeout.TotalSeconds:F0}s");
            await Task.Delay(200);
        }
    }

    /// <summary>Poll QMP <c>query-migrate</c> until the outgoing migration completes.</summary>
    async Task WaitForMigrationCompletionAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            JObject response = await _qmpClient.ExecuteCommandAsync("query-migrate");
            string status = response["return"]?["status"]?.Value<string>();
            if (string.Equals(status, "completed", StringComparison.Ordinal))
                return;
            if (string.Equals(status, "failed", StringComparison.Ordinal) ||
                string.Equals(status, "cancelled", StringComparison.Ordinal))
            {
                string desc = response["return"]?["error-desc"]?.Value<string>() ?? "(no error-desc)";
                throw new Exception($"Migration {status}: {desc}");
            }
            if (sw.Elapsed > timeout)
                throw new TimeoutException(
                    $"Outgoing migration not completed after {timeout.TotalSeconds:F0}s (status: {status})");
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// D4 durable save, QEMU side: pause → freeze the current work layer as the session's
    /// disk delta (<c>blockdev-snapshot-sync</c>; a fresh layer becomes active) →
    /// quick-save into the new layer (must precede migrate: the postmigrate runstate
    /// refuses savevm) → migrate RAM/CPU out into <paramref name="vmstateOutputPath"/>
    /// → resume. When <paramref name="gzip"/> is true, the file is gzip-compressed.
    /// The migration runs over a pre-connected duplicated socket (<c>migrate fd:</c>);
    /// see <see cref="MigrationRelay"/> for why <c>tcp:</c> can hang.
    /// Returns the frozen layer path for the offline disk-diff step. QEMU keeps running.
    /// </summary>
    public async Task<string> CaptureStateAsync(string vmstateOutputPath, bool gzip = true)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
            throw new InvalidOperationException("QMP not connected");
        if (string.IsNullOrEmpty(_workOverlayPath) || !File.Exists(_workOverlayPath))
            throw new InvalidOperationException("No work image — boot with a Disk Asset first");
        if (string.IsNullOrWhiteSpace(vmstateOutputPath))
            throw new ArgumentException("vmstate output path required", nameof(vmstateOutputPath));

        string frozenLayer = _workOverlayPath;
        string newLayer = DiskOverlay.WorkLayerPathForSession(WorkSessionId, ++_workLayerCounter);

        await PauseAsync();
        try
        {
            // Freeze the disk delta; guest writes continue in a fresh thin layer.
            // Absolute path: QEMU records the backing reference verbatim and qcow2
            // resolves relative paths against the overlay's own directory.
            var snapshotArgs = new JObject
            {
                ["device"] = HdaBlockDeviceName,
                ["snapshot-file"] = Path.GetFullPath(newLayer).Replace('\\', '/'),
                ["format"] = "qcow2",
            };
            await _qmpClient.ExecuteCommandAsync(
                "blockdev-snapshot-sync", snapshotArgs.ToString(Newtonsoft.Json.Formatting.None));
            _sessionLayerPaths.Add(frozenLayer);
            _workOverlayPath = newLayer;

            string quickSave = await RunHumanMonitorCommandAsync(
                $"savevm {DiskOverlay.DurableSaveVmTag}");
            if (!string.IsNullOrWhiteSpace(quickSave))
                UnityEngine.Debug.LogWarning($"Quick-save during capture: {quickSave.Trim()}");

            using (var capture = MigrationRelay.OutgoingCapture.Create(_qemuProcess.Id))
            {
                var fdArgs = new JObject
                {
                    ["info"] = capture.ProtocolInfoBase64,
                    ["fdname"] = capture.FdName,
                };
                await _qmpClient.ExecuteCommandAsync(
                    "get-win32-socket", fdArgs.ToString(Newtonsoft.Json.Formatting.None));
                capture.CloseQemuEnd();

                Task<long> receiveTask = capture.ReceiveToFileAsync(vmstateOutputPath, gzip);
                try
                {
                    await _qmpClient.ExecuteCommandAsync(
                        "migrate", $"{{\"uri\":\"fd:{capture.FdName}\"}}");
                    await WaitForMigrationCompletionAsync(TimeSpan.FromMinutes(2));
                }
                catch
                {
                    // Disposing the capture below aborts the reader; observe its
                    // fault so Unity doesn't log an unobserved task exception.
                    _ = receiveTask.ContinueWith(t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                    throw;
                }

                // Ask QEMU to drop any lingering reference to the duplicated socket,
                // then let the reader finish on EOF or silence.
                try
                {
                    await _qmpClient.ExecuteCommandAsync(
                        "closefd", $"{{\"fdname\":\"{capture.FdName}\"}}");
                }
                catch { /* already released by migrate */ }
                capture.FinishAfterDrain();
                long bytes = await receiveTask;
                UnityEngine.Debug.Log(
                    $"machine state captured: {vmstateOutputPath} " +
                    $"({bytes / (1024.0 * 1024.0):F1} MB{(gzip ? " gzip" : " raw")})");
            }
        }
        finally
        {
            // cont is a valid transition out of postmigrate; also resumes after errors.
            try { await ResumeAsync(); }
            catch (Exception resumeError)
            {
                UnityEngine.Debug.LogWarning(
                    $"Failed to resume after state capture: {resumeError.Message}");
            }
        }

        return frozenLayer;
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