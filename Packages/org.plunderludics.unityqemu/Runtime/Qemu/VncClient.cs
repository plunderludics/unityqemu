using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Plunderludics.RemoteViewing.Vnc;
using TriInspector;
using System.Diagnostics;
using RemoteVncClient = Plunderludics.RemoteViewing.Vnc.VncClient;

/// <summary>
/// VNC client wrapper using RemoteViewing library for QEMU
/// </summary>
namespace UnityQemu {
public class VncClient : IDisposable
{
    private string _host;
    private int _display;
    private RemoteVncClient _vncClient;
    private Texture2D _texture;
    private Color32[] _colorBuffer;
    private bool _connected = false;
    private bool _needsUpdate = false;
    private object _updateLock = new object();

    /// <summary>When true, negotiate QEMU audio and feed PCM to <see cref="AudioPlayer"/>.</summary>
    public bool PlayAudioInUnity { get; set; }

    /// <summary>Optional Unity playback sink (required when <see cref="PlayAudioInUnity"/> is on).</summary>
    public VncAudioPlayer AudioPlayer { get; set; }

    // Reconnect state. RemoteViewing's message-loop thread swallows protocol/socket errors
    // and silently raises Closed, so we log it ourselves and retry from Update().
    private volatile bool _disposed;
    private volatile bool _unexpectedClose;
    private bool _reconnectInProgress;
    private int _reconnectAttempts;
    private DateTime _nextReconnectAt = DateTime.MinValue;
    private DateTime _connectedAt;

    // Frame pacing: NotifyFps = FramebufferChanged events from RemoteViewing's thread;
    // ApplyFps = textures actually uploaded on the Unity main thread. A large gap means
    // Unity is coalescing / falling behind on the CPU upload path.
    readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    int _notifyCountInWindow;
    int _applyCountInWindow;
    volatile float _notifyFps;
    volatile float _applyFps;
    const double FpsWindowSeconds = 1.0;

    const double BaseReconnectDelaySeconds = 2.0;
    const double MaxReconnectDelaySeconds = 10.0;

    public bool IsConnected => _connected && _vncClient != null && _vncClient.IsConnected;
    public bool IsInternalClientConnected => _vncClient != null && _vncClient.IsConnected;
    public Texture2D Texture => _texture;

    /// <summary>VNC framebuffer-changed notifications per second (incoming).</summary>
    public float NotifyFps => _notifyFps;

    /// <summary>Texture uploads completed per second (what Unity actually presents).</summary>
    public float ApplyFps => _applyFps;

    public async Task ConnectAsync(string host, int display)
    {
        _host = host;
        _display = display;
        try
        {
            await ConnectInternalAsync();

            // Texture will be created on main thread when first framebuffer update arrives
            UnityEngine.Debug.Log($"VNC connected! Resolution: {_vncClient.Framebuffer?.Width ?? 0}x{_vncClient.Framebuffer?.Height ?? 0}");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"VNC connection error: {e.GetType().Name}: {e.Message}");
            _connected = false;
            throw;
        }
    }

    async Task ConnectInternalAsync()
    {
        int port = 5900 + _display;

        var client = new RemoteVncClient();
        // RemoteViewing defaults to 15 — that alone was capping us at ~14 FPS.
        // This only throttles FramebufferUpdateRequest; pointer/key events are unthrottled.
        client.MaxUpdateRate = 60;

        // Set up framebuffer changed event to update texture
        client.FramebufferChanged += OnFramebufferChanged;
        client.Closed += OnClientClosed;
        if (PlayAudioInUnity)
        {
            client.QemuAudioNegotiated += OnQemuAudioNegotiated;
            client.AudioDataReceived += OnAudioDataReceived;
            AudioPlayer?.RequestStartPlayback();
        }

        int audioHz = AudioSettings.outputSampleRate > 0
            ? Mathf.Min(48000, AudioSettings.outputSampleRate)
            : 44100;

        var options = new VncClientConnectOptions
        {
            ShareDesktop = true,
            EnableQemuAudio = PlayAudioInUnity,
            AdvertiseQemuAudio = PlayAudioInUnity,
            QemuAudioFormat = new QemuAudioFormat(QemuAudioSampleFormat.S16, 2, audioHz),
        };

        // Connect to VNC server (synchronous method, run in task)
        await Task.Run(() => client.Connect(_host, port, options));

        if (_disposed)
        {
            client.FramebufferChanged -= OnFramebufferChanged;
            client.Closed -= OnClientClosed;
            client.QemuAudioNegotiated -= OnQemuAudioNegotiated;
            client.AudioDataReceived -= OnAudioDataReceived;
            try { client.Close(); } catch { /* ignore */ }
            return;
        }

        _vncClient = client;
        _connected = true;
        _connectedAt = DateTime.UtcNow;
        _unexpectedClose = false;
        _reconnectAttempts = 0;
    }

