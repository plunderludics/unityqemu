using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UnityQemu {
/// <summary>
/// Send a file descriptor over a unix-domain socket via SCM_RIGHTS (for QMP <c>getfd</c>).
/// </summary>
static class UnixScmRights
{
    const int ScmRights = 0x01;

    // Linux: SOL_SOCKET=1; macOS/BSD: SOL_SOCKET=0xffff
    static int SolSocket =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0xffff : 1;

    public static void SendFd(Socket unixSocket, int fdToSend)
    {
        if (unixSocket == null)
            throw new ArgumentNullException(nameof(unixSocket));
        if (unixSocket.AddressFamily != AddressFamily.Unix)
            throw new ArgumentException("SCM_RIGHTS requires a unix-domain socket", nameof(unixSocket));

        int sockFd = unixSocket.Handle.ToInt32();
        // One NUL payload byte — matches QEMU/libvirt getfd convention.
        byte[] payload = { 0 };
        int rc = SendFdNative(sockFd, payload, fdToSend);
        if (rc < 0)
        {
            throw new InvalidOperationException(
                $"sendmsg(SCM_RIGHTS) failed (errno {Marshal.GetLastWin32Error()})");
        }
    }

    static int SendFdNative(int sockFd, byte[] payload, int fdToSend)
    {
        // cmsg buffer: cmsghdr + int fd, with platform alignment.
        // Linux cmsg_len is size_t; Darwin cmsg_len is socklen_t (UInt32).
        bool darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        int cmsgHdrSize = darwin
            ? (4 + 4 + 4) // socklen_t + int + int, then align to 4? Darwin aligns to sizeof(uint32)
            : IntPtr.Size + 4 + 4; // size_t + int + int
        // CMSG_SPACE(sizeof(int)): align header + data
        int align = IntPtr.Size; // pointer-size alignment is safe on both
        int cmsgLen = CmsgLen(cmsgHdrSize, sizeof(int), align);
        int cmsgSpace = Align(cmsgLen, align);

        var iov = new Iovec
        {
            iov_base = Marshal.AllocHGlobal(payload.Length),
            iov_len = (IntPtr)payload.Length,
        };
        Marshal.Copy(payload, 0, iov.iov_base, payload.Length);

        IntPtr control = Marshal.AllocHGlobal(cmsgSpace);
        try
        {
            for (int i = 0; i < cmsgSpace; i++)
                Marshal.WriteByte(control, i, 0);

            // Write cmsghdr
            int offset = 0;
            if (darwin)
            {
                Marshal.WriteInt32(control, offset, cmsgLen); // cmsg_len socklen_t
                offset += 4;
            }
            else
            {
                if (IntPtr.Size == 8)
                    Marshal.WriteInt64(control, offset, cmsgLen);
                else
                    Marshal.WriteInt32(control, offset, cmsgLen);
                offset += IntPtr.Size;
            }
            Marshal.WriteInt32(control, offset, SolSocket);
            offset += 4;
            Marshal.WriteInt32(control, offset, ScmRights);
            offset += 4;
            // Align data start to pointer size after header fields
            int dataOff = Align(darwin ? 12 : (IntPtr.Size + 8), align);
            Marshal.WriteInt32(control, dataOff, fdToSend);

            var msg = new Msghdr
            {
                msg_name = IntPtr.Zero,
                msg_namelen = 0,
                msg_iov = Marshal.AllocHGlobal(Marshal.SizeOf<Iovec>()),
                msg_iovlen = (IntPtr)1,
                msg_control = control,
                msg_controllen = (IntPtr)cmsgSpace,
                msg_flags = 0,
            };
            Marshal.StructureToPtr(iov, msg.msg_iov, false);
            try
            {
                return sendmsg(sockFd, ref msg, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(msg.msg_iov);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(iov.iov_base);
            Marshal.FreeHGlobal(control);
        }
    }

    static int CmsgLen(int hdrSize, int dataSize, int align) =>
        Align(hdrSize, align) + dataSize;

    static int Align(int n, int align) => (n + align - 1) & ~(align - 1);

    [StructLayout(LayoutKind.Sequential)]
    struct Iovec
    {
        public IntPtr iov_base;
        public IntPtr iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msghdr
    {
        public IntPtr msg_name;
        public uint msg_namelen;
        public IntPtr msg_iov;
        public IntPtr msg_iovlen;
        public IntPtr msg_control;
        public IntPtr msg_controllen;
        public int msg_flags;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int sendmsg(int sockfd, ref Msghdr msg, int flags);
}
}
