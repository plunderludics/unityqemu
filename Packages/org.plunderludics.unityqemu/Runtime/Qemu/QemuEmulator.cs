using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
public class QemuEmulator : MonoBehaviour
{
    Process _qemuProcess;
    QemuVncClient _vncClient;
    QemuQmpClient _qmpClient;
    QemuGdbClient _gdbClient;
    bool _starting;
    
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
    public bool showGui = false;
    public bool passKeyboardInputFromUnity = true;
    public bool passMouseInputFromUnity = true;

    [Tooltip("Run QEMU and stream the VNC texture while the editor is not in Play mode")]
    public bool runInEditMode = false;

    [Tooltip("Hard disk image path (project-relative, e.g. Assets/Qemu/qemu~/winXP/o1.qcow2)")]
    public string diskImagePath = "";

    [Tooltip("CD-ROM images — drag .iso assets here (each becomes -drive media=cdrom).")]
    public UnityEngine.Object[] cdroms;

    [Tooltip(
        "Floppy sources for quick guest file drop-in. " +
        "Drag a folder → fat:floppy:rw (vvfat; ~1.44MB); " +
        "or drag a .img/.ima. Index 0 = A:, 1 = B:, …")]
    public UnityEngine.Object[] floppies;

    public string saveStateName = "";

    [SerializeField] private int vncPort = 5900;
    [SerializeField] private int qmpPort = 4444;
    [SerializeField] private int gdbPort = 1234;
    [SerializeField] private bool gdbPhysicalMemory = true;
    [SerializeField] private RenderTexture outputTexture; // This is kind of unnecessary should just use _vncClient.Texture directly, ideally..

    [TextArea(3, 10)]
    public string qemuArgs = @"
    -m 64
    -cpu pentium
    -vga cirrus
    -device sb16,audiodev=snd0
    -audiodev dsound,id=snd0
    "; // VNC display + disk/cd/floppy args get added automatically
    // TODO: move necessary+common args into separate fields

    bool ShouldRun => Application.isPlaying || runInEditMode;
    bool IsRunning => _qemuProcess != null && !_qemuProcess.HasExited;

    /// <summary>Resolve a project-relative or absolute path for QEMU.</summary>
    static string ResolveProjectPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
    }

    string ResolveDiskImagePath() => ResolveProjectPath(diskImagePath);

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
    /// Folder → <c>fat:floppy:rw:...</c>; otherwise filesystem path to the asset.
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
            return $"fat:floppy:rw:{full.Replace('\\', '/')}";

        return full;
    }

    static void AppendCdromArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources)
    {
        if (sources == null)
            return;
        foreach (var source in sources)
        {
            string path = ResolveObjectFilesystemPath(source);
            if (string.IsNullOrEmpty(path))
                continue;
            args.Add("-drive");
            args.Add($"file={path},media=cdrom");
        }
    }

    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources)
    {
        if (sources == null)
            return;
        int index = 0;
        foreach (var source in sources)
        {
            string fileSpec = ResolveFloppyFileSpec(source);
            if (string.IsNullOrEmpty(fileSpec))
                continue;
            args.Add("-drive");
            args.Add($"file={fileSpec},if=ide,index={index},format=raw,media=disk");
            index++;
        }
    }
#else
    static void AppendCdromArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources) { }
    static void AppendFloppyArgs(System.Collections.Generic.IList<string> args, UnityEngine.Object[] sources) { }
