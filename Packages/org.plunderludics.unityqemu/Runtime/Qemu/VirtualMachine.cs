using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

using TriInspector;

namespace UnityQemu {
[ExecuteAlways]
[DeclareFoldoutGroup("Advanced", Expanded = false)]
[DeclareFoldoutGroup("Status", Expanded = false)]
[DeclareHorizontalGroup("guest/restart")]
[DeclareHorizontalGroup("guest/pause")]
public partial class VirtualMachine : MonoBehaviour
{
    Process _qemuProcess;
    VncClient _vncClient;
    QmpClient _qmpClient;
    GdbClient _gdbClient;
    bool _starting;
    /// <summary>
    /// Bumped by <see cref="AbortInFlightStart"/> so a hung/abandoned
    /// <see cref="StartQemuAsync"/> stops mutating state after awaits.
    /// </summary>
    int _startEpoch;
    bool _autoRestartInFlight;
    float _lastAutoRestartRealtime = float.NegativeInfinity;
    const float AutoRestartCooldownSeconds = 2f;

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

    [Tooltip("Also open QEMU's native SDL window while running in the Unity editor.")]
    [FormerlySerializedAs("showGui")]
    public bool showGuiInEditor = false;

    [Tooltip(
        "When on, if the QEMU process exits unexpectedly (crash, Task Manager kill, OS " +
        "reclaim, etc.), automatically start it again while this VM should still be running. " +
        "Off by default. Intentional stops (disable, destroy, save/load pipeline) do not restart.")]
    public bool autoRestart = false;

    [Header("Audio")]
    [Tooltip(
        "When on, capture guest audio over VNC (QEMU Audio RFB) and play it in Unity. " +
        "Off by default. If Extra Qemu Args still use -audiodev sdl/dsound, mute or change " +
        "that backend yourself to avoid double playback with the host mixer.")]
    public bool playAudioInUnity = false;

    [Header("Input")]
    [Tooltip("If null, uses an attached InputProvider or adds a BasicInputProvider in Play mode.")]
    public InputProvider inputProvider;

    [Tooltip("Run QEMU and stream the VNC texture while the editor is not in Play mode")]
    [FormerlySerializedAs("runInEditMode")]
    public bool runVmInEditMode = false;

