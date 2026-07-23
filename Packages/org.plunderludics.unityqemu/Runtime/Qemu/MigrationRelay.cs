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
/// (QMP <c>get-win32-socket</c>) and used with <c>migrate fd:</c>; we gzip the
/// stream into a <c>.vmstate</c> sidecar.</item>
/// <item>Load: QEMU `-incoming tcp:` listens; we connect and feed the gunzipped
/// sidecar.</item>
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
        /// Gzip everything QEMU writes into <paramref name="outputPath"/>
        /// (written via temp + rename). Returns the compressed byte count.
        /// Ends at EOF, or — once <see cref="FinishAfterDrain"/> was called — when
        /// the socket has been silent for the drain window.
        /// </summary>
        public Task<long> ReceiveToFileAsync(string outputPath, CancellationToken ct = default)
        {
            Socket readEnd = _readEnd;
            return Task.Run(() =>
            {
                string tmp = outputPath + ".tmp";
                try
                {
                    using (FileStream file = File.Create(tmp))
                    using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                    {
                        var buffer = new byte[256 * 1024];
                        var quiet = new Stopwatch();
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            // Readable == data available or connection closed.
                            if (readEnd.Poll(200_000 /*µs*/, SelectMode.SelectRead))
                            {
                                int read = readEnd.Receive(buffer);
                                if (read <= 0)
                                    break; // EOF
                                gzip.Write(buffer, 0, read);
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
    /// it boots) and feed it the gunzipped contents of <paramref name="vmstatePath"/>.
    /// Returns once the stream is fully written and half-closed. Whether QEMU
    /// accepted the state is observed via QMP runstate polling by the caller — QEMU
    /// may never close its side of the socket (see class remarks), so we don't wait
    /// for that.
    /// </summary>
    public static Task SendFromFileAsync(
        int port, string vmstatePath, int connectTimeoutMs = 15_000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vmstatePath) || !File.Exists(vmstatePath))
            throw new FileNotFoundException("vmstate sidecar not found", vmstatePath);

        return Task.Run(() =>
        {
            TcpClient client = ConnectWithRetry(port, connectTimeoutMs, ct);
            using (client)
            using (NetworkStream net = client.GetStream())
            {
                using (FileStream file = File.OpenRead(vmstatePath))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    Pump(gzip, net, ct);
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
}
}
