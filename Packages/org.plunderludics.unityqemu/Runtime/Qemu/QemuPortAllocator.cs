using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Claims QEMU control ports with an in-process set plus OS-level holds so other
/// Unity projects cannot steal a port between probe and <c>qemu-system</c> bind.
/// VNC stays in the classic display range (port = 5900 + display), preferring a
/// display derived from a stable hash of the project identity.
/// QMP/GDB use kernel-assigned ephemeral ports.
/// </summary>
public static class QemuPortAllocator
{
    public const int VncBasePort = 5900;
    const int VncMaxDisplay = 99;

    static readonly object Gate = new object();
    static readonly HashSet<int> Claimed = new HashSet<int>();

    /// <summary>
    /// A loopback TCP port that stays bound until <see cref="HandOff"/> (so QEMU can
    /// listen) or <see cref="Dispose"/> (unclaim).
    /// </summary>
    public sealed class HeldPort : IDisposable
    {
        TcpListener _listener;
        bool _claimed;

        internal HeldPort(int port, TcpListener listener)
        {
            Port = port;
            _listener = listener;
            _claimed = true;
        }

        public int Port { get; }

        /// <summary>
        /// Stop listening so QEMU can bind this port. Stays claimed in-process until
        /// <see cref="Dispose"/>.
        /// </summary>
        public void HandOff()
        {
            lock (Gate)
            {
                StopListener();
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                StopListener();
                if (_claimed)
                {
                    Claimed.Remove(Port);
                    _claimed = false;
                }
            }
        }

        void StopListener()
        {
            if (_listener == null)
                return;
            try { _listener.Stop(); } catch { /* ignore */ }
            _listener = null;
        }
    }

    /// <summary>
    /// Prefer a VNC display from the project hash, then walk <c>:0</c>–<c>:99</c>
    /// wrapping around. The port stays bound until <see cref="HeldPort.HandOff"/>.
    /// </summary>
    public static HeldPort ClaimVncDisplayPort()
    {
        lock (Gate)
        {
            int start = PreferredVncDisplay();
            for (int i = 0; i <= VncMaxDisplay; i++)
            {
                int display = (start + i) % (VncMaxDisplay + 1);
                int port = VncBasePort + display;
                if (Claimed.Contains(port))
                    continue;
                if (!TryBindHold(port, out TcpListener listener))
                    continue;
                Claimed.Add(port);
                return new HeldPort(port, listener);
            }

            throw new InvalidOperationException(
                "No free VNC port in 5900–5999 (all claimed or in use). " +
                "Stop other VirtualMachines or override ports in Advanced.");
        }
    }

    /// <summary>Kernel-assigned free loopback port, held until handoff.</summary>
    public static HeldPort ClaimEphemeralPort()
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                }
                catch (SocketException)
                {
                    continue;
                }

                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (!Claimed.Add(port))
                {
                    try { listener.Stop(); } catch { /* ignore */ }
                    continue;
                }

                return new HeldPort(port, listener);
            }

            throw new InvalidOperationException(
                "Could not claim an ephemeral loopback port for QEMU.");
        }
    }

    /// <summary>Claim a specific port (override mode) and hold it until handoff.</summary>
    public static HeldPort ClaimExact(int port)
    {
        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "TCP port out of range");

        lock (Gate)
        {
            if (Claimed.Contains(port))
                throw new InvalidOperationException(
                    $"Port {port} is already claimed by another VirtualMachine in this process.");
            if (!TryBindHold(port, out TcpListener listener))
                throw new InvalidOperationException(
                    $"Port {port} is already in use on 127.0.0.1.");
            Claimed.Add(port);
            return new HeldPort(port, listener);
        }
    }

    public static bool IsPortFree(int port)
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

    /// <summary>
    /// True when QEMU stderr/output suggests a TCP listen/bind collision
    /// (retry with fresh ports).
    /// </summary>
    public static bool LooksLikeAddressInUse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.IndexOf("Address already in use", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Failed to start VNC", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Failed to bind", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("could not bind", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("error binding", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Preferred VNC display index for this project (<c>0..99</c>).</summary>
    public static int PreferredVncDisplay()
    {
        return StableHashMod(ProjectIdentity(), VncMaxDisplay + 1);
    }

    static string ProjectIdentity()
    {
#if UNITY_EDITOR
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        string root = !string.IsNullOrEmpty(Application.identifier)
            ? Application.identifier
            : Application.dataPath;
#endif
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    /// <summary>FNV-1a 32-bit — stable across process runs (unlike string.GetHashCode).</summary>
    static int StableHashMod(string s, int modulus)
    {
        unchecked
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return (int)(h % (uint)modulus);
        }
    }

    static bool TryBindHold(int port, out TcpListener listener)
    {
        listener = null;
        var trial = new TcpListener(IPAddress.Loopback, port);
        try
        {
            trial.Start();
            listener = trial;
            return true;
        }
        catch (SocketException)
        {
            try { trial.Stop(); } catch { /* ignore */ }
            return false;
        }
    }
}
}
