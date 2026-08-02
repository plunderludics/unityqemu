#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Hot-plug removable media on a running guest (CD / floppy / USB vvfat).
/// Editor-only; inspector entry points live on <see cref="PeripheralsUI"/>.
/// </summary>
public partial class VirtualMachine
{
    const string VvfatDriveIdPrefix = "uqvv";

    /// <summary>
    /// QEMU non-floppy vvfat default capacity (CHS 1024×16×63). Folder contents must fit
    /// under this; FAT metadata eats a little more, so treat it as a hard ceiling.
    /// </summary>
    const long VvfatUsbCapacityBytes = 504L * 1024 * 1024;

    public readonly struct HotpluggedVvfatInfo
    {
        public HotpluggedVvfatInfo(string id, string folderPath)
        {
            Id = id;
            FolderPath = folderPath;
        }

        public string Id { get; }
        public string FolderPath { get; }
    }

    sealed class HotpluggedVvfatDrive
    {
        public string id;
        public string folderPath;
    }

    readonly List<HotpluggedVvfatDrive> _hotpluggedVvfatDrives = new List<HotpluggedVvfatDrive>();
    int _nextVvfatDriveIndex;

    void ClearVvfatSessionTracking()
    {
        _hotpluggedVvfatDrives.Clear();
        _nextVvfatDriveIndex = 0;
    }

