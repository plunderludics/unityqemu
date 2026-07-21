using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Minimal GDB remote serial protocol client for QEMU's gdbstub.
/// Supports guest memory read/write (virtual or physical).
/// </summary>
public class QemuGdbClient : IDisposable
{
    readonly object _lock = new object();
    TcpClient _tcpClient;
    NetworkStream _stream;
    bool _noAck;
    bool _stopped;
    int _memorySessionDepth;
    /// <summary>True if the outermost memory session interrupted a running guest (so it should resume).</summary>
    bool _memorySessionOwnsResume;

    // Session is "connected" iff we still hold a stream. Do not use TcpClient.Connected /
    // Socket.Poll (unreliable here).
    public bool IsConnected => _stream != null;
    public bool UsePhysicalMemory { get; private set; }
    /// <summary>True when the guest vCPU is stopped under GDB control.</summary>
    public bool IsStopped => _stopped;
    /// <summary>Log connect / interrupt / packet chatter.</summary>
    public bool Verbose { get; set; }

    public void Connect(string host, int port, bool usePhysicalMemory = true, int timeoutMs = 5000)
    {
        lock (_lock)
        {
            DisposeSocket(quiet: true);

            _tcpClient = new TcpClient();
            var connect = _tcpClient.ConnectAsync(host, port);
            if (!connect.Wait(timeoutMs))
            {
                DisposeSocket(quiet: true);
                throw new TimeoutException($"Timed out connecting to gdbstub at {host}:{port}");
            }
            connect.GetAwaiter().GetResult();

            _stream = _tcpClient.GetStream();
            _stream.ReadTimeout = timeoutMs;
            _stream.WriteTimeout = timeoutMs;
            _noAck = false;
            _stopped = false;

            // QEMU pauses the vCPU on attach. It often does NOT push an unsolicited stop
            // packet — GDB is expected to query halt reason with '?'.
            DrainInbound();
            string halt;
            try
            {
                halt = Transact("?");
            }
            catch (Exception e)
            {
                DisposeSocket($"Connect '?' failed: {e.Message}");
                throw new Exception($"GDB '?' failed: {e.Message}", e);
            }

            if (IsStopReply(halt))
            {
                _stopped = true;
            }
            else
            {
                // Still treat as stopped: QEMU's gdbstub stops on connect even when '?'
                // is empty/unexpected, otherwise the guest stays frozen after attach.
                _stopped = true;
                if (!string.IsNullOrEmpty(halt))
                    Debug.LogWarning($"GDB attach: unexpected '?' reply '{halt}' (assuming stopped)");
                else
                    Debug.LogWarning("GDB attach: empty '?' reply (assuming stopped)");
            }

            // Faster packet exchange (optional; fall back to ACK mode if unsupported).
            try
            {
                string qSupported = Transact("qSupported:multiprocess+;QStartNoAckMode+");
                if (qSupported != null && qSupported.Contains("QStartNoAckMode+"))
                {
                    string ack = Transact("QStartNoAckMode");
                    if (ack == "OK")
                    {
                        _noAck = true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GDB qSupported/NoAck negotiation failed: {e.Message}");
                if (!IsConnected)
                    throw;
            }

            SetPhysicalMemoryModeUnlocked(usePhysicalMemory);

            // Always resume after attach so Windows keeps booting. Peek/poke still works while running.
            ContinueUnlocked();
            if (Verbose)
                Debug.Log($"Connected to QEMU gdbstub at {host}:{port} (physical={UsePhysicalMemory}, halt={halt ?? "<none>"}, connected={IsConnected})");
        }
    }

    public void SetPhysicalMemoryMode(bool enabled)
    {
        lock (_lock)
        {
            SetPhysicalMemoryModeUnlocked(enabled);
        }
    }

    void SetPhysicalMemoryModeUnlocked(bool enabled)
    {
        EnsureConnected();
        // QEMU extension: 1 = physical, 0 = virtual (guest VA).
        string resp = Transact($"Qqemu.PhyMemMode:{(enabled ? "1" : "0")}");
        if (resp != "OK" && resp != "")
        {
            Debug.LogWarning($"Qqemu.PhyMemMode returned '{resp}' (continuing anyway)");
        }
        UsePhysicalMemory = enabled;
    }

    /// <summary>
    /// Pause the guest once, perform many memory ops, then resume only if we paused it.
    /// If the guest was already stopped (manual Pause), it stays stopped. Nested sessions supported.
    /// </summary>
    public void BeginMemorySession()
    {
        lock (_lock)
        {
            EnsureConnected();
            if (_memorySessionDepth++ == 0)
            {
                _memorySessionOwnsResume = !_stopped;
                if (_memorySessionOwnsResume)
                    InterruptUnlocked();
            }
        }
    }

    /// <summary>End a session started with <see cref="BeginMemorySession"/>.</summary>
    public void EndMemorySession()
    {
        lock (_lock)
        {
            if (_memorySessionDepth <= 0)
                return;
            if (--_memorySessionDepth == 0)
            {
                if (_memorySessionOwnsResume && IsConnected && _stopped)
                    ContinueUnlocked();
                _memorySessionOwnsResume = false;
            }
        }
    }

    public byte[] ReadMemory(long address, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        lock (_lock)
        {
            EnsureConnected();
            bool manageRunState = _memorySessionDepth == 0;
            bool resumeAfter = manageRunState && !_stopped;
            try
            {
                if (manageRunState && resumeAfter)
                    InterruptUnlocked();

                return ReadMemoryUnlocked(address, length);
            }
            finally
            {
                if (resumeAfter && IsConnected)
                    ContinueUnlocked();
            }
        }
    }

    public void WriteMemory(long address, byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0) return;

        lock (_lock)
        {
            EnsureConnected();
            bool manageRunState = _memorySessionDepth == 0;
            bool resumeAfter = manageRunState && !_stopped;
            try
            {
                if (manageRunState && resumeAfter)
                    InterruptUnlocked();

                WriteMemoryUnlocked(address, data);
            }
            finally
            {
                if (resumeAfter && IsConnected)
                    ContinueUnlocked();
            }
        }
    }

    /// <summary>Max bytes per GDB <c>m</c>/<c>M</c> packet (QEMU rejects oversized requests with E22).</summary>
    const int GdbPacketMaxBytes = 256;

    byte[] ReadMemoryUnlocked(long address, int length)
    {
        var result = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int chunk = Math.Min(GdbPacketMaxBytes, length - offset);
            string resp = Transact($"m{address + offset:x},{chunk:x}");
            if (string.IsNullOrEmpty(resp) || resp.StartsWith("E"))
            {
                throw new Exception($"GDB memory read failed at 0x{address + offset:X}: {resp}");
            }
            if (resp.Length < chunk * 2)
            {
                throw new Exception(
                    $"GDB memory read short response at 0x{address + offset:X}: got {resp.Length / 2} bytes, expected {chunk} (raw='{Truncate(resp, 64)}')");
            }
            ParseHexBytes(resp, result, offset, chunk);
            offset += chunk;
        }
        return result;
    }