#endif

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

    bool CanPauseResume => QmpConnected || GdbConnected;

    void OnEnable()
    {
#if UNITY_EDITOR
        // Avoid starting during the play-mode transition.
        if (EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying)
            return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
        EditorApplication.update -= EditorTick;
        EditorApplication.update += EditorTick;
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
        // ExecuteAlways Update is sparse in edit mode; drive texture blit every editor tick.
        if (!Application.isPlaying && runInEditMode && enabled && gameObject.activeInHierarchy)
        {
            Tick();
        }
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
        // Use Path.Combine to take advantage of unity's dark magic (somehow redirects to the actual package location in packagecache if needed)
        var qemuExe = Path.Combine("Packages", "org.plunderludics.unityqemu", "qemu~", "qemu-system-i386.exe");
        qemuExe = Path.GetFullPath(qemuExe);
        // UnityEngine.Debug.Log($"QEMU executable: {qemuExe}");

        var process = new Process();
        process.StartInfo.FileName = qemuExe;

        foreach (var arg in qemuArgs.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (showGui)
        {
            process.StartInfo.ArgumentList.Add("-display");
            process.StartInfo.ArgumentList.Add("sdl");
        }

        string hdaPath = ResolveDiskImagePath();
        if (!string.IsNullOrEmpty(hdaPath))
        {
            process.StartInfo.ArgumentList.Add("-hda");
            process.StartInfo.ArgumentList.Add(hdaPath);
        }

        AppendCdromArgs(process.StartInfo.ArgumentList, cdroms);
        AppendFloppyArgs(process.StartInfo.ArgumentList, floppies);

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

            if (!string.IsNullOrEmpty(saveStateName))
            {
                await LoadSaveStateAsync(saveStateName);
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
        TryAutoStart();
    }

    void Update() {
        if (Application.isPlaying)
            Tick();
    }

    void Tick()
    {
        if (_vncClient == null)
            return;

        _vncClient.Update();

        if (_vncClient.Texture != null && outputTexture != null)
        {
            Graphics.Blit(_vncClient.Texture, outputTexture);
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        // Unity Input is unreliable outside play mode.
        if (Application.isPlaying)
            HandleInput();
    }

    // TODO move into some kind of BasicInputProvider type of class
    void HandleInput()
    {
        if (!passKeyboardInputFromUnity && !passMouseInputFromUnity)
            return;

        if (_vncClient == null || _vncClient.Texture == null)
            return;

        if (passMouseInputFromUnity) {
            var texture = _vncClient.Texture;
            int vncWidth = texture.width;
            int vncHeight = texture.height;

            // Mouse input
            Vector3 mousePos = Input.mousePosition;
            
            // Convert Unity screen coordinates to VNC coordinates
            // Assuming the texture is displayed in a UI element or render texture
            // For now, map directly from screen space to VNC space
            // You may need to adjust this based on how you're displaying the texture


            int vncX = Mathf.Clamp((int)(mousePos.x * vncWidth / Screen.width), 0, vncWidth - 1);
            int vncY = Mathf.Clamp((int)(mousePos.y * vncHeight / Screen.height), 0, vncHeight - 1);
            
            // Flip Y coordinate (Unity has origin at bottom-left, VNC at top-left)
            vncY = vncHeight - 1 - vncY;

            bool leftButton = Input.GetMouseButton(0);
            bool middleButton = Input.GetMouseButton(2);
            bool rightButton = Input.GetMouseButton(1);
            
            SendMouseEvent(vncX, vncY, leftButton, middleButton, rightButton);
        }

        if (passKeyboardInputFromUnity) {
            foreach (KeyCode key in SpecialKeyCodes)
            {
                if (Input.GetKeyDown(key))
                    SendKeyEvent(key, true);
                if (Input.GetKeyUp(key))
                    SendKeyEvent(key, false);
            }

            // Letters/digits/space via KeyCode (hold + Ctrl/Alt chords).
            foreach (KeyCode key in LetterDigitSpaceKeyCodes)
            {
                if (Input.GetKeyDown(key))
                    SendKeyEvent(key, true);
                if (Input.GetKeyUp(key))
                    SendKeyEvent(key, false);
            }

            // Punctuation via inputString (layout-accurate). KeyCode.Slash is unreliable on
            // some OEM layouts; ASCII '/' here is the correct VNC keysym (0x2F).
            foreach (char c in Input.inputString)
            {
                if (c <= 0x1f || c == 0x7f)
                    continue; // control chars; Enter/Backspace/Tab handled above
                if (c == ' ' || char.IsLetterOrDigit(c))
                    continue; // KeyCode path
                // Shift+digit already sent as digit keysym + Shift; skip the shifted glyph.
                if (IsUsShiftedDigitChar(c))
                    continue;
                int keysym = CharToVncKeysym(c);
                if (keysym == 0)
                    continue;
                if (_vncClient == null || _vncClient.Texture == null)
                    continue;
                _vncClient.SendKeyEvent(keysym, true);
                _vncClient.SendKeyEvent(keysym, false);
            }
        }
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
        if (_vncClient == null || _vncClient.Texture == null) {
            UnityEngine.Debug.LogWarning("VNC client not connected");
            return;
        }

        _vncClient.SendKeyEvent(keysym, down);
    }

    // Keys that are not reliably represented by Input.inputString (or need hold semantics).
    static readonly KeyCode[] SpecialKeyCodes =
    {
        KeyCode.LeftShift, KeyCode.RightShift,
        KeyCode.LeftControl, KeyCode.RightControl,
        KeyCode.LeftAlt, KeyCode.RightAlt,
        KeyCode.LeftCommand, KeyCode.RightCommand,
        KeyCode.CapsLock, KeyCode.Numlock,
        KeyCode.Escape, KeyCode.Backspace, KeyCode.Delete,
        KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Tab,
        KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
        KeyCode.Insert, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
        KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6,
        KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        KeyCode.Print, KeyCode.ScrollLock, KeyCode.Pause,
        KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4,
        KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
        KeyCode.KeypadPeriod, KeyCode.KeypadDivide, KeyCode.KeypadMinus, KeyCode.KeypadPlus,
        KeyCode.KeypadMultiply,
    };

    // Letters / digits / space — KeyCode down/up for hold + modifier chords.
    static readonly KeyCode[] LetterDigitSpaceKeyCodes =
    {
        KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G, KeyCode.H,
        KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N, KeyCode.O, KeyCode.P,
        KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U, KeyCode.V, KeyCode.W, KeyCode.X,
        KeyCode.Y, KeyCode.Z,
        KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
        KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
        KeyCode.Space,
    };

    static bool IsUsShiftedDigitChar(char c) =>
        "!@#$%^&*()".IndexOf(c) >= 0;

    void OnDestroy()
    {
        // Fire-and-forget sync stop on destroy (can't await here reliably).
        StopQemu();
    }

    Task ConnectVncAsync()
    {
        _vncClient = new QemuVncClient();
        if (outputTexture == null)
        {
            outputTexture = new RenderTexture(640, 480, 0);
            outputTexture.name = "QEMU Output";
        }
        return ConnectVncCoreAsync(_vncClient, vncPort - 5900);
    }

    static async Task ConnectVncCoreAsync(QemuVncClient client, int display)
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
        _qmpClient = new QemuQmpClient { Verbose = verboseQmp };
        return ConnectQmpCoreAsync(_qmpClient, qmpPort, verboseQmp);
    }

    static async Task ConnectQmpCoreAsync(QemuQmpClient client, int port, bool verbose)
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
            _gdbClient = new QemuGdbClient { Verbose = verboseGdb };
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
        readonly QemuGdbClient _client;

        internal GdbMemorySession(QemuGdbClient client)
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
    static int CharToVncKeysym(char c)
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