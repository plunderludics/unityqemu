using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityQemu {
/// <summary>
/// Plumbing for QEMU migration streams (durable snapshot state capture/restore).
/// <list type="bullet">
/// <item>Save: a connected loopback socket is duplicated into the QEMU process
/// (QMP <c>get-win32-socket</c>) and used with <c>migrate fd:</c>; we optionally
/// gzip the stream into a <c>.uqsnap</c> asset.</item>
/// <item>Load: QEMU <c>-incoming tcp:</c> listens; we connect and feed the (possibly
/// gunzipped) <c>.uqsnap</c> stream.</item>
/// </list>
/// Why <c>fd:</c> instead of <c>tcp:</c> for saving: QEMU delivers outgoing TCP
/// connect completion via a glib *idle* source on its main loop. A restored guest
/// whose SB16/i8257 DMA is mid-transfer reschedules its bottom-half forever, so the
/// loop never goes idle and `migrate tcp:` sits in "setup" indefinitely. A
/// pre-connected fd is adopted synchronously and sidesteps that entirely. The
/// incoming side is unaffected (its accept fires before any state is loaded).
///
/// Unity never parses the stream — it only pumps bytes. All pumping runs on the
/// thread pool so the main thread stays free.
/// </summary>
public static class MigrationRelay
{
    [DllImport("ws2_32.dll", SetLastError = true)]
    static extern int WSADuplicateSocketW(IntPtr socketHandle, int processId, byte[] protocolInfo);

    // sizeof(WSAPROTOCOL_INFOW)
    const int ProtocolInfoSize = 628;

    /// <summary>
    /// An outgoing-migration capture: one end of a connected loopback socket pair,
    /// whose other end has been duplicated for the QEMU process. Register the
    /// duplicate via QMP <c>get-win32-socket</c> (with <see cref="ProtocolInfoBase64"/>
    /// and <see cref="FdName"/>), start <c>migrate fd:&lt;FdName&gt;</c>, then read the
    /// stream with <see cref="ReceiveToFileAsync"/>.
    /// </summary>
    public sealed class OutgoingCapture : IDisposable
    {
        Socket _readEnd;
        Socket _qemuEnd;
        volatile bool _drainQuietly;

        public string FdName => "unityqemu-vmstate";
        public string ProtocolInfoBase64 { get; private set; }

        /// <summary>
        /// Create a connected loopback socket pair and duplicate the write end into
        /// process <paramref name="qemuPid"/>.
        /// </summary>
        public static OutgoingCapture Create(int qemuPid)
        {
            var capture = new OutgoingCapture();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                var qemuEnd = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                qemuEnd.Connect((IPEndPoint)listener.LocalEndpoint);
                capture._qemuEnd = qemuEnd;
                capture._readEnd = listener.AcceptSocket();
            }
            finally
            {
                listener.Stop();
            }

            var info = new byte[ProtocolInfoSize];
            if (WSADuplicateSocketW(capture._qemuEnd.Handle, qemuPid, info) != 0)
            {
                capture.Dispose();
                throw new InvalidOperationException(
                    $"WSADuplicateSocketW failed (error {Marshal.GetLastWin32Error()})");
            }
            capture.ProtocolInfoBase64 = Convert.ToBase64String(info);
            return capture;
        }

        /// <summary>
        /// Call after QMP <c>get-win32-socket</c> succeeded: QEMU owns its duplicate
        /// now, so our copy of that end can close (leaving QEMU's the only writer,
        /// which gives us EOF when migration finishes).
        /// </summary>
        public void CloseQemuEnd()
        {
            try { _qemuEnd?.Close(); } catch { /* ignore */ }
            _qemuEnd = null;
        }

        /// <summary>
        /// Copy everything QEMU writes into <paramref name="outputPath"/>
        /// (written via temp + rename). When <paramref name="gzip"/> is true, the
        /// file is gzip-compressed. Returns the on-disk byte count.
        /// Ends at EOF, or — once <see cref="FinishAfterDrain"/> was called — when
        /// the socket has been silent for the drain window.
        /// </summary>
        public Task<long> ReceiveToFileAsync(
            string outputPath, bool gzip = true, CancellationToken ct = default)
        {
            Socket readEnd = _readEnd;
            return Task.Run(() =>
            {
                string tmp = outputPath + ".tmp";
                try
                {
                    using (FileStream file = File.Create(tmp))
                    {
                        if (gzip)
                        {
                            using (var gz = new GZipStream(file, CompressionLevel.Fastest))
                                DrainSocketTo(readEnd, gz, ct);
                        }
                        else
                        {
                            DrainSocketTo(readEnd, file, ct);
                        }
                    }

                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                    File.Move(tmp, outputPath);
                    return new FileInfo(outputPath).Length;
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                    throw;
                }
            }, ct);
        }

        void DrainSocketTo(Socket readEnd, Stream sink, CancellationToken ct)
        {
            var buffer = new byte[256 * 1024];
            var quiet = new Stopwatch();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (readEnd.Poll(200_000 /*µs*/, SelectMode.SelectRead))
                {
                    int read = readEnd.Receive(buffer);
                    if (read <= 0)
                        break; // EOF
                    sink.Write(buffer, 0, read);
                    quiet.Reset();
                }
                else if (_drainQuietly)
                {
                    if (!quiet.IsRunning)
                        quiet.Start();
                    else if (quiet.ElapsedMilliseconds > 1500)
                        break; // completed + socket silent — stream drained
                }
            }
        }

        /// <summary>
        /// Call once QMP reports the migration completed. QEMU has flushed the whole
        /// stream by then, but a surviving handle to the write end inside the QEMU
        /// process can prevent a clean EOF — so after this, a quiet period on the
        /// socket counts as end of stream.
        /// </summary>
        public void FinishAfterDrain()
        {
            _drainQuietly = true;
        }

        public void Dispose()
        {
            try { _qemuEnd?.Close(); } catch { /* ignore */ }
            _qemuEnd = null;
            try { _readEnd?.Close(); } catch { /* ignore */ }
            _readEnd = null;
        }
    }

    /// <summary>Pick a currently free loopback TCP port (for QEMU's -incoming listener).</summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Connect to a QEMU started with <c>-incoming tcp:127.0.0.1:port</c> (retrying while
    /// it boots) and feed it the contents of <paramref name="vmstatePath"/>.
    /// When <paramref name="gzip"/> is true, the file is gunzipped on the way in.
    /// Returns once the stream is fully written and half-closed. Whether QEMU
    /// accepted the state is observed via QMP runstate polling by the caller — QEMU
    /// may never close its side of the socket (see class remarks), so we don't wait
    /// for that.
    /// </summary>
    public static Task SendFromFileAsync(
        int port,
        string vmstatePath,
        bool gzip = true,
        int connectTimeoutMs = 15_000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vmstatePath) || !File.Exists(vmstatePath))
            throw new FileNotFoundException("uqsnap machine-state file not found", vmstatePath);

        return Task.Run(() =>
        {
            TcpClient client = ConnectWithRetry(port, connectTimeoutMs, ct);
            using (client)
            using (NetworkStream net = client.GetStream())
            {
                using (FileStream file = File.OpenRead(vmstatePath))
                {
                    if (gzip)
                    {
                        using (var gz = new GZipStream(file, CompressionMode.Decompress))
                            Pump(gz, net, ct);
                    }
                    else
                    {
                        Pump(file, net, ct);
                    }
                }
                net.Flush();
                // Half-close: QEMU reads to EOF, then applies the state.
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }, ct);
    }

    static TcpClient ConnectWithRetry(int port, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                client.Connect(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Could not connect to QEMU -incoming on port {port} within {timeoutMs}ms");
                Thread.Sleep(100);
            }
        }
    }

    static void Pump(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    // QEMUFile: put_byte(len) + id ("pc.ram") then be64 RAM size. Appears early
    // in the migration stream (well before page data), so we only need a small prefix.
    static readonly byte[] PcRamIdMarker =
        { 6, (byte)'p', (byte)'c', (byte)'.', (byte)'r', (byte)'a', (byte)'m' };

    /// <summary>
    /// Guest RAM size in bytes from a migration stream's first <c>pc.ram</c> section.
    /// Decompresses only enough of a gzip file to find the early header (not the whole stream).
    /// </summary>
    public static bool TryProbePcRamBytes(string vmstatePath, bool gzip, out long ramBytes)
    {
        ramBytes = 0;
        if (string.IsNullOrEmpty(vmstatePath) || !File.Exists(vmstatePath))
            return false;

        try
        {
            using (FileStream file = File.OpenRead(vmstatePath))
            {
                Stream input = file;
                GZipStream gz = null;
                try
                {
                    if (gzip)
                    {
                        gz = new GZipStream(file, CompressionMode.Decompress);
                        input = gz;
                    }

                    // Cap how much we decompress: pc.ram is near the start (~tens of bytes).
                    const int maxScan = 256 * 1024;
                    var window = new byte[PcRamIdMarker.Length + 8];
                    int windowLen = 0;
                    var chunk = new byte[16 * 1024];
                    int scanned = 0;
                    while (scanned < maxScan)
                    {
                        int toRead = Math.Min(chunk.Length, maxScan - scanned);
                        int n = input.Read(chunk, 0, toRead);
                        if (n <= 0)
                            break;
                        scanned += n;

                        for (int i = 0; i < n; i++)
                        {
                            if (windowLen < window.Length)
                            {
                                window[windowLen++] = chunk[i];
                            }
                            else
                            {
                                Buffer.BlockCopy(window, 1, window, 0, window.Length - 1);
                                window[window.Length - 1] = chunk[i];
                            }

                            if (windowLen < window.Length)
                                continue;
                            if (!StartsWithPcRamId(window))
                                continue;

                            // BE64 size immediately after the id marker.
                            ramBytes =
                                ((long)window[7] << 56) |
                                ((long)window[8] << 48) |
                                ((long)window[9] << 40) |
                                ((long)window[10] << 32) |
                                ((long)window[11] << 24) |
                                ((long)window[12] << 16) |
                                ((long)window[13] << 8) |
                                window[14];
                            // Sanity: at least 1 MiB, at most 64 GiB, page-aligned.
                            if (ramBytes >= (1L << 20) &&
                                ramBytes <= (64L << 30) &&
                                (ramBytes & 0xFFF) == 0)
                                return true;
                            ramBytes = 0;
                        }
                    }
                }
                finally
                {
                    gz?.Dispose();
                }
            }
        }
        catch
        {
            ramBytes = 0;
            return false;
        }

        return false;
    }

    static bool StartsWithPcRamId(byte[] window)
    {
        for (int i = 0; i < PcRamIdMarker.Length; i++)
        {
            if (window[i] != PcRamIdMarker[i])
                return false;
        }
        return true;
    }
}
}