    void WriteMemoryUnlocked(long address, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(GdbPacketMaxBytes, data.Length - offset);
            var sb = new StringBuilder(chunk * 2);
            for (int i = 0; i < chunk; i++)
                sb.Append(data[offset + i].ToString("x2"));
            string resp = Transact($"M{address + offset:x},{chunk:x}:{sb}");
            if (resp != "OK")
                throw new Exception($"GDB memory write failed at 0x{address + offset:X}: {resp}");
            offset += chunk;
        }
    }

    static void ParseHexBytes(string hex, byte[] dest, int destOffset, int count)
    {
        for (int i = 0; i < count; i++)
            dest[destOffset + i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    }

    /// <summary>
    /// Mark the guest stopped because something outside GDB paused it (e.g. QMP <c>stop</c>).
    /// Prevents memory sessions from auto-resuming.
    /// </summary>
    public void NotifyStoppedExternally()
    {
        lock (_lock)
        {
            _stopped = true;
        }
    }

    /// <summary>
    /// Mark the guest running after an external resume (e.g. QMP <c>cont</c>).
    /// </summary>
    public void NotifyRunningExternally()
    {
        lock (_lock)
        {
            // Don't clear stopped while a memory session still owns the pause.
            if (_memorySessionDepth == 0)
                _stopped = false;
        }
    }

    public void Interrupt()
    {
        lock (_lock)
        {
            InterruptUnlocked();
        }
    }

    void InterruptUnlocked()
    {
        EnsureConnected();
        if (_stopped) return;

        if (Verbose)
            Debug.Log("GDB interrupt (Ctrl-C) — pausing guest for memory op");
        _stream.WriteByte(0x03); // Ctrl-C
        _stream.Flush();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            string stop = ReadPacket(allowEmpty: false);
            if (IsStopReply(stop))
            {
                _stopped = true;
                if (Verbose)
                    Debug.Log($"GDB interrupt stop reply: {stop}");
                return;
            }
            Debug.LogWarning($"GDB interrupt: ignoring non-stop packet '{Truncate(stop, 64)}'");
        }

        // Assume stopped so callers still try to continue afterward.
        _stopped = true;
        Debug.LogWarning("GDB interrupt: no stop reply after Ctrl-C (assuming stopped)");
    }

    public void Continue()
    {
        lock (_lock)
        {
            ContinueUnlocked();
        }
    }

    void ContinueUnlocked()
    {
        EnsureConnected();
        SendPacket("c");
        _stopped = false;
        if (!_noAck)
        {
            TryReadAck();
        }
        DrainInbound();
    }

    static bool IsStopReply(string packet)
    {
        if (string.IsNullOrEmpty(packet)) return false;
        char c = packet[0];
        return c == 'S' || c == 'T' || c == 'W' || c == 'X' || c == 'N';
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max) + "...";
    }

    /// <summary>
    /// Discard any bytes already buffered. Non-blocking only — a timed-out ReadByte on
    /// Unity/Mono can leave NetworkStream/TcpClient in a bad state.
    /// </summary>
    void DrainInbound()
    {
        if (_stream == null) return;
        try
        {
            var buf = new byte[256];
            while (_stream.DataAvailable)
            {
                if (_stream.Read(buf, 0, buf.Length) <= 0)
                    break;
            }
        }
        catch
        {
            // ignore
        }
    }

    string Transact(string data)
    {
        try
        {
            SendPacket(data);
            string resp = ReadPacket(allowEmpty: false);

            // '?'s normal reply IS a stop/halt packet (e.g. T05thread:01;).
            // For other commands, an unsolicited stop (from pausing a running guest)
            // can arrive first — consume it and read the real command reply.
            if (data != "?" && IsStopReply(resp))
            {
                _stopped = true;
                if (Verbose)
                    Debug.Log($"GDB Transact({Truncate(data, 48)}): skipped stop reply '{resp}'");
                resp = ReadPacket(allowEmpty: false);
            }

            return resp;
        }
        catch (Exception e)
        {
            DisposeSocket($"Transact({Truncate(data, 48)}) failed: {e.GetType().Name}: {e.Message}");
            throw;
        }
    }

    void SendPacket(string data)
    {
        byte[] payload = Encoding.ASCII.GetBytes(data);
        int checksum = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            checksum = (checksum + payload[i]) & 0xff;
        }

        var packet = new byte[1 + payload.Length + 1 + 2];
        packet[0] = (byte)'$';
        Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
        packet[1 + payload.Length] = (byte)'#';
        string cs = checksum.ToString("x2");
        packet[packet.Length - 2] = (byte)cs[0];
        packet[packet.Length - 1] = (byte)cs[1];

        _stream.Write(packet, 0, packet.Length);
        _stream.Flush();
    }

    string ReadPacket(bool allowEmpty)
    {
        // Skip ACKs / noise until '$' or timeout.
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new Exception("GDB connection closed while reading packet");
            if (b == '+') continue;
            if (b == '-') throw new Exception("GDB NACK received");
            if (b == '$') break;
            // Ignore unexpected bytes (e.g. leftover stop noise).
        }

        var data = new StringBuilder();
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new Exception("GDB connection closed while reading packet body");
            if (b == '#') break;
            // RSP binary escaping: '}' followed by (byte XOR 0x20)
            if (b == '}')
            {
                int escaped = _stream.ReadByte();
                if (escaped < 0) throw new Exception("GDB connection closed in escape sequence");
                data.Append((char)(escaped ^ 0x20));
            }
            else
            {
                data.Append((char)b);
            }
        }

        int c1 = _stream.ReadByte();
        int c2 = _stream.ReadByte();
        if (c1 < 0 || c2 < 0) throw new Exception("GDB connection closed while reading checksum");

        if (!_noAck)
        {
            _stream.WriteByte((byte)'+');
            _stream.Flush();
        }

        string result = data.ToString();
        if (!allowEmpty && result == null)
        {
            throw new Exception("Empty GDB packet");
        }
        return result;
    }

    bool TryReadAck()
    {
        // Non-blocking: a timed-out ReadByte can poison the stream on Unity/Mono.
        if (_stream == null || !_stream.DataAvailable) return false;
        try
        {
            int b = _stream.ReadByte();
            return b == '+';
        }
        catch
        {
            return false;
        }
    }

    void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to QEMU gdbstub");
        }
    }

    void DisposeSocket(string reason = null, bool quiet = false)
    {
        bool hadSession = _stream != null || _tcpClient != null;
        if (hadSession && !quiet)
        {
            Debug.LogWarning(
                $"GDB IsConnected -> false. reason={reason ?? "DisposeSocket()"}\n{StackTraceUtility.ExtractStackTrace()}");
        }

        try { _stream?.Close(); } catch { /* ignore */ }
        try { _tcpClient?.Close(); } catch { /* ignore */ }
        _stream = null;
        _tcpClient = null;
        _noAck = false;
        _stopped = false;
        _memorySessionDepth = 0;
        _memorySessionOwnsResume = false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeSocket("Dispose()");
        }
    }
}
}
