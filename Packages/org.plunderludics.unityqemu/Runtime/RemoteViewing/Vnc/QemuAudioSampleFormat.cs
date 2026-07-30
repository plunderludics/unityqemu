#region License
/*
Plunderludics.RemoteViewing — QEMU Audio RFB extension
Based on RemoteViewing (BSD-2-Clause). See LICENSE in package root.
*/
#endregion

namespace Plunderludics.RemoteViewing.Vnc
{
    /// <summary>
    /// Sample formats for the QEMU Audio RFB extension.
    /// </summary>
    /// <seealso href="https://github.com/rfbproto/rfbproto/blob/master/rfbproto.rst#qemu-audio-client-message"/>
    public enum QemuAudioSampleFormat : byte
    {
        U8 = 0,
        S8 = 1,
        U16 = 2,
        S16 = 3,
        U32 = 4,
        S32 = 5,
    }
}