    /// <summary>
    /// Last seen <see cref="runVmInEditMode"/> for detecting inspector toggles.
    /// Explicit <see cref="StartGuestProcessAsync"/> in edit mode (snapshots, tests) must
    /// not be killed by unrelated OnValidate traffic.
    /// </summary>
    bool _runVmInEditModeCached;

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
        "Memory, USB EHCI, freeform QEMU args, and removable media (CD / floppy images). " +
        "With a snapshot and Override off, shows the snapshot's config (read-only).")]
    public LaunchConfig launchConfig = LaunchConfig.CreateDefault();

    [Header("Rendering")]
    [Tooltip(
        "Use an assigned RenderTexture instead of auto-creating one sized to the guest framebuffer.")]
    [LabelText("Use Custom Render Texture")]
    public bool useCustomRenderTexture = false;

    [ShowIf(nameof(useCustomRenderTexture))]
    [Tooltip("RenderTexture to blit the guest framebuffer into.")]
    [SerializeField] RenderTexture outputTexture;

    [HideIf(nameof(useCustomRenderTexture))]
    [Tooltip("Filter / auto-resize for the auto-created output RenderTexture.")]
    [LabelText("Render Texture Settings")]
    [SerializeField] RenderTextureSettings renderTextureSettings = new RenderTextureSettings();

    /// <summary>Runtime auto RT when <see cref="useCustomRenderTexture"/> is off.</summary>
    RenderTexture _autoOutputTexture;

    /// <summary>Effective output RenderTexture (custom slot or auto-created).</summary>
    public RenderTexture OutputTexture =>
        useCustomRenderTexture ? outputTexture : _autoOutputTexture;

    /// <summary>
    /// Tip this session is on (loaded/saved, or set from boot config at start).
    /// Not serialized — does not alter <see cref="snapshot"/> / <see cref="diskAsset"/>.
    /// </summary>
    [NonSerialized] BootableAsset _sessionCurrent;

    // --- Advanced (collapsed) -------------------------------------------------

    [Group("Advanced")]
    [Tooltip(
        "Also open QEMU's native SDL window in player builds. Off by default — " +
        "builds normally use the Unity VNC texture only.")]
    [SerializeField] bool showGuiInBuild = false;

    [Group("Advanced")]
    [Tooltip(
        "Keep the assigned disk immutable by writing into a Library/ work overlay. " +
        "Leave on unless you intentionally want QEMU to write the Disk Asset file.")]
    public bool useEphemeralWorkOverlay = true;

    [Group("Advanced")]
    [Tooltip(
        "Off (default): pick free VNC/QMP/GDB ports on each start (VNC prefers a " +
        "display hashed from the project path so separate Unity projects rarely collide). " +
        "On: use the fixed ports below (rare — external clients, scripts).")]
    public bool overridePorts = false;

    [Group("Advanced")]
    [ShowIf(nameof(overridePorts))]
    [SerializeField] int vncPort = 5900;

    [Group("Advanced")]
    [ShowIf(nameof(overridePorts))]
    [SerializeField] int qmpPort = 4444;

    [Group("Advanced")]
    [ShowIf(nameof(overridePorts))]
    [SerializeField] int gdbPort = 1234;

    [Group("Advanced")]
    [Tooltip("QMP is required for snapshots, pause/reboot, and media hotplug. Leave on.")]
    public bool enableQmp = true;

    [Group("Advanced")]
    [Tooltip(
        "GDB stub for guest memory peek/poke (WinXpRamSearch, MemViewer). " +
        "Idle overhead is small; each memory op briefly stops the vCPU.")]
    public bool enableGdb = true;

    [Group("Advanced")]
    [Tooltip("Use physical addresses for GDB memory ops (needed for WinXpRamSearch).")]
    [LabelText("GDB Physical Memory")]
    [SerializeField] bool gdbPhysicalMemory = true;

    [Group("Advanced")]
    [Tooltip(
        "Log full QMP connect/command JSON and routine events (STOP/RESUME/DEVICE_DELETED…). " +
        "QMP errors and unusual events are always logged.")]
    public bool verboseQmp = false;

    [Group("Advanced")]
    [Tooltip("Log GDB attach/interrupt/packet chatter")]
    public bool verboseGdb = false;

    [Group("Advanced")]
    [Tooltip(
        "OS priority for the qemu-system process after spawn. " +
        "AboveNormal/High can help when Unity Play Mode is CPU-heavy (e.g. fluid sim). " +
        "RealTime usually needs elevation and can starve the host — avoid unless experimenting.")]
    [OnValueChanged(nameof(OnProcessPriorityChanged))]
    [SerializeField] ProcessPriorityClass processPriority = ProcessPriorityClass.Normal;

    // --- Status (collapsed) ---------------------------------------------------

    [Group("Status")]
    [ShowInInspector] bool VncConnected => _vncClient != null && _vncClient.IsConnected;
    [Group("Status")]
    [ShowInInspector] bool VncInternalClientConnected => _vncClient != null && _vncClient.IsInternalClientConnected;
    [Group("Status")]
    [ShowInInspector] float VncNotifyFps => _vncClient?.NotifyFps ?? 0f;
    [Group("Status")]
    [ShowInInspector] float VncApplyFps => _vncClient?.ApplyFps ?? 0f;
    [Group("Status")]
    [ShowInInspector] public bool GdbConnected => _gdbClient != null && _gdbClient.IsConnected;
    [Group("Status")]
    [ShowInInspector] public bool GdbStopped => _gdbClient != null && _gdbClient.IsStopped;
    [Group("Status")]
    [ShowInInspector] public bool QmpConnected => _qmpClient != null && _qmpClient.IsConnected;

    /// <summary>Ports used by the current (or last) QEMU process. 0 = not allocated.</summary>
    int _activeVncPort;
    int _activeQmpPort;
    int _activeGdbPort;
    QemuPortAllocator.HeldPort _heldVncPort;
    QemuPortAllocator.HeldPort _heldQmpPort;
    QemuPortAllocator.HeldPort _heldGdbPort;
    const int MaxPortBindRetries = 3;

    [Group("Status")]
    [ShowInInspector, ReadOnly]
    [LabelText("Active VNC Port")]
    int ActiveVncPort => _activeVncPort;

    [Group("Status")]
    [ShowInInspector, ReadOnly]
    [LabelText("Active QMP Port")]
    int ActiveQmpPort => _activeQmpPort;

    [Group("Status")]
    [ShowInInspector, ReadOnly]
    [LabelText("Active GDB Port")]
    int ActiveGdbPort => _activeGdbPort;

    [Group("Status")]
    [ShowInInspector, ReadOnly]
    [LabelText("Session Current")]
    [PropertyTooltip(
        "Live tip for this QEMU session (after Load/Save, or the boot config once started). " +
        "Independent of the Snapshot / Disk slots above.")]
    public BootableAsset sessionCurrent => _sessionCurrent;

    [Group("Status")]
    [HideIf(nameof(useCustomRenderTexture))]
    [ShowInInspector, ReadOnly]
    [LabelText("Output Texture")]
    RenderTexture AutoOutputTexture => _autoOutputTexture;

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
    /// Launch config used for CD/floppy when a uqsnap owns config; otherwise local.
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
    public FloppyAsset[] EffectiveFloppies => EffectiveLaunchConfig?.floppies;

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

    public bool RemoveCdRomFromEffectiveLaunchConfig(CdRomAsset asset) =>
        asset != null && AddToEffectiveLaunchConfig(cfg => cfg.RemoveCdRom(asset));

    public bool AddFloppyToEffectiveLaunchConfig(FloppyAsset asset) =>
        asset != null && AddToEffectiveLaunchConfig(cfg => cfg.AddFloppy(asset));

    public bool RemoveFloppyFromEffectiveLaunchConfig(FloppyAsset asset) =>
        asset != null && AddToEffectiveLaunchConfig(cfg => cfg.RemoveFloppy(asset));

    /// <summary>
    /// Record a dedicated EHCI controller on EffectiveLaunchConfig so the next durable
    /// save restores with the same PCI USB host that may have been hotplugged for vvfat.
    /// </summary>
    public bool RecordUsbEhciInEffectiveLaunchConfig(
        string id = null, string pciAddr = null, bool enable = true) =>
        AddToEffectiveLaunchConfig(cfg => cfg.RecordUsbEhci(id, pciAddr, enable));

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
#if UNITY_EDITOR
        RefreshIdleOutputPreview();
#endif
    }

    void OnProcessPriorityChanged()
    {
        if (IsRunning)
            ApplyProcessPriority(_qemuProcess);
    }

    void ApplyProcessPriority(Process process)
    {
        if (process == null)
            return;
        try
        {
            if (process.HasExited)
                return;
            process.PriorityClass = processPriority;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning(
                $"Failed to set QEMU process priority to {processPriority}: {e.Message}");
        }
    }

    /// <summary>Whether to open QEMU's native SDL window for this environment.</summary>
    bool ShowGui =>
#if UNITY_EDITOR
        showGuiInEditor;
#else
        showGuiInBuild;
#endif

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
    public static string ResolveQemuExecutablePath() => Paths.QemuSystemI386Path;

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
                WorkingDirectory = Paths.QemuDir,
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
#if UNITY_EDITOR
        RefreshIdleOutputPreview();
