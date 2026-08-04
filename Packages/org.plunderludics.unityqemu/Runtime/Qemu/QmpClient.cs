using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// QEMU Machine Protocol (QMP) client for sending commands to QEMU.
/// Handles connection, handshake, and command execution.
/// </summary>
namespace UnityQemu {
public class QmpClient : IDisposable
{
    Socket _socket;
    NetworkStream _stream;
    StreamReader _reader;
    StreamWriter _writer;
    bool _isConnected;
    bool _capabilitiesNegotiated;
    int _commandIdCounter = 1;

    /// <summary>Log connect/handshake/command traffic to the console.</summary>
    public bool Verbose { get; set; }

    /// <summary>True when connected over a unix-domain socket (SCM_RIGHTS / getfd capable).</summary>
    public bool IsUnixTransport { get; private set; }

    /// <summary>Whether the client is connected to QEMU's QMP socket.</summary>
    public bool IsConnected =>
        _isConnected && _socket != null && _socket.Connected;

    /// <summary>Connect to QEMU's QMP TCP socket.</summary>
    public async Task ConnectAsync(string host, int port)
    {
        LogVerbose($"Connecting to QMP socket on {host}:{port}");
        try
        {
            ResetTransport();
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await _socket.ConnectAsync(host, port);
            IsUnixTransport = false;
            await FinishConnectAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to connect to QMP socket: {e.Message}");
            _isConnected = false;
            throw;
        }
    }

    /// <summary>
    /// Connect to QEMU's QMP unix-domain socket (required for <see cref="PassFdAsync"/>).
    /// </summary>
    public async Task ConnectUnixAsync(string socketPath)
    {
        if (string.IsNullOrEmpty(socketPath))
            throw new ArgumentException("socket path required", nameof(socketPath));

        LogVerbose($"Connecting to QMP unix socket '{socketPath}'");
        try
        {
            ResetTransport();
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            IsUnixTransport = true;
            await FinishConnectAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to connect to QMP unix socket: {e.Message}");
            _isConnected = false;
            throw;
        }
    }

    async Task FinishConnectAsync()
    {
        _stream = new NetworkStream(_socket, ownsSocket: false);
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, Encoding.ASCII) { AutoFlush = true };
        _isConnected = true;

        string greeting = await _reader.ReadLineAsync();
        LogVerbose($"QMP greeting: {greeting}");

        if (string.IsNullOrEmpty(greeting))
            throw new Exception("Empty QMP greeting");

        JObject greetingObj = JObject.Parse(greeting);
        if (greetingObj["QMP"] == null)
            throw new Exception("Invalid QMP greeting - expected QMP property");

        await NegotiateCapabilitiesAsync();
    }

