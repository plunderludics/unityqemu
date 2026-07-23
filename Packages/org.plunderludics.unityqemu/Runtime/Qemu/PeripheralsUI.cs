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
/// (insert/eject ISO or floppy via QEMU HMP <c>change</c> / <c>eject</c>).
/// <para>
/// Floppy hotplug is for tiny (~1.44MB) drop-ins. Larger shares: Launch Config
/// <see cref="LaunchConfig.hostFolders"/> (vvfat IDE) or
/// <see cref="LaunchConfig.smbShareFolder"/> (live-ish SMB at \\10.0.2.4\qemu).
/// </para>
/// </summary>
[ExecuteAlways]
[DeclareHorizontalGroup("cdrom/actions")]
[DeclareHorizontalGroup("floppy/actions")]
[DeclareFoldoutGroup("Debug", Expanded = false)]
public class PeripheralsUI : MonoBehaviour
{
    public VirtualMachine virtualMachine;

    [Tooltip(
        "QEMU block device name for the CD tray (e.g. ide1-cd0). " +
        "Leave empty to auto-detect from query-block / info block.")]
    public string cdromDevice = "";

    [Tooltip(
        "QEMU block device name for the floppy tray (e.g. floppy0 / unityqemu-fd0). " +
        "Leave empty to auto-detect from query-block / info block.")]
    public string floppyDevice = "";

    [Tooltip(
        "If the chosen media is already a project asset (CdRomAsset / folder / .img), " +
        "also append it to EffectiveLaunchConfig (uqsnap metadata when locked, otherwise " +
        "the VM launchConfig) so the next durable save records the insert. " +
        "Paths outside the project are inserted by path only.")]
    public bool alsoAddToLaunchConfig = true;

#if UNITY_EDITOR
    [ShowInInspector, ReadOnly]
    bool QmpReady => virtualMachine != null && virtualMachine.QmpConnected;

    [ShowInInspector, ReadOnly]
    [LabelText("Resolved CD")]
    string ResolvedCdromDevice =>
        !string.IsNullOrWhiteSpace(cdromDevice) ? cdromDevice.Trim() : "(auto)";