#endif
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
    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, FloppyAsset[] sources)
    {
        int index = 0;
        if (sources != null)
        {
            foreach (var source in sources)
            {
                if (source == null)
                    continue;
                string path = source.GetImgFilesystemPath();
                if (string.IsNullOrEmpty(path))
                {
                    UnityEngine.Debug.LogWarning(
                        $"FloppyAsset '{source.name}' has no readable image path");
                    continue;
                }
                if (!File.Exists(path))
                {
                    UnityEngine.Debug.LogWarning(
                        $"FloppyAsset '{source.name}' image missing at '{path}'");
                    continue;
                }
                // Must use if=floppy (A:/B:), not if=ide — IDE index 0 is already -hda.
                // readonly so savevm/loadvm are not blocked by raw.
                args.Add("-drive");
                args.Add($"file={path},if=floppy,index={index},format=raw,readonly=on");
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
#else
    // Player builds: FloppyAsset resolves via Paths like CdRomAsset.
    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, FloppyAsset[] sources)
    {
        int index = 0;
        if (sources != null)
        {
            foreach (var source in sources)
            {
                if (source == null)
                    continue;
                string path = source.GetImgFilesystemPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    UnityEngine.Debug.LogWarning(
                        $"FloppyAsset '{source.name}' image missing at '{path}'");
                    continue;
                }
                args.Add("-drive");
                args.Add($"file={path},if=floppy,index={index},format=raw,readonly=on");
                index++;
            }
        }

        if (index == 0)
        {
            args.Add("-drive");
            args.Add(
                $"id={EmptyFloppyDriveId},if=floppy,index=0,format=raw,readonly=on," +
                "file.driver=null-co,file.size=1474560");
        }
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
    /// Fired on the main thread after the QEMU process and QMP/VNC/GDB clients are torn down.
    /// </summary>
    public event Action OnStopped;

    /// <summary>
    /// Fired each tick after the guest framebuffer <see cref="Texture"/> is updated
    /// (and the optional output RenderTexture blit), and before input is processed.
    /// Subscribe to present or post-process the frame (e.g. chroma blit) so results are
    /// ready for the same-frame input / hit-test path. Does not fire when no texture exists yet.
    /// </summary>
    public event Action OnTextureUpdated;

    public Texture2D Texture
    {
        get
        {
            if (_vncClient?.Texture != null)
                return _vncClient.Texture;
#if UNITY_EDITOR
            // Edit-mode idle: expose the uqsnap screenshot so consumers (Blitter, etc.)
            // see the same preview we blit into OutputTexture.
            if (!Application.isPlaying && !IsRunning)
                return IdlePreviewScreenshot;
#endif
            return null;
        }
    }

    public int Width => Texture != null ? Texture.width : -1;
    public int Height => Texture != null ? Texture.height : -1;

#if UNITY_EDITOR
    /// <summary>
    /// Session tip when it is a uqsnap, else the boot <see cref="snapshot"/>.
    /// Used for edit-mode idle preview on <see cref="OutputTexture"/>.
    /// </summary>
    Texture2D IdlePreviewScreenshot
    {
        get
        {
            var snap = (_sessionCurrent as UqsnapAsset) ?? snapshot;
            return snap != null ? snap.screenshot : null;
        }
    }

    /// <summary>
    /// When QEMU is not running in the editor, blit the assigned snapshot's screenshot
    /// into <see cref="OutputTexture"/> so Scene/Game views show the saved frame.
    /// </summary>
    void RefreshIdleOutputPreview()
    {
        // Scene save / asset reimport can drop the RT contents a few ticks later, so keep
        // repainting views for a moment rather than trusting a single blit to stick.
        _idlePreviewRepaintUntil = EditorApplication.timeSinceStartup + IdlePreviewRepaintWindow;
        BlitIdleOutputPreview(repaintViews: true);
    }

    /// <summary>
    /// The output RenderTexture loses its contents whenever the editor releases it
    /// (scene save, asset reimport, domain reload, graphics device reset), and there is no
    /// notification for that — so re-blit the idle frame periodically while QEMU is down.
    /// </summary>
    void MaintainIdleOutputPreview()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now < _nextIdlePreviewBlit)
            return;
        _nextIdlePreviewBlit = now + IdlePreviewBlitInterval;

        BlitIdleOutputPreview(repaintViews: now < _idlePreviewRepaintUntil);
    }

    void BlitIdleOutputPreview(bool repaintViews)
    {
        if (Application.isPlaying || IsRunning || _starting)
            return;

        Texture2D src = IdlePreviewScreenshot;
        if (src == null)
            return;

        EnsureOutputTexture(src.width, src.height);
        RenderTexture dest = OutputTexture;
        if (dest == null)
            return;

        Graphics.Blit(src, dest);
        OnTextureUpdated?.Invoke();
        if (!repaintViews)
            return;

        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    /// <summary>Throttle for the idle re-blit driven by <see cref="EditorTick"/>.</summary>
    const double IdlePreviewBlitInterval = 0.2;

    /// <summary>How long to keep requesting view repaints after an explicit refresh.</summary>
    const double IdlePreviewRepaintWindow = 1.5;

    double _nextIdlePreviewBlit;
    double _idlePreviewRepaintUntil;
#endif

    [PropertyOrder(1000)]
    [Group("guest/restart")]
    [Button("Restart QEMU")]
    public async void RestartQemu() {
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
            // StopQemu aborts any in-flight StartQemuAsync (epoch + _starting) so a
            // hung start can't leave Restart as a silent no-op.
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

    [PropertyOrder(1001)]
    [Group("guest/restart")]
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

    [PropertyOrder(1010)]
    [Group("guest/pause")]
    [Button("Pause guest")]
    [EnableIf(nameof(CanPauseResume))]
    public async void PauseGuest()
    {
        try { await PauseAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Pause guest: {e.Message}"); }
    }

    [PropertyOrder(1011)]
    [Group("guest/pause")]
    [Button("Resume guest")]
    [EnableIf(nameof(CanPauseResume))]
    public async void ResumeGuest()
    {
        try { await ResumeAsync(); }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"Resume guest: {e.Message}"); }
    }

    bool CanPauseResume => QmpConnected || GdbConnected;

    void OnEnable()
    {
#if UNITY_EDITOR
        _runVmInEditModeCached = runVmInEditMode;
        // Keep the tick subscription outside the undo guard: deleting a VM still runs
        // OnDisable with Undo.isProcessing, and skipping unsubscribe leaves a destroyed
        // instance hooked to EditorApplication.update.
        EditorApplication.update -= EditorTick;
        EditorApplication.update += EditorTick;
        EditorSceneManager.sceneSaved -= OnEditorSceneSaved;
        EditorSceneManager.sceneSaved += OnEditorSceneSaved;

        // A persistent RenderTexture can be recreated/cleared by editor serialization.
        // Repaint the saved frame once enable/reload processing settles.
        QueueIdleOutputPreviewRefresh();
#endif
#if UNITY_EDITOR && UNITY_2022_2_OR_NEWER
        // Undo/redo re-enables components; ignore so we don't restart QEMU.
        if (Undo.isProcessing) return;
#endif
        if (ShowLockedSnapshotLaunchConfig)
            SyncLaunchConfigFromSnapshotMetadata();
#if UNITY_EDITOR
        // Drive edit-mode ticks while enabled; only skip auto-start during transitions.
        if (EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
#endif
        TryAutoStart();
#if UNITY_EDITOR
        RefreshIdleOutputPreview();
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
        EditorSceneManager.sceneSaved -= OnEditorSceneSaved;
        EditorApplication.delayCall -= RefreshIdleOutputPreviewDeferred;
#endif
#if UNITY_EDITOR && UNITY_2022_2_OR_NEWER
        // Undo/redo disables components; ignore so we don't kill QEMU.
        if (Undo.isProcessing) return;
#endif
        StopQemu();
        ReleaseClaimedPorts();
        ReleaseAutoOutputTexture();
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
    void OnEditorSceneSaved(UnityEngine.SceneManagement.Scene scene)
    {
        if (scene == gameObject.scene)
            QueueIdleOutputPreviewRefresh();
    }

    void QueueIdleOutputPreviewRefresh()
    {
        EditorApplication.delayCall -= RefreshIdleOutputPreviewDeferred;
        EditorApplication.delayCall += RefreshIdleOutputPreviewDeferred;
    }

    void RefreshIdleOutputPreviewDeferred()
    {
        if (this == null)
            return;
        RefreshIdleOutputPreview();
    }

    void OnValidateDeferred()
    {
        if (this == null)
            return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        // Only stop when the user turns edit-mode run off — not on every OnValidate
        // (disk/snapshot field writes, scene open, tests calling StartGuestProcessAsync).
        bool editModeToggledOff = _runVmInEditModeCached && !runVmInEditMode;
        _runVmInEditModeCached = runVmInEditMode;
        if (editModeToggledOff && !Application.isPlaying && (IsRunning || _starting))
        {
            StopQemu();
            ReleaseClaimedPorts();
            return;
        }

        TryAutoStart();
        RefreshIdleOutputPreview();
    }

    void EditorTick()
    {
        // Unity fake-null: destroyed instances can still receive a late update callback.
        if (this == null)
            return;
        if (Application.isPlaying || !enabled || !gameObject.activeInHierarchy)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        if (runVmInEditMode)
            Tick();

        MaintainIdleOutputPreview();
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

    /// <summary>
    /// Invalidate a running <see cref="StartQemuAsync"/> and clear the gate so
    /// Stop/Restart can start again. Stale starts check <see cref="_startEpoch"/>
    /// after awaits and bail without touching the new session.
    /// </summary>
    void AbortInFlightStart()
    {
        _startEpoch++;
        _starting = false;
    }

    bool StartAborted(int epoch) => this == null || _startEpoch != epoch;

    async Task StartQemuAsync()
    {
        if (_starting)
        {
            UnityEngine.Debug.LogWarning(
                $"UnityQemu: StartQemu skipped on '{name}' — start already in progress.",
                this);
            return;
        }
        int epoch = ++_startEpoch;
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
            // Don't abort our own epoch — this stop is part of the current start.
            await StopQemuAsync(abortInFlightStart: false);
            if (StartAborted(epoch))
                return;
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

        // Resolve work overlay + pending incoming *before* -m so we can validate the
        // RAM size embedded in the migration stream and (when needed) fall back to a
        // cold disk boot.
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

        // Memory from effective launch config (may adjust to match the migration stream
        // when the user is not overriding snapshot launch config).
        // Extra args via EffectiveExtraQemuArgs so an empty uqsnap extraQemuArgs still falls
        // back to the VM Launch Config (incl. sb16).
        int memoryMb = EffectiveLaunchConfig?.ResolvedMemoryMb ?? LaunchConfig.DefaultMemoryMb;
        if (!string.IsNullOrEmpty(_pendingIncomingStatePath) &&
            MigrationRelay.TryProbePcRamBytes(
                _pendingIncomingStatePath, _pendingIncomingGzip, out long streamRamBytes))
        {
            int streamMb = (int)(streamRamBytes / (1024L * 1024L));
            if (streamMb > 0 && streamMb != memoryMb)
            {
                if (overrideSnapshotLaunchConfig)
                {
                    UnityEngine.Debug.LogWarning(
                        $"Snapshot machine-state has pc.ram={streamMb} MB but launch config says " +
                        $"{memoryMb} MB — skipping machine-state restore and booting disk contents " +
                        $"with launch-config memory.");
                    // If the user intentionally overrode the launch config, we should still start
                    // with that configuration even if state restore can't be trusted.
                    _pendingIncomingStatePath = null;
                    _pendingIncomingGzip = true;
                }
                else
                {
                    UnityEngine.Debug.LogWarning(
                        $"Snapshot machine-state has pc.ram={streamMb} MB but launch metadata " +
                        $"says {memoryMb} MB — using stream size for -m so restore can succeed.");
                    memoryMb = streamMb;
                    // Keep session metadata in sync so EffectiveLaunchConfig matches what we boot.
                    UqsnapAsset owner = LaunchConfigOwnerSnap;
                    if (owner?.metadata?.launchConfig != null)
                        owner.metadata.launchConfig.memoryMb = streamMb;
                }
            }
        }

        bool boundPorts = false;
        for (int portTry = 0; portTry < MaxPortBindRetries; portTry++)
        {
        AllocatePortsForStart();

        var process = new Process();
        process.StartInfo.FileName = qemuExe;

        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(memoryMb.ToString());

        foreach (var arg in EffectiveExtraQemuArgs.Split(
                     new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        EffectiveLaunchConfig?.AppendUsbEhciArgs(process.StartInfo.ArgumentList);

        if (ShowGui)
        {
            process.StartInfo.ArgumentList.Add("-display");
            process.StartInfo.ArgumentList.Add("sdl");
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

        // IDE index 0 is -hda; CDs use the remaining units. vvfat is hotplug-only (PeripheralsUI).
        var usedIdeIndices = new System.Collections.Generic.HashSet<int> { 0 };
        AppendCdromArgs(process.StartInfo.ArgumentList, EffectiveCdroms, usedIdeIndices);
        AppendFloppyArgs(process.StartInfo.ArgumentList, EffectiveFloppies);

        // Add VNC display - :N means display N → TCP port 5900+N.
        // audiodev=snd0 only when Unity playback is on (matches LaunchConfig id=snd0).
        process.StartInfo.ArgumentList.Add("-display");
        string vncDisplay = $"vnc=:{_activeVncPort - QemuPortAllocator.VncBasePort}";
        if (playAudioInUnity)
            vncDisplay += ",audiodev=snd0";
        process.StartInfo.ArgumentList.Add(vncDisplay);
        
        // Add QMP socket for command control
        // Format: -qmp tcp:host:port,server,nowait
        // -qmp replaces the default HMP monitor, so keep readline HMP on the VC explicitly
        // (Ctrl+Alt+2 in the SDL/GTK window) when we also want interactive monitor access.
        if (enableQmp) {
            process.StartInfo.ArgumentList.Add("-qmp");
            process.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{_activeQmpPort},server,nowait");
            process.StartInfo.ArgumentList.Add("-monitor");
            process.StartInfo.ArgumentList.Add("vc");
        }

        // GDB stub for memory peek/poke (-s is shorthand for tcp::1234)
        if (enableGdb) {
            process.StartInfo.ArgumentList.Add("-gdb");
            process.StartInfo.ArgumentList.Add($"tcp:127.0.0.1:{_activeGdbPort},server,nowait");
        }
        
        // Redirect output to see if QEMU has any errors
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        // So QEMU finds share/ BIOS next to the packaged qemu~ tree.
        process.StartInfo.WorkingDirectory = Paths.QemuDir;

        UnityEngine.Debug.Log($"{qemuExe} {string.Join(' ', process.StartInfo.ArgumentList)}");

        if (StartAborted(epoch))
            return;

        // Drop OS holds immediately before spawn so qemu-system can bind the same ports.
        HandOffPortsToQemu();
        process.Start();
        _qemuProcess = process;
        ApplyProcessPriority(process);

        UnityEngine.Debug.Log(
            $"Started QEMU process (PID: {process.Id}) with VNC on port {_activeVncPort}" +
            (enableQmp ? $", QMP {_activeQmpPort}" : "") +
            (enableGdb ? $", GDB {_activeGdbPort}" : "") +
            $" (preferred VNC display :{QemuPortAllocator.PreferredVncDisplay()})");

        var earlyStderr = new StringBuilder();
        // Log QEMU stdout/stderr (null Data = stream closed / async reader sentinel — ignore)
        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                UnityEngine.Debug.Log($"QEMU output: {e.Data}");
        };
        process.ErrorDataReceived += (sender, e) => {
            if (string.IsNullOrEmpty(e.Data))
                return;
            lock (earlyStderr)
                earlyStderr.AppendLine(e.Data);
            UnityEngine.Debug.LogWarning($"QEMU error: {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool survivedPortBind = await QemuSurvivedPortBindAsync(earlyStderr);
        if (StartAborted(epoch))
            return;
        if (survivedPortBind)
        {
            boundPorts = true;
            break;
        }

        UnityEngine.Debug.LogWarning(
            $"QEMU port bind failed (attempt {portTry + 1}/{MaxPortBindRetries}); " +
            "reclaiming ports and retrying.");
        AbortFailedQemuStart();
        await WaitForActivePortsFreeAsync(1000);
        if (StartAborted(epoch))
            return;
        ReleaseClaimedPorts();
        } // port-bind retry loop

        if (!boundPorts)
        {
            throw new InvalidOperationException(
                "QEMU failed to bind VNC/QMP/GDB ports after retries. " +
                "Stop other VirtualMachines or override ports in Advanced.");
        }

        // Wait a moment for QEMU to start and QMP socket to be ready
        await Task.Delay(1000);
        if (StartAborted(epoch))
            return;

        // Connect VNC client
        await ConnectVncAsync();
        if (StartAborted(epoch))
            return;
        
        if (enableQmp) {
            // Connect QMP client
            await ConnectQmpAsync();
            if (StartAborted(epoch))
                return;

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
                if (StartAborted(epoch))
                    return;
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
                if (StartAborted(epoch))
                    return;
            }
        }

#if UNITY_EDITOR
        ClearVvfatSessionTracking();
#endif
        try { OnReady?.Invoke(); }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
        break;
        } // cold-boot retry loop
        }
        catch (Exception e)
        {
            if (StartAborted(epoch))
                return;
            UnityEngine.Debug.LogException(e);
            if (!IsRunning)
                ReleaseClaimedPorts();
        }
        finally
        {
            // Only the active start clears the gate — a superseded start must not
            // clear _starting for a newer Restart/Start that already took over.
            if (_startEpoch == epoch)
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
        MaybeAutoRestart();

        if (_vncClient == null)
            return;

        _vncClient.Update();

        Texture2D src = _vncClient.Texture;
        if (src != null)
        {
            EnsureOutputTexture(src.width, src.height);
            RenderTexture dest = OutputTexture;
            if (dest != null)
            {
                Graphics.Blit(src, dest);
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

    /// <summary>
    /// If <see cref="autoRestart"/> is on and QEMU died while we still want it running,
    /// clean up and start again (preserving <see cref="sessionCurrent"/> when possible).
    /// </summary>
    void MaybeAutoRestart()
    {
        if (!autoRestart || _autoRestartInFlight || _starting)
            return;
        if (!ShouldRun || !enabled || !gameObject.activeInHierarchy)
            return;
        // Intentional StopQemu clears _qemuProcess; a non-null exited handle means unexpected death.
        if (_qemuProcess == null || !_qemuProcess.HasExited)
            return;
        if (Time.realtimeSinceStartup - _lastAutoRestartRealtime < AutoRestartCooldownSeconds)
            return;

        _lastAutoRestartRealtime = Time.realtimeSinceStartup;
        int exitCode = -1;
        try { exitCode = _qemuProcess.ExitCode; } catch { /* process handle may be disposed */ }
        UnityEngine.Debug.LogWarning(
            $"UnityQemu: QEMU exited unexpectedly (exit code {exitCode}) on '{name}' — auto-restarting.",
            this);
        _ = AutoRestartAfterUnexpectedExitAsync();
    }

    async Task AutoRestartAfterUnexpectedExitAsync()
    {
        if (_autoRestartInFlight || _starting)
            return;
        _autoRestartInFlight = true;
        try
        {
            BootableAsset tip = _sessionCurrent;
            await StopQemuAsync();

            if (!autoRestart || !ShouldRun || !enabled || !gameObject.activeInHierarchy)
                return;

            if (tip is UqsnapAsset snap)
                PrepareBoot(snap, loadVmState: true);
            else if (tip is DiskAsset disk)
                PrepareBoot(disk);

            await StartQemuAsync();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogException(e, this);
        }
        finally
        {
            _autoRestartInFlight = false;
        }
    }

    void EnsureOutputTexture(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        if (useCustomRenderTexture)
        {
            if (outputTexture == null)
                return;

            if (outputTexture.width != width || outputTexture.height != height)
            {
                outputTexture.Release();
                outputTexture.width = width;
                outputTexture.height = height;
            }

            if (!outputTexture.IsCreated())
                outputTexture.Create();
            return;
        }

        if (renderTextureSettings == null)
            renderTextureSettings = new RenderTextureSettings();

        _autoOutputTexture = renderTextureSettings.Ensure(
            _autoOutputTexture,
            width,
            height,
            $"{name} QEMU Output",
            ReleaseOwnedRenderTexture);
    }

    static void ReleaseOwnedRenderTexture(RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(rt);
        else
            UnityEngine.Object.DestroyImmediate(rt);
    }

    void ReleaseAutoOutputTexture()
    {
        if (_autoOutputTexture == null)
            return;
        ReleaseOwnedRenderTexture(_autoOutputTexture);
        _autoOutputTexture = null;
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
        ReleaseClaimedPorts();
        ReleaseAutoOutputTexture();
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
        VncAudioPlayer audioPlayer = null;
        if (playAudioInUnity)
        {
            audioPlayer = GetComponent<VncAudioPlayer>();
            if (audioPlayer == null)
                audioPlayer = gameObject.AddComponent<VncAudioPlayer>();
            audioPlayer.enabled = true;
            audioPlayer.StartPlayback();
        }
        else
        {
            var existing = GetComponent<VncAudioPlayer>();
            if (existing != null)
                existing.StopPlayback();
        }

        _vncClient = new VncClient
        {
            PlayAudioInUnity = playAudioInUnity,
            AudioPlayer = audioPlayer,
        };
        EnsureOutputTexture(640, 480);
        return ConnectVncCoreAsync(_vncClient, _activeVncPort - QemuPortAllocator.VncBasePort);
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
        return ConnectQmpCoreAsync(_qmpClient, _activeQmpPort, verboseQmp);
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
        string command = $"loadvm {tag}";
        try
        {
            string failure = await TryGetHumanMonitorFailureAsync(command);
            if (failure == null)
            {
                UnityEngine.Debug.Log($"loadvm {tag} OK");
                return;
            }

            // Reply already logged by TryGetHumanMonitorFailureAsync.
            LastStateRestoreError = failure;
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

            string saveCmd = $"savevm {DiskOverlay.DurableSaveVmTag}";
            // Non-empty reply is logged by TryGetHumanMonitorFailureAsync.
            await TryGetHumanMonitorFailureAsync(saveCmd);

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
    /// Result of <see cref="CaptureStateAsync"/>: frozen disk layer always; machine
    /// state only when migrate succeeded (writable vvfat / some devices block it).
    /// </summary>
    public readonly struct CaptureStateResult
    {
        public readonly string FrozenLayerPath;
        public readonly bool CapturedMachineState;

        public CaptureStateResult(string frozenLayerPath, bool capturedMachineState)
        {
            FrozenLayerPath = frozenLayerPath;
            CapturedMachineState = capturedMachineState;
        }
    }

    /// <summary>
    /// D4 durable save, QEMU side: pause → freeze the current work layer as the session's
    /// disk delta (<c>blockdev-snapshot-sync</c>; a fresh layer becomes active) →
    /// quick-save into the new layer (must precede migrate: the postmigrate runstate
    /// refuses savevm) → optionally migrate RAM/CPU out into
    /// <paramref name="vmstateOutputPath"/> → resume.
    /// When <paramref name="captureMachineState"/> is false, skips migrate and returns
    /// disk-only (<see cref="CaptureStateResult.CapturedMachineState"/> false).
    /// When <paramref name="gzip"/> is true, the file is gzip-compressed.
    /// The migration runs over a pre-connected duplicated socket (<c>migrate fd:</c>);
    /// see <see cref="MigrationRelay"/> for why <c>tcp:</c> can hang.
    /// If migrate fails (e.g. writable vvfat drives), still returns the frozen
    /// layer so a disk-only tip can be saved; <see cref="CaptureStateResult.CapturedMachineState"/>
    /// is false. QEMU keeps running.
    /// </summary>
    public async Task<CaptureStateResult> CaptureStateAsync(
        string vmstateOutputPath, bool gzip = true, bool captureMachineState = true)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
            throw new InvalidOperationException("QMP not connected");
        if (string.IsNullOrEmpty(_workOverlayPath) || !File.Exists(_workOverlayPath))
            throw new InvalidOperationException("No work image — boot with a Disk Asset first");
        if (captureMachineState && string.IsNullOrWhiteSpace(vmstateOutputPath))
            throw new ArgumentException("vmstate output path required", nameof(vmstateOutputPath));

        string frozenLayer = _workOverlayPath;
        string newLayer = DiskOverlay.WorkLayerPathForSession(WorkSessionId, ++_workLayerCounter);
        bool capturedMachineState = false;

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

            string saveCmd = $"savevm {DiskOverlay.DurableSaveVmTag}";
            // Non-empty reply is logged by TryGetHumanMonitorFailureAsync.
            await TryGetHumanMonitorFailureAsync(saveCmd);

            if (captureMachineState)
            {
                try
                {
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
                        capturedMachineState = bytes > 0;
                    }
                }
                catch (Exception migrateError)
                {
                    TryDeleteFile(vmstateOutputPath);
                    string hint = migrateError.Message != null &&
                                  migrateError.Message.IndexOf("vvfat", StringComparison.OrdinalIgnoreCase) >= 0
                        ? " Detach vvfat drives before save for a full RAM snapshot, " +
                          "or keep this as a cold-bootable disk tip."
                        : "";
                    UnityEngine.Debug.LogWarning(
                        $"UnityQemu: machine-state migrate failed ({migrateError.Message}). " +
                        $"Saving disk tip only.{hint}");
                    capturedMachineState = false;
                }
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

        return new CaptureStateResult(frozenLayer, capturedMachineState);
    }

    static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* best-effort cleanup of a partial migrate stream */
        }
    }

    /// <summary>
    /// Run an HMP command via the connected QMP session (e.g. savevm / loadvm / info snapshots).
    /// Returns the raw reply text. Non-empty replies from commands that normally stay silent are
    /// logged as warnings (for debugging), unless <paramref name="expectTextOutput"/> is set or
    /// the command is a known query (<c>info</c>, <c>help</c>, …).
    /// Prefer <see cref="RunHumanMonitorCommandOrThrowAsync"/> for mutating commands.
    /// </summary>
    public async Task<string> RunHumanMonitorCommandAsync(
        string commandLine, bool expectTextOutput = false)
    {
        string result = await RunHumanMonitorCommandCoreAsync(commandLine);
        SurfaceUnexpectedHmpReply(commandLine, result, expectTextOutput);
        return result;
    }

    /// <summary>
    /// Mutating HMP helper. Success is an empty reply, or a bare <c>OK</c>
    /// (<c>drive_add if=none</c> prints that; most other HMP commands print nothing).
    /// Any other text throws. Use <see cref="RunHumanMonitorCommandAsync"/> for queries.
    /// </summary>
    public async Task RunHumanMonitorCommandOrThrowAsync(string commandLine)
    {
        string result = await RunHumanMonitorCommandCoreAsync(commandLine);
        if (!IsHmpSuccessReply(result))
            throw new InvalidOperationException(FormatHmpFailure(commandLine, result));
    }

    /// <summary>
    /// Runs HMP and returns trimmed failure text, or <c>null</c> on success
    /// (empty / bare <c>OK</c>). Non-success replies are also logged.
    /// </summary>
    public async Task<string> TryGetHumanMonitorFailureAsync(string commandLine)
    {
        string result = await RunHumanMonitorCommandCoreAsync(commandLine);
        if (IsHmpSuccessReply(result))
            return null;
        SurfaceUnexpectedHmpReply(commandLine, result, expectTextOutput: false);
        return result.Trim();
    }

    async Task<string> RunHumanMonitorCommandCoreAsync(string commandLine)
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
        {
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");
        }
        return await _qmpClient.RunHumanMonitorCommandAsync(commandLine) ?? "";
    }

    /// <summary>
    /// Log HMP text that is not an expected query dump or a silent/OK success.
    /// </summary>
    static void SurfaceUnexpectedHmpReply(
        string commandLine, string result, bool expectTextOutput)
    {
        if (IsHmpSuccessReply(result))
            return;
        if (expectTextOutput || HmpCommandExpectsTextOutput(commandLine))
            return;

        UnityEngine.Debug.LogWarning(FormatHmpReply(commandLine, result));
    }

    /// <summary>
    /// HMP has no single success token. Most mutating commands print nothing;
    /// <c>drive_add</c> with <c>if=none</c> is a special case that prints <c>OK</c>
    /// (see QEMU <c>hmp_drive_add</c>). Treat empty and bare OK as success.
    /// </summary>
    public static bool IsHmpSuccessReply(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;
        return string.Equals(result.Trim(), "OK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether this HMP verb normally prints a text dump (not a silent mutate).</summary>
    public static bool HmpCommandExpectsTextOutput(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        string trimmed = commandLine.TrimStart();
        int sp = trimmed.IndexOfAny(new[] { ' ', '\t' });
        string verb = (sp < 0 ? trimmed : trimmed.Substring(0, sp)).ToLowerInvariant();
        switch (verb)
        {
            case "info":
            case "help":
            case "?":
            case "print":
            case "p":
            case "x":
            case "xp":
            case "sum":
            case "history":
            case "qom-list":
            case "qom-get":
            case "qom-list-types":
            case "qom-list-properties":
                return true;
            default:
                return false;
        }
    }

    /// <summary>Build a short exception / log message for a failed mutating HMP command.</summary>
    public static string FormatHmpFailure(string commandLine, string failureDetail) =>
        FormatHmpReply(commandLine, failureDetail, failed: true);

    /// <summary>Format an HMP reply for logs (whether or not we treat it as a hard failure).</summary>
    public static string FormatHmpReply(
        string commandLine, string replyDetail, bool failed = false)
    {
        string summary = SummarizeHmpCommand(commandLine);
        string detail = replyDetail?.Trim() ?? "";
        string label = failed ? "failed" : "reply";
        return string.IsNullOrEmpty(summary)
            ? detail
            : $"HMP `{summary}` {label}:\n{detail}";
    }

    static string SummarizeHmpCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return "";
        // Verb + first arg only — omit long file= paths from titles.
        string[] parts = commandLine.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return commandLine.Trim();
        if (parts.Length == 1)
            return parts[0];
        return parts[0] + " " + parts[1];
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
    /// Filesystem path (or QEMU file spec) currently inserted in a block device,
    /// from QMP <c>query-block</c>. Null when empty / unknown.
    /// </summary>
    public async Task<string> TryGetInsertedBlockFileAsync(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;
        if (_qmpClient == null || !_qmpClient.IsConnected)
            throw new InvalidOperationException("QMP not connected (is enableQmp on, and is QEMU running?)");

        JObject response = await _qmpClient.ExecuteCommandAsync("query-block");
        JArray arr = response["return"] as JArray;
        if (arr == null)
            return null;

        string want = deviceName.Trim();
        foreach (JToken entry in arr)
        {
            string device = entry["device"]?.Value<string>();
            if (!string.Equals(device, want, StringComparison.OrdinalIgnoreCase))
                continue;

            string file = entry["inserted"]?["file"]?.Value<string>();
            return string.IsNullOrWhiteSpace(file) ? null : file.Trim();
        }

        return null;
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

    /// <summary>
    /// True when QMP <c>query-status</c> reports <c>paused</c>.
    /// False when running or when QMP is not connected.
    /// </summary>
    public async Task<bool> IsPausedAsync()
    {
        if (_qmpClient == null || !_qmpClient.IsConnected)
            return false;

        JObject response = await _qmpClient.ExecuteCommandAsync("query-status");
        string status = response["return"]?["status"]?.Value<string>();
        return string.Equals(status, "paused", StringComparison.Ordinal);
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

    async Task StopQemuAsync(bool abortInFlightStart = true)
    {
        StopQemu(abortInFlightStart);
        // Windows can take a beat to release listen sockets after Kill.
        await WaitForActivePortsFreeAsync(2000);
        ReleaseClaimedPorts();
    }

    void StopQemu(bool abortInFlightStart = true)
    {
        if (abortInFlightStart)
            AbortInFlightStart();

        _vncClient?.Dispose();
        _vncClient = null;
        var audioPlayer = GetComponent<VncAudioPlayer>();
        if (audioPlayer != null)
            audioPlayer.StopPlayback();
        
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
#if UNITY_EDITOR
        RefreshIdleOutputPreview();
#endif
#if UNITY_EDITOR
        ClearVvfatSessionTracking();
#endif
        try { OnStopped?.Invoke(); }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
    }

    /// <summary>
    /// Pick VNC/QMP/GDB ports for this start and hold the OS sockets until
    /// <see cref="HandOffPortsToQemu"/>. Default: free ports via
    /// <see cref="QemuPortAllocator"/> (VNC prefers a project-hash display).
    /// Advanced override: fixed serialized ports.
    /// </summary>
    void AllocatePortsForStart()
    {
        ReleaseClaimedPorts();
        _activeVncPort = 0;
        _activeQmpPort = 0;
        _activeGdbPort = 0;

        try
        {
            if (overridePorts)
            {
                _heldVncPort = QemuPortAllocator.ClaimExact(vncPort);
                _activeVncPort = _heldVncPort.Port;
                if (enableQmp)
                {
                    _heldQmpPort = QemuPortAllocator.ClaimExact(qmpPort);
                    _activeQmpPort = _heldQmpPort.Port;
                }

                if (enableGdb)
                {
                    _heldGdbPort = QemuPortAllocator.ClaimExact(gdbPort);
                    _activeGdbPort = _heldGdbPort.Port;
                }
            }
            else
            {
                _heldVncPort = QemuPortAllocator.ClaimVncDisplayPort();
                _activeVncPort = _heldVncPort.Port;
                if (enableQmp)
                {
                    _heldQmpPort = QemuPortAllocator.ClaimEphemeralPort();
                    _activeQmpPort = _heldQmpPort.Port;
                }
                if (enableGdb)
                {
                    _heldGdbPort = QemuPortAllocator.ClaimEphemeralPort();
                    _activeGdbPort = _heldGdbPort.Port;
                }
            }
        }
        catch
        {
            ReleaseClaimedPorts();
            throw;
        }
    }

    void HandOffPortsToQemu()
    {
        _heldVncPort?.HandOff();
        _heldQmpPort?.HandOff();
        _heldGdbPort?.HandOff();
    }

    void ReleaseClaimedPorts()
    {
        _heldVncPort?.Dispose();
        _heldVncPort = null;
        _heldQmpPort?.Dispose();
        _heldQmpPort = null;
        _heldGdbPort?.Dispose();
        _heldGdbPort = null;
    }

    /// <summary>
    /// Kill a QEMU process that failed during start without firing <see cref="OnStopped"/>
    /// (used for port-bind retries).
    /// </summary>
    void AbortFailedQemuStart()
    {
        _vncClient?.Dispose();
        _vncClient = null;
        _qmpClient?.Dispose();
        _qmpClient = null;
        _gdbClient?.Dispose();
        _gdbClient = null;

        if (_qemuProcess == null)
            return;
        try
        {
            try { _qemuProcess.CancelOutputRead(); } catch { /* ignore */ }
            try { _qemuProcess.CancelErrorRead(); } catch { /* ignore */ }
            if (!_qemuProcess.HasExited)
            {
                _qemuProcess.Kill();
                _qemuProcess.WaitForExit(3000);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"Error aborting QEMU start: {e.Message}");
        }
        finally
        {
            try { _qemuProcess.Dispose(); } catch { /* ignore */ }
            _qemuProcess = null;
        }
    }

    async Task<bool> QemuSurvivedPortBindAsync(StringBuilder earlyStderr)
    {
        for (int i = 0; i < 10; i++)
        {
            if (_qemuProcess == null || _qemuProcess.HasExited)
            {
                string text;
                lock (earlyStderr)
                    text = earlyStderr.ToString();
                // Give async stderr a beat to flush after exit.
                if (string.IsNullOrEmpty(text))
                {
                    await Task.Delay(50);
                    lock (earlyStderr)
                        text = earlyStderr.ToString();
                }
                return !QemuPortAllocator.LooksLikeAddressInUse(text);
            }
            await Task.Delay(50);
        }
        return true;
    }

    async Task WaitForActivePortsFreeAsync(int timeoutMs)
    {
        var ports = new System.Collections.Generic.List<int>(3);
        if (_activeVncPort > 0) ports.Add(_activeVncPort);
        if (_activeQmpPort > 0) ports.Add(_activeQmpPort);
        if (_activeGdbPort > 0) ports.Add(_activeGdbPort);
        if (ports.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool allFree = true;
            foreach (int port in ports)
            {
                if (!QemuPortAllocator.IsPortFree(port))
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
            $"Timed out waiting for QEMU ports to free " +
            $"(vnc={_activeVncPort}, qmp={_activeQmpPort}, gdb={_activeGdbPort}). " +
            "Restart may fail if an old qemu-system process is still running.");
    }

    void ConnectGdb()
    {
        try
        {
            _gdbClient?.Dispose();
            _gdbClient = new GdbClient { Verbose = verboseGdb };
            _gdbClient.Connect("127.0.0.1", _activeGdbPort, gdbPhysicalMemory);
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