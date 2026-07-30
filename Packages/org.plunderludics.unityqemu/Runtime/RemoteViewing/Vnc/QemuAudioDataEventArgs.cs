#region License
/*
Plunderludics.RemoteViewing — QEMU Audio RFB extension
Based on RemoteViewing (BSD-2-Clause). See LICENSE in package root.
*/
#endregion

using System;

namespace Plunderludics.RemoteViewing.Vnc
{
    /// <summary>
    /// PCM payload received from a QEMU VNC server via the Audio RFB extension.
    /// </summary>
    public sealed class QemuAudioDataEventArgs : EventArgs
    {
        public QemuAudioDataEventArgs(byte[] data, QemuAudioFormat format)
        {
            this.Data = data ?? throw new ArgumentNullException(nameof(data));
            this.Format = format;
        }

        /// <summary>
        /// Interleaved PCM samples. Ownership is transferred to the subscriber; the client
        /// will not reuse this buffer.
        /// </summary>
        public byte[] Data { get; }

        public QemuAudioFormat Format { get; }
    }
}
