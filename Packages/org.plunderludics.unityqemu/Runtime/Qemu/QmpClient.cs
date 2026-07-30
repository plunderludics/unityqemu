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
    private TcpClient _tcpClient;
    private NetworkStream _stream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private bool _isConnected = false;
    private bool _capabilitiesNegotiated = false;
    private int _commandIdCounter = 1;

    /// <summary>Log connect/handshake/command traffic to the console.</summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Whether the client is connected to QEMU's QMP socket.
    /// </summary>
    public bool IsConnected => _isConnected && _tcpClient != null && _tcpClient.Connected;

    /// <summary>
    /// Connect to QEMU's QMP socket.
    /// </summary>
    /// <param name="host">Hostname or IP address (usually "127.0.0.1" for localhost)</param>
    /// <param name="port">QMP port number</param>
    public async Task ConnectAsync(string host, int port)
    {
        LogVerbose($"Connecting to QMP socket on {host}:{port}");
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);
            _stream = _tcpClient.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.ASCII) { AutoFlush = true };
            _isConnected = true;

            // QEMU sends a greeting message immediately upon connection
            string greeting = await _reader.ReadLineAsync();
            LogVerbose($"QMP greeting: {greeting}");

            if (!string.IsNullOrEmpty(greeting))
            {
                JObject greetingObj = JObject.Parse(greeting);
                if (greetingObj["QMP"] != null)
                {
                    await NegotiateCapabilitiesAsync();
                }
                else
                {
                    throw new Exception("Invalid QMP greeting - expected QMP property");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to connect to QMP socket: {e.Message}");
            _isConnected = false;
            throw;
        }
    }

    private async Task NegotiateCapabilitiesAsync()
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
                // Routine lifecycle noise (our own device_del, pause/resume) stays Verbose-only.
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
                // Always surface — callers sometimes catch/swallow.
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
        _isConnected = false;
        _capabilitiesNegotiated = false;

        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();

        _reader = null;
        _writer = null;
        _stream = null;
        _tcpClient = null;
    }
}
}
