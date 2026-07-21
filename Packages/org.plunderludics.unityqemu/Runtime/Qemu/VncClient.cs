using System;
using System.Threading.Tasks;
using UnityEngine;
using RemoteViewing.Vnc;
using TriInspector;
using System.Diagnostics;

/// <summary>
/// VNC client wrapper using RemoteViewing library for QEMU
/// </summary>
namespace UnityQemu {
public class VncClient : IDisposable
{
    private string _host;
    private int _display;
    private RemoteViewing.Vnc.VncClient _vncClient;
    private Texture2D _texture;
    private bool _connected = false;
    private bool _needsUpdate = false;
    private object _updateLock = new object();

    // Reconnect state. RemoteViewing's message-loop thread swallows protocol/socket errors
    // and silently raises Closed, so we log it ourselves and retry from Update().
    private volatile bool _disposed;
    private volatile bool _unexpectedClose;
    private bool _reconnectInProgress;
    private int _reconnectAttempts;
    private DateTime _nextReconnectAt = DateTime.MinValue;
    private DateTime _connectedAt;

    const double BaseReconnectDelaySeconds = 2.0;
    const double MaxReconnectDelaySeconds = 10.0;

    public bool IsConnected => _connected && _vncClient != null && _vncClient.IsConnected;
    public bool IsInternalClientConnected => _vncClient != null && _vncClient.IsConnected;
    public Texture2D Texture => _texture;

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

        var client = new RemoteViewing.Vnc.VncClient();

        // Set up framebuffer changed event to update texture
        client.FramebufferChanged += OnFramebufferChanged;
        client.Closed += OnClientClosed;

        // Set up connection options
        var options = new VncClientConnectOptions
        {
            ShareDesktop = true,
            PixelFormat = new VncPixelFormat(
                bitsPerPixel: 32,
                bitDepth: 24,
                redBits: 8,
                redShift: 16,
                greenBits: 8,
                greenShift: 8,
                blueBits: 8,
                blueShift: 0,
                isLittleEndian: true,
                isPalettized: false
            )
        };

        // Connect to VNC server (synchronous method, run in task)
        await Task.Run(() => client.Connect(_host, port, options));

        if (_disposed)
        {
            client.FramebufferChanged -= OnFramebufferChanged;
            client.Closed -= OnClientClosed;
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

    private void OnFramebufferChanged(object sender, FramebufferChangedEventArgs e)
    {
        // Mark that we need an update - actual texture update happens on main thread
        lock (_updateLock)
        {
            _needsUpdate = true;
        }
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
        
        // Ensure texture size matches (this must be on main thread)
        if (_texture == null || _texture.width != framebuffer.Width || _texture.height != framebuffer.Height)
        {
            if (_texture != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_texture);
                else
                    UnityEngine.Object.DestroyImmediate(_texture);
            }
            _texture = new Texture2D(framebuffer.Width, framebuffer.Height, TextureFormat.RGB24, false);
        }
        
        // Get pixel data from framebuffer
        // VncFramebuffer.GetPixels() returns int[] where each int represents a pixel
        var pixels = framebuffer.GetPixels();
        
        // Convert to Color32 array
        // RemoteViewing returns pixels as int[] where each int is ARGB (32-bit)
        Color32[] colors = new Color32[framebuffer.Width * framebuffer.Height];
        
        for (int y = 0; y < framebuffer.Height; y++)
        {
            for (int x = 0; x < framebuffer.Width; x++)
            {
                int pixelIndex = y * framebuffer.Width + x;
                int textureIndex = ((framebuffer.Height - 1 - y) * framebuffer.Width) + x; // Flip Y for Unity
                
                if (pixelIndex < pixels.Length)
                {
                    // Extract ARGB components from int (assuming little-endian ARGB format)
                    int pixel = pixels[pixelIndex];
                    byte a = (byte)((pixel >> 24) & 0xFF);
                    byte r = (byte)((pixel >> 16) & 0xFF);
                    byte g = (byte)((pixel >> 8) & 0xFF);
                    byte b = (byte)(pixel & 0xFF);
                    
                    colors[textureIndex] = new Color32(r, g, b, a == 0 ? (byte)255 : a);
                }
            }
        }
        
        // Update texture (must be on main thread)
        _texture.SetPixels32(colors);
        _texture.Apply();
    }

    public void Update()
    {
        // Update texture on main thread when framebuffer changes
        UpdateTexture();

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
            _vncClient.SendKeyEvent(keysym, pressed);
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