    // Runs on RemoteViewing's message-loop thread when the connection dies for any reason
    // (protocol error, socket reset, server shutdown). The library gives no error detail.
    void OnClientClosed(object sender, EventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _vncClient))
            return;

        double uptime = (DateTime.UtcNow - _connectedAt).TotalSeconds;
        UnityEngine.Debug.LogWarning(
            $"VNC connection to {_host}:{5900 + _display} closed unexpectedly after {uptime:F0}s. " +
            "The RemoteViewing message loop hit an error it doesn't report (often a guest video mode/resolution " +
            "change it can't parse) or the server dropped the connection. Will attempt to reconnect.");
        _unexpectedClose = true;
    }

    void OnQemuAudioNegotiated(object sender, EventArgs e)
    {
        // VNC thread — do not touch AudioSource here.
        AudioPlayer?.RequestStartPlayback();
    }

    void OnAudioDataReceived(object sender, QemuAudioDataEventArgs e)
    {
        if (!PlayAudioInUnity || AudioPlayer == null || e?.Data == null)
            return;
        AudioPlayer.PushPcm(e.Data, e.Format);
    }

    private void OnFramebufferChanged(object sender, FramebufferChangedEventArgs e)
    {
        Interlocked.Increment(ref _notifyCountInWindow);
        // Mark that we need an update - actual texture update happens on main thread
        lock (_updateLock)
        {
            _needsUpdate = true;
        }
    }

    void RollFpsWindow()
    {
        double elapsed = _fpsWatch.Elapsed.TotalSeconds;
        if (elapsed < FpsWindowSeconds)
            return;

        int notifies = Interlocked.Exchange(ref _notifyCountInWindow, 0);
        _notifyFps = (float)(notifies / elapsed);
        _applyFps = (float)(_applyCountInWindow / elapsed);
        _applyCountInWindow = 0;
        _fpsWatch.Restart();
    }
    
    /// <summary>
    /// Call this from Unity's Update() on the main thread to process framebuffer updates
    /// </summary>
    public void UpdateTexture()
    {
        if (!_needsUpdate || _vncClient?.Framebuffer == null)
            return;
        
        lock (_updateLock)
        {
            if (!_needsUpdate)
                return;
            
            _needsUpdate = false;
        }
        
        var framebuffer = _vncClient.Framebuffer;
        int width = framebuffer.Width;
        int height = framebuffer.Height;
        
        // Ensure texture size matches (this must be on main thread)
        if (_texture == null || _texture.width != width || _texture.height != height)
        {
            if (_texture != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_texture);
                else
                    UnityEngine.Object.DestroyImmediate(_texture);
            }
            _texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            _colorBuffer = null;
        }

        int pixelCount = width * height;
        if (_colorBuffer == null || _colorBuffer.Length != pixelCount)
            _colorBuffer = new Color32[pixelCount];

        // Server pixel format after connect; QEMU is typically 32bpp LE truecolor.
        var pf = framebuffer.PixelFormat;
        lock (framebuffer.SyncRoot)
        {
            byte[] buffer = framebuffer.GetBuffer();
            if (pf.BytesPerPixel != 4 || pf.BitDepth < 24)
                return;

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * width * 4;
                int dstRow = (height - 1 - y) * width; // Flip Y for Unity
                for (int x = 0; x < width; x++)
                {
                    int src = srcRow + x * 4;
                    uint pixel = pf.IsLittleEndian
                        ? (uint)(buffer[src]
                            | (buffer[src + 1] << 8)
                            | (buffer[src + 2] << 16)
                            | (buffer[src + 3] << 24))
                        : (uint)((buffer[src] << 24)
                            | (buffer[src + 1] << 16)
                            | (buffer[src + 2] << 8)
                            | buffer[src + 3]);
                    byte r = (byte)((pixel >> pf.RedShift) & 0xFF);
                    byte g = (byte)((pixel >> pf.GreenShift) & 0xFF);
                    byte b = (byte)((pixel >> pf.BlueShift) & 0xFF);
                    _colorBuffer[dstRow + x] = new Color32(r, g, b, 255);
                }
            }
        }
        
        _texture.SetPixels32(_colorBuffer);
        _texture.Apply(false, false);
        _applyCountInWindow++;
    }

    public void Update()
    {
        AudioPlayer?.MainThreadTick();
        // Update texture on main thread when framebuffer changes
        UpdateTexture();
        RollFpsWindow();

        // Detect a dead connection even if the Closed event never fired.
        bool lostConnection = _unexpectedClose ||
            (_connected && _vncClient != null && !_vncClient.IsConnected);

        if (lostConnection && !_unexpectedClose)
        {
            UnityEngine.Debug.LogWarning(
                $"VNC connection to {_host}:{5900 + _display} is down (IsConnected went false without a Closed event). " +
                "Will attempt to reconnect.");
            _unexpectedClose = true;
        }

        if (!_disposed && _unexpectedClose && !_reconnectInProgress && DateTime.UtcNow >= _nextReconnectAt)
        {
            TryReconnect();
        }
    }

    async void TryReconnect()
    {
        _reconnectInProgress = true;
        _connected = false;
        _reconnectAttempts++;

        // Tear down the old client (keep _texture so the last frame stays visible).
        var old = _vncClient;
        _vncClient = null;
        if (old != null)
        {
            old.FramebufferChanged -= OnFramebufferChanged;
            old.Closed -= OnClientClosed;
            old.QemuAudioNegotiated -= OnQemuAudioNegotiated;
            old.AudioDataReceived -= OnAudioDataReceived;
            try { old.Close(); } catch { /* ignore */ }
        }

        try
        {
            UnityEngine.Debug.Log($"VNC reconnect attempt {_reconnectAttempts} to {_host}:{5900 + _display}...");
            await ConnectInternalAsync();
            if (!_disposed)
                UnityEngine.Debug.Log($"VNC reconnected after {_reconnectAttempts} attempt(s).");
        }
        catch (Exception e)
        {
            double delay = Math.Min(
                MaxReconnectDelaySeconds,
                BaseReconnectDelaySeconds * Math.Pow(2, Math.Min(_reconnectAttempts - 1, 4)));
            _nextReconnectAt = DateTime.UtcNow.AddSeconds(delay);
            UnityEngine.Debug.LogWarning(
                $"VNC reconnect attempt {_reconnectAttempts} failed: {e.GetType().Name}: {e.Message}. " +
                $"Retrying in {delay:F0}s.");
        }
        finally
        {
            _reconnectInProgress = false;
        }
    }

    /// <summary>
    /// Send mouse pointer event to QEMU via VNC
    /// </summary>
    /// <param name="x">X coordinate in VNC framebuffer space (0 to framebuffer width)</param>
    /// <param name="y">Y coordinate in VNC framebuffer space (0 to framebuffer height)</param>
    /// <param name="leftButton">Left mouse button pressed</param>
    /// <param name="middleButton">Middle mouse button pressed</param>
    /// <param name="rightButton">Right mouse button pressed</param>
    public void SendMouseEvent(int x, int y, bool leftButton, bool middleButton, bool rightButton)
    {
        if (!IsConnected || _vncClient == null)
            return;

        try
        {
            // VNC button mask: 1 = left, 2 = middle, 4 = right
            byte buttonMask = 0;
            if (leftButton) buttonMask |= 1;
            if (middleButton) buttonMask |= 2;
            if (rightButton) buttonMask |= 4;

            _vncClient.SendPointerEvent(x, y, buttonMask);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to send mouse event: {e.Message}");
        }
    }

    /// <summary>
    /// Send keyboard event to QEMU via VNC
    /// </summary>
    /// <param name="keysym">VNC keysym (key symbol) - see VNC keysym definitions</param>
    /// <param name="pressed">True for key press, false for key release</param>
    public void SendKeyEvent(int keysym, bool pressed)
    {
        if (!IsConnected || _vncClient == null)
            return;

        try
        {
            _vncClient.SendKeyEvent((KeySym)keysym, pressed);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to send key event: {e.Message}");
        }
    }

    public void Dispose()
    {
        UnityEngine.Debug.Log("Disposing VNC client");
        _disposed = true;
        _connected = false;
        _unexpectedClose = false;

        if (_vncClient != null)
        {
            _vncClient.FramebufferChanged -= OnFramebufferChanged;
            _vncClient.Closed -= OnClientClosed;
            _vncClient.QemuAudioNegotiated -= OnQemuAudioNegotiated;
            _vncClient.AudioDataReceived -= OnAudioDataReceived;
            _vncClient.Close();
            _vncClient = null;
        }
    }
}

// Simple main thread dispatcher for Unity
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private System.Collections.Generic.Queue<System.Action> _queue = new System.Collections.Generic.Queue<System.Action>();
    
    public static UnityMainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("UnityMainThreadDispatcher");
                _instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }
    
    void Update()
    {
        lock (_queue)
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()?.Invoke();
            }
        }
    }
    
    public void Enqueue(System.Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }
}
}