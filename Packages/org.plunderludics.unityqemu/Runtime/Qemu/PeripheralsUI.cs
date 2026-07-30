using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Inspector helpers for hot-plugging removable media into a running guest
/// (insert/eject ISO or floppy via QEMU HMP <c>change</c> / <c>eject</c>;
/// attach/detach USB vvfat).
/// <para>
/// Floppy hotplug: <see cref="FloppyAsset"/> images, or a tiny folder as vvfat floppy
/// (~1.44MB, session-only). Larger live shares: USB vvfat here (~504 MiB QEMU default).
/// Writable vvfat blocks migrate/savevm while attached — SnapshotUI disconnects them
/// automatically (with confirmation) before a full RAM save.
/// </para>
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("cdrom/actions")]
[DeclareHorizontalGroup("floppy/actions")]
[DeclareHorizontalGroup("vvfat/actions")]
public class PeripheralsUI : MonoBehaviour
{
    const string VvfatDriveIdPrefix = "uqvv";

    /// <summary>
    /// QEMU non-floppy vvfat default capacity (CHS 1024×16×63). Folder contents must fit
    /// under this; FAT metadata eats a little more, so treat it as a hard ceiling.
    /// </summary>
    const long VvfatUsbCapacityBytes = 504L * 1024 * 1024;

    public VirtualMachine virtualMachine;

    [Tooltip(
        "If the chosen media is already a project CdRomAsset / FloppyAsset, " +
        "also append/remove it on EffectiveLaunchConfig (uqsnap metadata when locked, " +
        "otherwise the VM launchConfig) so the next durable save records the insert/eject. " +
        "Paths outside the project are hotplugged by path only.")]
    public bool alsoAddToLaunchConfig = true;

#if UNITY_EDITOR
    sealed class HotpluggedVvfatDrive
    {
        public string id;
        public string folderPath;
    }

    readonly List<HotpluggedVvfatDrive> _hotpluggedVvfatDrives = new List<HotpluggedVvfatDrive>();
    int _nextVvfatDriveIndex;
    VirtualMachine _boundMachine;