    [ShowInInspector, ReadOnly]
    [LabelText("Resolved Floppy")]
    string ResolvedFloppyDevice =>
        !string.IsNullOrWhiteSpace(floppyDevice) ? floppyDevice.Trim() : "(auto)";

    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly, TextArea(4, 12)]
    string status = "Idle";

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    [Group("cdrom/actions")]
    [Button("Insert ISO…")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertIsoButton()
    {
        string path = EditorUtility.OpenFilePanel("Choose ISO", "", "iso");
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            status = "Inserting ISO…";
            CdRomAsset asset = await InsertIsoAsync(path);
            status = asset != null
                ? $"Inserted '{asset.DisplayLabel}'"
                : $"Inserted '{Path.GetFileName(path)}'";
        }
        catch (Exception e)
        {
            status = $"Insert ISO failed: {e.Message}";
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
            status = "Ejecting CD…";
            await EjectCdromAsync();
            status = "CD ejected";
        }
        catch (Exception e)
        {
            status = $"Eject CD failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [Button("Refresh CD devices")]
    [EnableIf(nameof(QmpReady))]
    public async void RefreshCdromDevicesButton()
    {
        try
        {
            string[] devices = await ListCdromDevicesAsync();
            if (devices.Length == 0)
            {
                status = "No removable/CD block devices found — is the VM running?\n" +
                         "(An empty CD tray is reserved automatically at boot.)";
                return;
            }

            if (string.IsNullOrWhiteSpace(cdromDevice))
                cdromDevice = devices[0];

            status = "CD / removable devices:\n- " + string.Join("\n- ", devices);
        }
        catch (Exception e)
        {
            status = $"Refresh CD failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [Group("floppy/actions")]
    [Button("Insert floppy…")]
    [EnableIf(nameof(QmpReady))]
    public async void InsertFloppyButton()
    {
        int choice = EditorUtility.DisplayDialogComplex(
            "Insert floppy",
            "Choose an image file (.img/.ima) or a project folder (vvfat ~1.44MB).",
            "Image file…",
            "Cancel",
            "Folder…");
        if (choice == 1)
            return;

        try
        {
            if (choice == 0)
            {
                string path = EditorUtility.OpenFilePanel("Choose floppy image", "", "img,ima");
                if (string.IsNullOrEmpty(path))
                    return;
                status = "Inserting floppy…";
                await InsertFloppyImageAsync(path);
                status = $"Inserted floppy '{Path.GetFileName(path)}'";
            }
            else
            {
                string folder = EditorUtility.OpenFolderPanel("Choose floppy folder (vvfat)", "", "");
                if (string.IsNullOrEmpty(folder))
                    return;
                status = "Inserting floppy folder…";
                await InsertFloppyFolderAsync(folder);
                status = $"Inserted floppy folder '{Path.GetFileName(folder)}'";
            }
        }
        catch (Exception e)
        {
            status = $"Insert floppy failed: {e.Message}";
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
            status = "Ejecting floppy…";
            await EjectFloppyAsync();
            status = "Floppy ejected";
        }
        catch (Exception e)
        {
            status = $"Eject floppy failed: {e.Message}";
            Debug.LogException(e);
        }
    }

    [Button("Refresh floppy devices")]
    [EnableIf(nameof(QmpReady))]
    public async void RefreshFloppyDevicesButton()
    {
        try
        {
            string[] devices = await ListFloppyDevicesAsync();
            if (devices.Length == 0)
            {
                status = "No floppy block devices found — is the VM running?\n" +
                         "(An empty floppy tray is reserved automatically at boot.)";
                return;
            }

            if (string.IsNullOrWhiteSpace(floppyDevice))
                floppyDevice = devices[0];

            status = "Floppy devices:\n- " + string.Join("\n- ", devices);
        }
        catch (Exception e)
        {
            status = $"Refresh floppy failed: {e.Message}";
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
        await RunHmpOrThrowAsync($"eject -f {await ResolveCdromDeviceAsync()}");
    }

    /// <summary>
    /// Hot-insert a floppy image via HMP <c>change</c>.
    /// Project assets may also be appended to EffectiveLaunchConfig.
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

        TryAddFloppySourceToLaunchConfig(full);
        Debug.Log($"Inserted floppy image into {device}: {qemuPath}");
    }

    /// <summary>
    /// Hot-insert a host folder as a vvfat floppy (<c>fat:floppy:ro:…</c>, ~1.44MB).
    /// Read-only so it stays compatible with savevm/loadvm.
    /// Project folders may also be appended to EffectiveLaunchConfig.
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

        TryAddFloppySourceToLaunchConfig(full);
        Debug.Log($"Inserted floppy folder into {device}: {fileSpec}");
    }

    public async Task EjectFloppyAsync()
    {
        RequireQmp();
        await RunHmpOrThrowAsync($"eject -f {await ResolveFloppyDeviceAsync()}");
    }

    public Task<string[]> ListCdromDevicesAsync() =>
        ListDevicesAsync(
            () => virtualMachine.QueryCdromDeviceNamesAsync(),
            ParseCdromDevicesFromInfoBlock, "CD");

    public Task<string[]> ListFloppyDevicesAsync() =>
        ListDevicesAsync(
            () => virtualMachine.QueryFloppyDeviceNamesAsync(),
            ParseFloppyDevicesFromInfoBlock, "floppy");

    async Task<string> ResolveCdromDeviceAsync()
    {
        if (!string.IsNullOrWhiteSpace(cdromDevice))
            return cdromDevice.Trim();
        cdromDevice = await FirstDeviceAsync(
            ListCdromDevicesAsync,
            "No CD-ROM block device found (an empty tray is normally reserved at boot). " +
            "Is the VM running? You can also set Cdrom Device manually (see Refresh CD devices).");
        return cdromDevice;
    }

    async Task<string> ResolveFloppyDeviceAsync()
    {
        if (!string.IsNullOrWhiteSpace(floppyDevice))
            return floppyDevice.Trim();
        floppyDevice = await FirstDeviceAsync(
            ListFloppyDevicesAsync,
            "No floppy block device found (an empty tray is normally reserved at boot). " +
            "Is the VM running? You can also set Floppy Device manually (see Refresh floppy devices).");
        return floppyDevice;
    }

    void RequireQmp()
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        if (!virtualMachine.QmpConnected)
            throw new InvalidOperationException("QMP not connected");
    }

    /// <summary>Run an HMP command and throw if the reply mentions an error.</summary>
    async Task RunHmpOrThrowAsync(string commandLine)
    {
        string result = await virtualMachine.RunHumanMonitorCommandAsync(commandLine);
        if (!string.IsNullOrWhiteSpace(result) &&
            result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new InvalidOperationException(result.Trim());
    }

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

    void TryAddFloppySourceToLaunchConfig(string fullFilesystemPath)
    {
        if (!alsoAddToLaunchConfig)
            return;

        UnityEngine.Object source = FindExistingProjectObject(fullFilesystemPath);
        if (source == null)
        {
            Debug.Log(
                "Floppy inserted by path only (not under Assets) — " +
                "EffectiveLaunchConfig was left unchanged. Keep the image/folder under Assets to persist it.");
            return;
        }

        if (virtualMachine.AddFloppyToEffectiveLaunchConfig(source))
            Debug.Log($"Added '{source.name}' to EffectiveLaunchConfig floppies (will persist on next durable save).");
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

    static UnityEngine.Object FindExistingProjectObject(string fullFilesystemPath)
    {
        string projectRelative = TryGetProjectRelativePath(fullFilesystemPath);
        if (projectRelative == null)
            return null;

        if (AssetDatabase.IsValidFolder(projectRelative))
            return AssetDatabase.LoadAssetAtPath<DefaultAsset>(projectRelative);

        return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(projectRelative);
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
#else
    [PropertyOrder(100)]
    [Group("Debug")]
    [ShowInInspector, ReadOnly]
    string status = "Idle";
#endif
}
}