    /// <summary>
    /// Hot-insert an ISO via HMP <c>change</c>. Uses the filesystem path as-is (no copy).
    /// When <paramref name="alsoAddToLaunchConfig"/> is on and the ISO is already a project
    /// <see cref="CdRomAsset"/>, that asset is appended to launch config.
    /// </summary>
    public async Task<CdRomAsset> InsertIsoAsync(
        string isoFilesystemPath, bool alsoAddToLaunchConfig = true)
    {
        RequireQmpConnected();
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
                if (AddCdRomToEffectiveLaunchConfig(asset))
                    Debug.Log(
                        $"Added '{asset.DisplayLabel}' to EffectiveLaunchConfig " +
                        "(will persist on next durable save).");
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

    public async Task EjectCdromAsync(bool alsoUpdateLaunchConfig = true)
    {
        RequireQmpConnected();
        string device = await ResolveCdromDeviceAsync();
        string insertedFile = null;
        if (alsoUpdateLaunchConfig)
        {
            try { insertedFile = await TryGetInsertedBlockFileAsync(device); }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not query inserted CD before eject: {e.Message}");
            }
        }

        await RunHumanMonitorCommandOrThrowAsync($"eject -f {device}");

        if (alsoUpdateLaunchConfig && !string.IsNullOrEmpty(insertedFile))
        {
            CdRomAsset asset = FindExistingCdRomAsset(NormalizeInsertedFilePath(insertedFile));
            if (asset != null && RemoveCdRomFromEffectiveLaunchConfig(asset))
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
    public async Task InsertFloppyImageAsync(
        string imageFilesystemPath, bool alsoAddToLaunchConfig = true)
    {
        RequireQmpConnected();
        if (string.IsNullOrWhiteSpace(imageFilesystemPath) || !File.Exists(imageFilesystemPath))
            throw new FileNotFoundException("Floppy image not found", imageFilesystemPath);

        string full = Path.GetFullPath(imageFilesystemPath);
        string device = await ResolveFloppyDeviceAsync();
        string qemuPath = full.Replace('\\', '/');
        await ChangeMediaAsync(device, qemuPath);

        TryAddFloppyAssetToLaunchConfig(full, alsoAddToLaunchConfig);
        Debug.Log($"Inserted floppy image into {device}: {qemuPath}");
    }

    /// <summary>
    /// Hot-insert a folder as a vvfat floppy (<c>fat:floppy:ro:…</c>, ~1.44MB).
    /// Read-only so it stays compatible with savevm/loadvm. Session-only (not Launch Config).
    /// </summary>
    public async Task InsertFloppyFolderAsync(string folderFilesystemPath)
    {
        RequireQmpConnected();
        if (string.IsNullOrWhiteSpace(folderFilesystemPath) || !Directory.Exists(folderFilesystemPath))
            throw new DirectoryNotFoundException($"Floppy folder not found: {folderFilesystemPath}");

        string full = Path.GetFullPath(folderFilesystemPath);
        string device = await ResolveFloppyDeviceAsync();
        string fileSpec = $"fat:floppy:ro:{full.Replace('\\', '/')}";
        await ChangeMediaAsync(device, fileSpec);

        Debug.Log($"Inserted floppy folder into {device}: {fileSpec}");
    }

    public async Task EjectFloppyAsync(bool alsoUpdateLaunchConfig = true)
    {
        RequireQmpConnected();
        string device = await ResolveFloppyDeviceAsync();
        string insertedFile = null;
        if (alsoUpdateLaunchConfig)
        {
            try { insertedFile = await TryGetInsertedBlockFileAsync(device); }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not query inserted floppy before eject: {e.Message}");
            }
        }

        await RunHumanMonitorCommandOrThrowAsync($"eject -f {device}");

        if (alsoUpdateLaunchConfig && !string.IsNullOrEmpty(insertedFile))
        {
            FloppyAsset asset = FindExistingFloppyAsset(NormalizeInsertedFilePath(insertedFile));
            if (asset != null && RemoveFloppyFromEffectiveLaunchConfig(asset))
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
    /// re-enumerated. Writable vvfat blocks migrate/savevm until disconnected.
    /// Host folder must fit in QEMU's default vvfat image (~504 MiB).
    /// </summary>
    public async Task<string> AttachVvfatDriveAsync(string folderFilesystemPath)
    {
        RequireQmpConnected();
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
            await RunHumanMonitorCommandOrThrowAsync($"drive_add 0 {driveOpts}");
        }
        catch (Exception e)
        {
            throw AnnotateVvfatDriveAddFailure(e, folderBytes);
        }

        try
        {
            await RunHumanMonitorCommandOrThrowAsync(
                $"device_add usb-storage,id={id},drive={id},removable=on,bus={usbBus}");
        }
        catch (Exception e)
        {
            try { await RunHumanMonitorCommandOrThrowAsync($"drive_del {id}"); }
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
    /// Hot-detach a vvfat drive previously attached via <see cref="AttachVvfatDriveAsync"/>.
    /// Idempotent: already-removed USB devices / drives are treated as success.
    /// </summary>
    public async Task DetachVvfatDriveAsync(string hotplugId)
    {
        RequireQmpConnected();
        if (string.IsNullOrWhiteSpace(hotplugId))
            throw new ArgumentException("Hotplug id required", nameof(hotplugId));

        string id = hotplugId.Trim();
        HotpluggedVvfatDrive tracked = FindTrackedVvfat(id);

        try
        {
            await RunHumanMonitorCommandOrThrowAsync($"device_del {id}");
        }
        catch (Exception e) when (IsHmpNotFoundReply(e.Message))
        {
            // Already gone — still drop tracking below.
        }

        try
        {
            await RunHumanMonitorCommandOrThrowAsync($"drive_del {id}");
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

    /// <summary>
    /// Reconcile session tracking with live QEMU, then return folder paths still attached.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetHotpluggedVvfatFolderPathsAsync()
    {
        IReadOnlyList<HotpluggedVvfatInfo> drives = await GetHotpluggedVvfatDrivesAsync();
        var paths = new List<string>(drives.Count);
        foreach (HotpluggedVvfatInfo drive in drives)
            paths.Add(drive.FolderPath);
        return paths;
    }

    /// <summary>
    /// Reconcile session tracking with live QEMU, then return attached drives (id + folder).
    /// </summary>
    public async Task<IReadOnlyList<HotpluggedVvfatInfo>> GetHotpluggedVvfatDrivesAsync()
    {
        await ReconcileVvfatTrackingAsync();
        var list = new List<HotpluggedVvfatInfo>(_hotpluggedVvfatDrives.Count);
        foreach (HotpluggedVvfatDrive drive in _hotpluggedVvfatDrives)
            list.Add(new HotpluggedVvfatInfo(drive.id, drive.folderPath));
        return list;
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

    public Task<string[]> ListCdromDevicesAsync() =>
        ListDevicesAsync(QueryCdromDeviceNamesAsync, ParseCdromDevicesFromInfoBlock, "CD");

    public Task<string[]> ListFloppyDevicesAsync() =>
        ListDevicesAsync(QueryFloppyDeviceNamesAsync, ParseFloppyDevicesFromInfoBlock, "floppy");

    async Task<string> EnsureVvfatDriveUsbBusAsync()
    {
        string preferredId = EffectiveLaunchConfig?.ResolvedUsbEhciId
            ?? LaunchConfig.DefaultUsbEhciId;
        string qtree = await RunHumanMonitorCommandAsync("info qtree") ?? "";
        if (qtree.IndexOf(preferredId, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            PersistVvfatEhciLaunchArg(qtree, preferredId, enable: false);
            return $"{preferredId}.0";
        }

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
            await RunHumanMonitorCommandOrThrowAsync($"device_add usb-ehci,id={preferredId}");
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

        qtree = await RunHumanMonitorCommandAsync("info qtree") ?? "";
        PersistVvfatEhciLaunchArg(qtree, preferredId, enable: true);
        return $"{preferredId}.0";
    }

    void PersistVvfatEhciLaunchArg(string qtree, string ehciId, bool enable)
    {
        if (string.IsNullOrWhiteSpace(ehciId))
            return;

        string pciAddr = TryParseEhciPciAddr(qtree, ehciId);
        if (RecordUsbEhciInEffectiveLaunchConfig(ehciId, pciAddr, enable))
        {
            string addr = LaunchConfig.NormalizePciAddrArg(pciAddr);
            Debug.Log(
                addr != null
                    ? $"Recorded USB EHCI id={ehciId}, addr={addr} on EffectiveLaunchConfig for durable save."
                    : $"Recorded USB EHCI id={ehciId} on EffectiveLaunchConfig for durable save.");
        }
    }

    static string TryParseEhciPciAddr(string qtree, string ehciId)
    {
        if (string.IsNullOrEmpty(qtree) || string.IsNullOrWhiteSpace(ehciId))
            return null;

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

    static bool IsHmpNotFoundReply(string message) =>
        !string.IsNullOrEmpty(message) &&
        message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;

    async Task ReconcileVvfatTrackingAsync()
    {
        if (!QmpConnected)
        {
            ClearVvfatSessionTracking();
            return;
        }

        string qtree = await RunHumanMonitorCommandAsync("info qtree") ?? "";
        string blocks = await RunHumanMonitorCommandAsync("info block") ?? "";

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

    void RequireQmpConnected()
    {
        if (!QmpConnected)
            throw new InvalidOperationException("QMP not connected");
    }

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
        RunHumanMonitorCommandOrThrowAsync($"change {device} {QuoteHmpPath(mediaSpec)}");

    async Task<string[]> ListDevicesAsync(
        Func<Task<string[]>> qmpQuery, Func<string, string[]> parseInfoBlock, string label)
    {
        if (!QmpConnected)
            return Array.Empty<string>();

        try
        {
            var fromQmp = await qmpQuery();
            if (fromQmp != null && fromQmp.Length > 0)
                return fromQmp;
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"query-block for {label} devices failed, falling back to info block: {e.Message}");
        }

        string info = await RunHumanMonitorCommandAsync("info block");
        return parseInfoBlock(info ?? "");
    }

    static async Task<string> FirstDeviceAsync(Func<Task<string[]>> list, string missingMessage)
    {
        string[] devices = await list();
        if (devices.Length == 0)
            throw new InvalidOperationException(missingMessage);
        return devices[0];
    }

    void TryAddFloppyAssetToLaunchConfig(string fullFilesystemPath, bool alsoAddToLaunchConfig)
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

        if (AddFloppyToEffectiveLaunchConfig(asset))
            Debug.Log(
                $"Added '{asset.DisplayLabel}' to EffectiveLaunchConfig floppies " +
                "(will persist on next durable save).");
    }

    static string QuoteDriveFileValue(string fileSpec)
    {
        if (fileSpec.IndexOfAny(new[] { ' ', '\t', ',', '"' }) < 0)
            return fileSpec;
        return "\"" + fileSpec.Replace("\"", "\\\"") + "\"";
    }

    static string NormalizeInsertedFilePath(string insertedFile)
    {
        if (string.IsNullOrWhiteSpace(insertedFile))
            return insertedFile;

        string s = insertedFile.Trim();
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
            string.Equals(name, EmptyCdromDriveId, StringComparison.OrdinalIgnoreCase));

    static string[] ParseFloppyDevicesFromInfoBlock(string infoBlock) =>
        ParseDevicesFromInfoBlock(infoBlock, (name, block) =>
            string.Equals(name, EmptyFloppyDriveId, StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.StartsWith("fd", StringComparison.OrdinalIgnoreCase) ||
            block.IndexOf("floppy", StringComparison.OrdinalIgnoreCase) >= 0);

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
}
}
#endif