    void ResetTransport()
    {
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _socket?.Dispose(); } catch { /* ignore */ }
        _reader = null;
        _writer = null;
        _stream = null;
        _socket = null;
        IsUnixTransport = false;
        _capabilitiesNegotiated = false;
        _isConnected = false;
    }

    /// <summary>
    /// Pass a file descriptor to QEMU via SCM_RIGHTS, then bind it with QMP <c>getfd</c>.
    /// Requires <see cref="IsUnixTransport"/>.
    /// </summary>
    public async Task PassFdAsync(int fd, string fdname)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to QMP socket");
        if (!IsUnixTransport)
            throw new InvalidOperationException(
                "QMP getfd requires a unix-domain QMP connection");
        if (string.IsNullOrEmpty(fdname))
            throw new ArgumentException("fdname required", nameof(fdname));

        UnixScmRights.SendFd(_socket, fd);
        await ExecuteCommandAsync("getfd", new JObject { ["fdname"] = fdname });
    }

    async Task NegotiateCapabilitiesAsync()
    {
        var response = await ExecuteCommandAsync("qmp_capabilities");

        if (response["return"] != null)
        {
            _capabilitiesNegotiated = true;
            LogVerbose("QMP capabilities negotiated successfully");
        }
        else if (response["error"] != null)
        {
            throw new Exception($"Failed to negotiate QMP capabilities: {response.ToString()}");
        }
    }

    /// <summary>
    /// Execute a QMP command and return the response.
    /// </summary>
    async Task<JObject> ExecuteCommandAsync(string command, JObject arguments)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to QMP socket");
        }

        if (!_capabilitiesNegotiated && command != "qmp_capabilities")
        {
            throw new InvalidOperationException("QMP capabilities not negotiated. Call ConnectAsync first.");
        }

        int commandId = _commandIdCounter++;

        JObject commandObj = new JObject
        {
            ["execute"] = command,
            ["id"] = commandId
        };

        if (arguments != null)
        {
            commandObj["arguments"] = arguments;
        }

        string commandJson = commandObj.ToString(Newtonsoft.Json.Formatting.None);
        LogVerbose($"Sending QMP command: {commandJson}");
        await _writer.WriteLineAsync(commandJson);

        // Skip async event messages until we get the matching command reply.
        while (true)
        {
            string responseLine = await _reader.ReadLineAsync();
            if (string.IsNullOrEmpty(responseLine))
            {
                throw new Exception("Empty response from QMP");
            }

            JObject response = JObject.Parse(responseLine);

            if (response["event"] != null)
            {
                string eventName = response["event"]?.ToString() ?? "?";
                if (Verbose)
                    Debug.Log($"QMP event: {responseLine}");
                else if (!IsRoutineQmpEvent(eventName))
                    Debug.Log($"QMP event: {eventName}");
                continue;
            }

            LogVerbose($"QMP response: {responseLine}");

            if (response["error"] != null)
            {
                JToken error = response["error"];
                string errorClass = error["class"]?.ToString() ?? "Unknown";
                string errorDesc = error["desc"]?.ToString() ?? "Unknown error";
                string message = $"QMP `{command}` failed: {errorClass} - {errorDesc}";
                Debug.LogWarning(message);
                throw new Exception(message);
            }

            if (response["id"] != null && response["id"].Value<int>() != commandId)
            {
                LogVerbose($"QMP response ID mismatch: expected {commandId}, got {response["id"].Value<int>()}");
                continue;
            }

            return response;
        }
    }

    /// <summary>
    /// Run a Human Monitor Protocol (HMP) command via QMP passthrough.
    /// Returns the raw reply. Most mutating commands succeed with an empty string;
    /// <c>drive_add if=none</c> prints <c>OK</c>. Failures put other text in the reply
    /// (often without the word "error"). Prefer
    /// <see cref="VirtualMachine.RunHumanMonitorCommandOrThrowAsync"/>.
    /// </summary>
    public async Task<string> RunHumanMonitorCommandAsync(string commandLine)
    {
        var args = new JObject { ["command-line"] = commandLine };
        JObject response = await ExecuteCommandAsync("human-monitor-command", args);
        return response["return"]?.ToString() ?? "";
    }

    /// <summary>
    /// Execute a QMP command with arguments as a JSON string.
    /// </summary>
    public async Task<JObject> ExecuteCommandAsync(string command, string argumentsJson = null)
    {
        JObject arguments = string.IsNullOrEmpty(argumentsJson) ? null : JObject.Parse(argumentsJson);
        return await ExecuteCommandAsync(command, arguments);
    }

    void LogVerbose(string message)
    {
        if (Verbose)
            Debug.Log(message);
    }

    /// <summary>
    /// Expected chatter during pause/resume, hotplug, and RTC — not worth a console line
    /// unless <see cref="Verbose"/> is on. Unusual events (SHUTDOWN, GUEST_PANICKED, …) still log.
    /// </summary>
    static bool IsRoutineQmpEvent(string eventName)
    {
        switch (eventName)
        {
            case "DEVICE_DELETED":
            case "DEVICE_TRAY_MOVED":
            case "STOP":
            case "RESUME":
            case "RESET":
            case "WAKEUP":
            case "SUSPEND":
            case "SUSPEND_DISK":
            case "RTC_CHANGE":
            case "BALLOON_CHANGE":
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        ResetTransport();
    }
}
}
