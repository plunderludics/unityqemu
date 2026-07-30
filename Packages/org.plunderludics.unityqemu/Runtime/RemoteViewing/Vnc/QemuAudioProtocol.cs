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
    /// Pure helpers for QEMU Audio RFB client/server messages (big-endian where RFB requires).
    /// </summary>
    /// <remarks>
    /// Client ops: enable=0, disable=1, set-format=2.
    /// Server ops: end=0, begin=1, data=2.
    /// Message type 255, audio submessage type 1. Encoding -259 / 0xFFFFFEFD.
    /// </remarks>
    public static class QemuAudioProtocol
    {
        public const byte QemuMessageType = 255;
        public const byte AudioSubmessageType = 1;

        public const ushort ClientOpEnable = 0;
        public const ushort ClientOpDisable = 1;
        public const ushort ClientOpSetFormat = 2;

        public const ushort ServerOpEnd = 0;
        public const ushort ServerOpBegin = 1;
        public const ushort ServerOpData = 2;

        public static readonly VncEncoding PseudoEncoding = VncEncoding.QemuAudio;

        public static byte[] BuildEnableMessage()
        {
            var p = new byte[4];
            p[0] = QemuMessageType;
            p[1] = AudioSubmessageType;
            VncUtility.EncodeUInt16BE(p, 2, ClientOpEnable);
            return p;
        }

        public static byte[] BuildDisableMessage()
        {
            var p = new byte[4];
            p[0] = QemuMessageType;
            p[1] = AudioSubmessageType;
            VncUtility.EncodeUInt16BE(p, 2, ClientOpDisable);
            return p;
        }

        public static byte[] BuildSetFormatMessage(QemuAudioFormat format)
        {
            var p = new byte[10];
            p[0] = QemuMessageType;
            p[1] = AudioSubmessageType;
            VncUtility.EncodeUInt16BE(p, 2, ClientOpSetFormat);
            p[4] = (byte)format.SampleFormat;
            p[5] = (byte)format.Channels;
            VncUtility.EncodeUInt32BE(p, 6, (uint)format.Frequency);
            return p;
        }
    }
}