    bool QmpReady => virtualMachine != null && virtualMachine.QmpConnected;

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
        BindMachine(virtualMachine);
    }

    void OnDisable()
    {
        BindMachine(null);
    }

    void OnValidate()
    {
        if (!Application.isPlaying && virtualMachine != _boundMachine)
            BindMachine(virtualMachine);
    }

    void BindMachine(VirtualMachine next)
    {
        if (_boundMachine != null)
        {
            _boundMachine.OnReady -= HandleMachineReady;
            _boundMachine.OnStopped -= HandleMachineStopped;
        }
        _boundMachine = next;
        if (_boundMachine != null)
        {
            _boundMachine.OnReady += HandleMachineReady;
            _boundMachine.OnStopped += HandleMachineStopped;
        }
    }

    void HandleMachineReady() => ClearVvfatSessionTracking();

    void HandleMachineStopped() => ClearVvfatSessionTracking();

    void ClearVvfatSessionTracking()
    {
        _hotpluggedVvfatDrives.Clear();
        _nextVvfatDriveIndex = 0;
    }

    static string MediaPickerDirectory => UnityQemuProjectSettings.GetPickerDirectory();

    [Group("cdrom/actions")]
    [Button("Insert CD")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertIsoButton()
    {
        string path = EditorUtility.OpenFilePanel("Choose ISO", MediaPickerDirectory, "iso");
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await InsertIsoAsync(path);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    [Group("cdrom/actions")]
    [Button("Eject CD")]
    [EnableIf(nameof(QmpReady))]
    public async void EjectCdromButton()
    {
        try
        {
            await EjectCdromAsync();
            Debug.Log("CD ejected");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    [Group("floppy/actions")]
    [Button("Insert floppy")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertFloppyButton()
    {
        int choice = EditorUtility.DisplayDialogComplex(
            "Insert floppy",
            "Choose an image file (.img/.ima) or a project folder (vvfat ~1.44MB).",
            "Image file",
            "Cancel",
            "Folder");
        if (choice == 1)
            return;

        try
        {
            if (choice == 0)
            {
                string path = EditorUtility.OpenFilePanel(
                    "Choose floppy image", MediaPickerDirectory, "img,ima");
                if (string.IsNullOrEmpty(path))
                    return;
                await InsertFloppyImageAsync(path);
            }
            else
            {
                string folder = EditorUtility.OpenFolderPanel(
                    "Choose floppy folder (vvfat ~1.44MB)", MediaPickerDirectory, "");
                if (string.IsNullOrEmpty(folder))
                    return;
                await InsertFloppyFolderAsync(folder);
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    [Group("floppy/actions")]
    [Button("Eject floppy")]
    [EnableIf(nameof(QmpReady))]
    public async void EjectFloppyButton()
    {
        try
        {
            await EjectFloppyAsync();
            Debug.Log("Floppy ejected");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    [Group("vvfat/actions")]
    [Button("Attach vvfat drive")]
    [EnableIf(nameof(QmpReady))]
    public async void AttachVvfatDriveButton()
    {
        string folder = EditorUtility.OpenFolderPanel(
            "Choose folder for vvfat drive (USB)", MediaPickerDirectory, "");
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            await AttachVvfatDriveAsync(folder);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    [Group("vvfat/actions")]
    [Button("Detach vvfat drive")]
    [EnableIf(nameof(QmpReady))]
    public async void DetachVvfatDriveButton()
    {
        try
        {
            await ReconcileVvfatTrackingAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"vvfat tracking reconcile failed: {e.Message}");
        }

        if (_hotpluggedVvfatDrives.Count == 0)
        {
            Debug.LogWarning("No hotplugged vvfat drives in this session.");
            return;
        }

        HotpluggedVvfatDrive target = _hotpluggedVvfatDrives[_hotpluggedVvfatDrives.Count - 1];
        if (_hotpluggedVvfatDrives.Count > 1)
        {
            var labels = new string[_hotpluggedVvfatDrives.Count];
            for (int i = 0; i < _hotpluggedVvfatDrives.Count; i++)
            {
                HotpluggedVvfatDrive drive = _hotpluggedVvfatDrives[i];
                labels[i] = $"{drive.id}: {Path.GetFileName(drive.folderPath)}";
            }

            int pick = EditorUtility.DisplayDialogComplex(
                "Detach vvfat drive",
                "Multiple vvfat drives are attached. Detach the most recent?\n\n" +
                string.Join("\n", labels),
                "Detach latest",
                "Cancel",
                "Detach oldest");
            if (pick == 1)
                return;
            target = pick == 0
                ? _hotpluggedVvfatDrives[_hotpluggedVvfatDrives.Count - 1]
                : _hotpluggedVvfatDrives[0];
        }

        try
        {
            await DetachVvfatDriveAsync(target.id);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    /// <summary>
    /// Hot-insert an ISO via HMP <c>change</c>. Uses the filesystem path as-is (no copy).
    /// If <see cref="alsoAddToLaunchConfig"/> is on and the ISO is already a project
    /// <see cref="CdRomAsset"/>, that asset is appended to launch config.
    /// </summary>
    public async Task<CdRomAsset> InsertIsoAsync(string isoFilesystemPath)
    {
        RequireQmp();
        if (string.IsNullOrWhiteSpace(isoFilesystemPath) || !File.Exists(isoFilesystemPath))
            throw new FileNotFoundException("ISO not found", isoFilesystemPath);

        string fullIso = Path.GetFullPath(isoFilesystemPath);
        CdRomAsset asset = FindExistingCdRomAsset(fullIso);

        string device = await ResolveCdromDeviceAsync();
        string qemuPath = fullIso.Replace('\\', '/');
        await ChangeMediaAsync(device, qemuPath);

        if (alsoAddToLaunchConfig)
        {
            if (asset != null)
            {
                if (virtualMachine.AddCdRomToEffectiveLaunchConfig(asset))
                    Debug.Log($"Added '{asset.DisplayLabel}' to EffectiveLaunchConfig (will persist on next durable save).");
            }
            else
            {
                Debug.Log(
                    "ISO inserted by path only (not a project CdRomAsset) — " +
                    "EffectiveLaunchConfig was left unchanged. Drop the .iso under Assets to keep it.");
            }
        }

        Debug.Log($"Inserted ISO into {device}: {qemuPath}");
        return asset;
    }

    public async Task EjectCdromAsync()
    {
        RequireQmp();
        string device = await ResolveCdromDeviceAsync();
        string insertedFile = null;
        if (alsoAddToLaunchConfig)
        {
            try { insertedFile = await virtualMachine.TryGetInsertedBlockFileAsync(device); }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not query inserted CD before eject: {e.Message}");
            }
        }

        await RunHmpOrThrowAsync($"eject -f {device}");

        if (alsoAddToLaunchConfig && !string.IsNullOrEmpty(insertedFile))
        {
            CdRomAsset asset = FindExistingCdRomAsset(NormalizeInsertedFilePath(insertedFile));
            if (asset != null && virtualMachine.RemoveCdRomFromEffectiveLaunchConfig(asset))
            {
                Debug.Log(
                    $"Removed '{asset.DisplayLabel}' from EffectiveLaunchConfig " +
                    "(will persist on next durable save).");
            }
        }
    }

    /// <summary>
    /// Hot-insert a floppy image via HMP <c>change</c>.
    /// Project <see cref="FloppyAsset"/>s may also be appended to EffectiveLaunchConfig.
    /// </summary>
    public async Task InsertFloppyImageAsync(string imageFilesystemPath)
    {
        RequireQmp();
        if (string.IsNullOrWhiteSpace(imageFilesystemPath) || !File.Exists(imageFilesystemPath))
            throw new FileNotFoundException("Floppy image not found", imageFilesystemPath);

        string full = Path.GetFullPath(imageFilesystemPath);
        string device = await ResolveFloppyDeviceAsync();
        string qemuPath = full.Replace('\\', '/');
        await ChangeMediaAsync(device, qemuPath);

        TryAddFloppyAssetToLaunchConfig(full);
        Debug.Log($"Inserted floppy image into {device}: {qemuPath}");
    }

    /// <summary>
    /// Hot-insert a folder as a vvfat floppy (<c>fat:floppy:ro:…</c>, ~1.44MB).
    /// Read-only so it stays compatible with savevm/loadvm. Session-only (not Launch Config).
    /// </summary>
    public async Task InsertFloppyFolderAsync(string folderFilesystemPath)
    {
        RequireQmp();
        if (string.IsNullOrWhiteSpace(folderFilesystemPath) || !Directory.Exists(folderFilesystemPath))
            throw new DirectoryNotFoundException($"Floppy folder not found: {folderFilesystemPath}");

        string full = Path.GetFullPath(folderFilesystemPath);
        string device = await ResolveFloppyDeviceAsync();
        string fileSpec = $"fat:floppy:ro:{full.Replace('\\', '/')}";
        await ChangeMediaAsync(device, fileSpec);

        Debug.Log($"Inserted floppy folder into {device}: {fileSpec}");
    }

    public async Task EjectFloppyAsync()
    {
        RequireQmp();
        string device = await ResolveFloppyDeviceAsync();
        string insertedFile = null;
        if (alsoAddToLaunchConfig)
        {
            try { insertedFile = await virtualMachine.TryGetInsertedBlockFileAsync(device); }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not query inserted floppy before eject: {e.Message}");
            }
        }

        await RunHmpOrThrowAsync($"eject -f {device}");

        if (alsoAddToLaunchConfig && !string.IsNullOrEmpty(insertedFile))
        {
            FloppyAsset asset = FindExistingFloppyAsset(NormalizeInsertedFilePath(insertedFile));
            if (asset != null && virtualMachine.RemoveFloppyFromEffectiveLaunchConfig(asset))
            {
                Debug.Log(
                    $"Removed '{asset.DisplayLabel}' from EffectiveLaunchConfig floppies " +
                    "(will persist on next durable save).");
            }
        }
    }

    /// <summary>
    /// Hot-attach a folder as a USB vvfat drive (<c>fat:rw:…</c>) via HMP
    /// <c>drive_add</c> + <c>device_add usb-storage</c> on a dedicated EHCI bus
    /// (<see cref="LaunchConfig.DefaultUsbEhciId"/>), so the UHCI <c>usb-tablet</c> mouse is not
    /// re-enumerated. Writable vvfat blocks migrate/savevm until disconnected
    /// (SnapshotUI offers to detach before a full RAM save).
    /// Host folder must fit in QEMU's default vvfat image (~504 MiB).
    /// </summary>
    public async Task<string> AttachVvfatDriveAsync(string folderFilesystemPath)
    {
        RequireQmp();
        if (string.IsNullOrWhiteSpace(folderFilesystemPath) || !Directory.Exists(folderFilesystemPath))
            throw new DirectoryNotFoundException($"vvfat drive folder not found: {folderFilesystemPath}");

        string full = Path.GetFullPath(folderFilesystemPath);
        long folderBytes = EstimateDirectoryByteSize(full);
        if (folderBytes >= VvfatUsbCapacityBytes)
        {
            throw new InvalidOperationException(
                $"Folder is too large for QEMU vvfat ({FormatMib(folderBytes)} ≥ " +
                $"{FormatMib(VvfatUsbCapacityBytes)} capacity). " +
                "Remove files or use a smaller share folder.");
        }

        string id = $"{VvfatDriveIdPrefix}{_nextVvfatDriveIndex++}";
        // Avoid duplicate list rows if a prior attach left the same id tracked.
        for (int i = _hotpluggedVvfatDrives.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_hotpluggedVvfatDrives[i].id, id, StringComparison.OrdinalIgnoreCase))
                _hotpluggedVvfatDrives.RemoveAt(i);
        }

        string fileSpec = $"fat:rw:{full.Replace('\\', '/')}";
        string driveOpts =
            $"if=none,id={id},file={QuoteDriveFileValue(fileSpec)},format=raw";

        string usbBus = await EnsureVvfatDriveUsbBusAsync();
        try
        {
            await RunHmpOrThrowAsync($"drive_add 0 {driveOpts}");
        }
        catch (Exception e)
        {
            throw AnnotateVvfatDriveAddFailure(e, folderBytes);
        }

        try
        {
            await RunHmpOrThrowAsync(
                $"device_add usb-storage,id={id},drive={id},removable=on,bus={usbBus}");
        }
        catch (Exception e)
        {
            try { await RunHmpOrThrowAsync($"drive_del {id}"); }
            catch (Exception cleanup)
            {
                Debug.LogWarning($"drive_del cleanup after failed device_add: {cleanup.Message}");
            }
            throw AnnotateVvfatDeviceAddFailure(e, id);
        }

        _hotpluggedVvfatDrives.Add(new HotpluggedVvfatDrive { id = id, folderPath = full });
        Debug.Log(
            $"Attached USB vvfat drive {id} on {usbBus} " +
            $"({FormatMib(folderBytes)} / {FormatMib(VvfatUsbCapacityBytes)}): {fileSpec}");
        return id;
    }

    /// <summary>
    /// Prefer a dedicated EHCI controller for vvfat USB storage so hotplug does not
    /// sit on the same UHCI root hub as <c>usb-tablet</c> (which breaks guest mouse).
    /// Adds <c>usb-ehci</c> at runtime when the VM was started without it, and records
    /// it on EffectiveLaunchConfig (with PCI addr when known) so durable restore matches.
    /// </summary>
    async Task<string> EnsureVvfatDriveUsbBusAsync()
    {
        string preferredId = virtualMachine?.EffectiveLaunchConfig?.ResolvedUsbEhciId
            ?? LaunchConfig.DefaultUsbEhciId;
        string qtree = await virtualMachine.RunHumanMonitorCommandAsync("info qtree") ?? "";
        if (qtree.IndexOf(preferredId, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            PersistVvfatEhciLaunchArg(qtree, preferredId, enable: false);
            return $"{preferredId}.0";
        }

        // Any existing EHCI is still better than sharing UHCI with the tablet.
        var ehciId = Regex.Match(
            qtree,
            @"dev:\s*usb-ehci,\s*id\s*""([^""]+)""",
            RegexOptions.IgnoreCase);
        if (ehciId.Success)
        {
            string existingId = ehciId.Groups[1].Value;
            PersistVvfatEhciLaunchArg(qtree, existingId, enable: false);
            return $"{existingId}.0";
        }

        try
        {
            await RunHmpOrThrowAsync($"device_add usb-ehci,id={preferredId}");
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "Could not add a USB EHCI controller for vvfat drive hotplug. " +
                "Without it, storage shares UHCI with usb-tablet and often breaks the mouse. " +
                "Enable USB EHCI on Launch Config and restart the VM. " +
                $"HMP error: {e.Message}",
                e);
        }

        qtree = await virtualMachine.RunHumanMonitorCommandAsync("info qtree") ?? "";
        PersistVvfatEhciLaunchArg(qtree, preferredId, enable: true);
        return $"{preferredId}.0";
    }

    /// <summary>
    /// Write EHCI id/addr into EffectiveLaunchConfig so the next durable snapshot
    /// restore includes the same EHCI instance that is live in QEMU.
    /// </summary>
    void PersistVvfatEhciLaunchArg(string qtree, string ehciId, bool enable)
    {
        if (virtualMachine == null || string.IsNullOrWhiteSpace(ehciId))
            return;

        string pciAddr = TryParseEhciPciAddr(qtree, ehciId);
        if (virtualMachine.RecordUsbEhciInEffectiveLaunchConfig(ehciId, pciAddr, enable))
        {
            string addr = LaunchConfig.NormalizePciAddrArg(pciAddr);
            Debug.Log(
                addr != null
                    ? $"Recorded USB EHCI id={ehciId}, addr={addr} on EffectiveLaunchConfig for durable save."
                    : $"Recorded USB EHCI id={ehciId} on EffectiveLaunchConfig for durable save.");
        }
    }

    /// <summary>
    /// From <c>info qtree</c>, find the PCI <c>addr = XX.0</c> under <c>dev: usb-ehci, id "…"</c>.
    /// </summary>
    static string TryParseEhciPciAddr(string qtree, string ehciId)
    {
        if (string.IsNullOrEmpty(qtree) || string.IsNullOrWhiteSpace(ehciId))
            return null;

        // Match the device block, then the first addr = NN.0 inside it (before the next "dev:").
        var block = Regex.Match(
            qtree,
            $@"dev:\s*usb-ehci,\s*id\s*""{Regex.Escape(ehciId)}""(?<body>.*?)(?=\n\s*dev:|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!block.Success)
            return null;

        var addr = Regex.Match(
            block.Groups["body"].Value,
            @"addr\s*=\s*([0-9a-fA-F]{1,2}\.0)",
            RegexOptions.IgnoreCase);
        return addr.Success ? addr.Groups[1].Value : null;
    }

    /// <summary>
    /// Hot-detach a vvfat drive previously attached via <see cref="AttachVvfatDriveAsync"/>.
    /// Idempotent: already-removed USB devices / drives (guest eject, prior detach, stale
    /// session tracking) are treated as success so snapshot save is not blocked.
    /// </summary>
    public async Task DetachVvfatDriveAsync(string hotplugId)
    {
        RequireQmp();
        if (string.IsNullOrWhiteSpace(hotplugId))
            throw new ArgumentException("Hotplug id required", nameof(hotplugId));

        string id = hotplugId.Trim();
        HotpluggedVvfatDrive tracked = FindTrackedVvfat(id);

        try
        {
            await RunHmpOrThrowAsync($"device_del {id}");
        }
        catch (Exception e) when (IsHmpNotFoundReply(e.Message))
        {
            // Already gone — still drop tracking below.
        }

        try
        {
            await RunHmpOrThrowAsync($"drive_del {id}");
        }
        catch (Exception e) when (IsHmpNotFoundReply(e.Message))
        {
            // device_del often releases the drive; bare drive_del then says not found.
        }
        catch (Exception e)
        {
            Debug.LogWarning($"drive_del {id}: {e.Message}");
        }

        if (tracked != null)
            _hotpluggedVvfatDrives.Remove(tracked);

        Debug.Log($"Detached USB vvfat drive {id}");
    }

    static bool IsHmpNotFoundReply(string message) =>
        !string.IsNullOrEmpty(message) &&
        message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Folder paths of USB vvfat drives hotplugged this session (may be empty).</summary>
    public IReadOnlyList<string> GetHotpluggedVvfatFolderPaths()
    {
        var paths = new List<string>(_hotpluggedVvfatDrives.Count);
        foreach (HotpluggedVvfatDrive drive in _hotpluggedVvfatDrives)
            paths.Add(drive.folderPath);
        return paths;
    }

    /// <summary>
    /// Reconcile session tracking with live QEMU, then return folder paths still attached.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetHotpluggedVvfatFolderPathsAsync()
    {
        await ReconcileVvfatTrackingAsync();
        return GetHotpluggedVvfatFolderPaths();
    }

    /// <summary>Detach every USB vvfat drive hotplugged this session (most recent first).</summary>
    public async Task DetachAllVvfatDrivesAsync()
    {
        await ReconcileVvfatTrackingAsync();
        if (_hotpluggedVvfatDrives.Count == 0)
            return;

        var ids = new string[_hotpluggedVvfatDrives.Count];
        for (int i = 0; i < _hotpluggedVvfatDrives.Count; i++)
            ids[_hotpluggedVvfatDrives.Count - 1 - i] = _hotpluggedVvfatDrives[i].id;

        foreach (string id in ids)
            await DetachVvfatDriveAsync(id);
    }

    /// <summary>
    /// Drop tracking for devices/drives QEMU no longer has, and adopt any live
    /// <c>uqvv*</c> devices that Unity lost track of (domain reload / failed detach).
    /// </summary>
    async Task ReconcileVvfatTrackingAsync()
    {
        if (virtualMachine == null || !virtualMachine.QmpConnected)
        {
            ClearVvfatSessionTracking();
            return;
        }

        string qtree = await virtualMachine.RunHumanMonitorCommandAsync("info qtree") ?? "";
        string blocks = await virtualMachine.RunHumanMonitorCommandAsync("info block") ?? "";

        for (int i = _hotpluggedVvfatDrives.Count - 1; i >= 0; i--)
        {
            string id = _hotpluggedVvfatDrives[i].id;
            if (!QemuReportsVvfatId(qtree, blocks, id))
                _hotpluggedVvfatDrives.RemoveAt(i);
        }

        foreach (Match m in Regex.Matches(
                     qtree,
                     @"id\s*""(" + Regex.Escape(VvfatDriveIdPrefix) + @"\d+)""",
                     RegexOptions.IgnoreCase))
        {
            string id = m.Groups[1].Value;
            if (FindTrackedVvfat(id) != null)
                continue;

            string folder = TryParseVvfatFolderFromInfoBlock(blocks, id) ?? "(unknown folder)";
            _hotpluggedVvfatDrives.Add(new HotpluggedVvfatDrive { id = id, folderPath = folder });
        }

        int maxIndex = -1;
        foreach (HotpluggedVvfatDrive drive in _hotpluggedVvfatDrives)
        {
            if (drive.id != null &&
                drive.id.StartsWith(VvfatDriveIdPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(drive.id.Substring(VvfatDriveIdPrefix.Length), out int n) &&
                n > maxIndex)
                maxIndex = n;
        }
        if (maxIndex + 1 > _nextVvfatDriveIndex)
            _nextVvfatDriveIndex = maxIndex + 1;
    }

    HotpluggedVvfatDrive FindTrackedVvfat(string id)
    {
        foreach (HotpluggedVvfatDrive drive in _hotpluggedVvfatDrives)
        {
            if (string.Equals(drive.id, id, StringComparison.OrdinalIgnoreCase))
                return drive;
        }
        return null;
    }

    static bool QemuReportsVvfatId(string qtree, string blocks, string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        string needle = $"id \"{id}\"";
        if (!string.IsNullOrEmpty(qtree) &&
            qtree.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        // info block headers look like: uqvv0: ... or uqvv0 (#blockNNN):
        if (!string.IsNullOrEmpty(blocks) &&
            Regex.IsMatch(
                blocks,
                @"^\s*" + Regex.Escape(id) + @"\s*(\(|:)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline))
            return true;
        return false;
    }

    static string TryParseVvfatFolderFromInfoBlock(string blocks, string id)
    {
        if (string.IsNullOrEmpty(blocks) || string.IsNullOrEmpty(id))
            return null;

        var block = Regex.Match(
            blocks,
            @"^\s*" + Regex.Escape(id) + @"\s*(?:\([^)]*\))?:\s*(?<body>.*?)(?=^\S|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
        if (!block.Success)
            return null;

        var file = Regex.Match(
            block.Groups["body"].Value,
            @"file\s*=\s*(?<path>\S+)",
            RegexOptions.IgnoreCase);
        if (!file.Success)
            return null;

        return NormalizeInsertedFilePath(file.Groups["path"].Value.Trim().Trim('"'));
    }

    public Task<string[]> ListCdromDevicesAsync() =>
        ListDevicesAsync(
            () => virtualMachine.QueryCdromDeviceNamesAsync(),
            ParseCdromDevicesFromInfoBlock, "CD");

    public Task<string[]> ListFloppyDevicesAsync() =>
        ListDevicesAsync(
            () => virtualMachine.QueryFloppyDeviceNamesAsync(),
            ParseFloppyDevicesFromInfoBlock, "floppy");

    Task<string> ResolveCdromDeviceAsync() =>
        FirstDeviceAsync(
            ListCdromDevicesAsync,
            "No CD-ROM block device found (an empty tray is normally reserved at boot). " +
            "Is the VM running?");

    Task<string> ResolveFloppyDeviceAsync() =>
        FirstDeviceAsync(
            ListFloppyDevicesAsync,
            "No floppy block device found (an empty tray is normally reserved at boot). " +
            "Is the VM running?");

    void RequireQmp()
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
    }

    /// <summary>Mutating HMP via <see cref="VirtualMachine.RunHumanMonitorCommandOrThrowAsync"/>.</summary>
    Task RunHmpOrThrowAsync(string commandLine) =>
        virtualMachine.RunHumanMonitorCommandOrThrowAsync(commandLine);

    static Exception AnnotateVvfatDriveAddFailure(Exception e, long folderBytes)
    {
        string msg = e?.Message ?? "";
        if (msg.IndexOf("does not fit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            msg.IndexOf("FAT16", StringComparison.OrdinalIgnoreCase) >= 0 ||
            msg.IndexOf("FAT32", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new InvalidOperationException(
                $"vvfat drive_add failed — folder contents do not fit in QEMU's ~" +
                $"{FormatMib(VvfatUsbCapacityBytes)} vvfat image " +
                $"(folder ≈ {FormatMib(folderBytes)}; FAT overhead can push it over). " +
                "Remove files or use a smaller share.\n\n" + msg,
                e);
        }

        return new InvalidOperationException($"vvfat drive_add failed:\n{msg}", e);
    }

    static Exception AnnotateVvfatDeviceAddFailure(Exception e, string driveId)
    {
        string msg = e?.Message ?? "";
        if (msg.IndexOf("can't find value", StringComparison.OrdinalIgnoreCase) >= 0 ||
            msg.IndexOf("could not find", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new InvalidOperationException(
                $"vvfat device_add failed — block drive '{driveId}' was not created " +
                $"(drive_add likely failed silently or was cleaned up).\n\n{msg}",
                e);
        }

        return new InvalidOperationException($"vvfat device_add failed:\n{msg}", e);
    }

    /// <summary>Sum of file lengths under <paramref name="root"/> (best-effort; skips unreadable entries).</summary>
    static long EstimateDirectoryByteSize(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception) { /* skip locked / gone files */ }
                }
                foreach (string child in Directory.EnumerateDirectories(dir))
                    pending.Push(child);
            }
            catch (Exception)
            {
                // Skip unreadable subtrees; QEMU will still validate on open.
            }
        }
        return total;
    }

    static string FormatMib(long bytes) =>
        $"{bytes / (1024.0 * 1024.0):0.##} MiB";

    Task ChangeMediaAsync(string device, string mediaSpec) =>
        RunHmpOrThrowAsync($"change {device} {QuoteHmpPath(mediaSpec)}");

    /// <summary>Device names via QMP query-block, falling back to parsing HMP `info block`.</summary>
    async Task<string[]> ListDevicesAsync(
        Func<Task<string[]>> qmpQuery, Func<string, string[]> parseInfoBlock, string label)
    {
        if (virtualMachine == null || !virtualMachine.QmpConnected)
            return Array.Empty<string>();

        try
        {
            var fromQmp = await qmpQuery();
            if (fromQmp != null && fromQmp.Length > 0)
                return fromQmp;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"query-block for {label} devices failed, falling back to info block: {e.Message}");
        }

        string info = await virtualMachine.RunHumanMonitorCommandAsync("info block");
        return parseInfoBlock(info ?? "");
    }

    static async Task<string> FirstDeviceAsync(Func<Task<string[]>> list, string missingMessage)
    {
        string[] devices = await list();
        if (devices.Length == 0)
            throw new InvalidOperationException(missingMessage);
        return devices[0];
    }

    void TryAddFloppyAssetToLaunchConfig(string fullFilesystemPath)
    {
        if (!alsoAddToLaunchConfig)
            return;

        FloppyAsset asset = FindExistingFloppyAsset(fullFilesystemPath);
        if (asset == null)
        {
            Debug.Log(
                "Floppy inserted by path only (not a project FloppyAsset) — " +
                "EffectiveLaunchConfig was left unchanged. Drop a .img/.ima under Assets to persist it.");
            return;
        }

        if (virtualMachine.AddFloppyToEffectiveLaunchConfig(asset))
            Debug.Log(
                $"Added '{asset.DisplayLabel}' to EffectiveLaunchConfig floppies " +
                "(will persist on next durable save).");
    }

    /// <summary>
    /// Quote a <c>file=</c> value for HMP <c>drive_add</c> when it contains spaces/commas.
    /// </summary>
    static string QuoteDriveFileValue(string fileSpec)
    {
        if (fileSpec.IndexOfAny(new[] { ' ', '\t', ',', '"' }) < 0)
            return fileSpec;
        return "\"" + fileSpec.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Strip QEMU vvfat / fat: prefixes so path lookup matches Assets.
    /// Examples: <c>fat:floppy:ro:C:/foo</c>, <c>C:\bar.iso</c>.
    /// </summary>
    static string NormalizeInsertedFilePath(string insertedFile)
    {
        if (string.IsNullOrWhiteSpace(insertedFile))
            return insertedFile;

        string s = insertedFile.Trim();
        // Longest-first so "fat:floppy:ro:" wins over "fat:".
        string[] fatPrefixes =
        {
            "fat:floppy:ro:",
            "fat:floppy:rw:",
            "fat:floppy:",
            "fat:ro:",
            "fat:rw:",
            "fat:",
        };
        foreach (string prefix in fatPrefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(prefix.Length);
                break;
            }
        }

        try { return Path.GetFullPath(s); }
        catch { return s; }
    }

    static FloppyAsset FindExistingFloppyAsset(string fullImgPath)
    {
        FloppyAsset existing = FloppyAsset.FindByFilesystemPath(fullImgPath);
        if (existing != null)
            return existing;

        string projectRelative = TryGetProjectRelativePath(fullImgPath);
        if (projectRelative == null)
            return null;

        return AssetDatabase.LoadAssetAtPath<FloppyAsset>(projectRelative);
    }

    static CdRomAsset FindExistingCdRomAsset(string fullIsoPath)
    {
        CdRomAsset existing = CdRomAsset.FindByFilesystemPath(fullIsoPath);
        if (existing != null)
            return existing;

        string projectRelative = TryGetProjectRelativePath(fullIsoPath);
        if (projectRelative == null)
            return null;

        return AssetDatabase.LoadAssetAtPath<CdRomAsset>(projectRelative);
    }

    static string TryGetProjectRelativePath(string fullFilesystemPath)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(fullFilesystemPath);
        if (!full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        return full.Substring(projectRoot.Length).Replace('\\', '/');
    }

    static string QuoteHmpPath(string path)
    {
        if (path.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return path;
        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    static string[] ParseCdromDevicesFromInfoBlock(string infoBlock) =>
        ParseDevicesFromInfoBlock(infoBlock, (name, block) =>
            name.IndexOf("cd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            block.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0 ||
            block.IndexOf("cdrom", StringComparison.OrdinalIgnoreCase) >= 0 ||
            string.Equals(name, VirtualMachine.EmptyCdromDriveId, StringComparison.OrdinalIgnoreCase));

    static string[] ParseFloppyDevicesFromInfoBlock(string infoBlock) =>
        ParseDevicesFromInfoBlock(infoBlock, (name, block) =>
            string.Equals(name, VirtualMachine.EmptyFloppyDriveId, StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.StartsWith("fd", StringComparison.OrdinalIgnoreCase) ||
            block.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// Device names from HMP <c>info block</c> output whose header name / body text
    /// satisfy <paramref name="matches"/>(name, block).
    /// </summary>
    static string[] ParseDevicesFromInfoBlock(string infoBlock, Func<string, string, bool> matches)
    {
        var devices = new List<string>();
        if (string.IsNullOrEmpty(infoBlock))
            return devices.ToArray();

        var header = new Regex(@"^(\S+):\s", RegexOptions.Multiline);
        MatchCollection headers = header.Matches(infoBlock);
        for (int i = 0; i < headers.Count; i++)
        {
            string name = headers[i].Groups[1].Value;
            int start = headers[i].Index;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : infoBlock.Length;
            string block = infoBlock.Substring(start, end - start);
            if (matches(name, block) && !devices.Contains(name))
                devices.Add(name);
        }

        return devices.ToArray();
    }
#endif
}
}
