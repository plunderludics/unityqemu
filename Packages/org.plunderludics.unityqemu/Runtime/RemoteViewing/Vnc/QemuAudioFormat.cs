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
    /// PCM format negotiated with a QEMU VNC server over the Audio RFB extension.
    /// </summary>
    public readonly struct QemuAudioFormat : IEquatable<QemuAudioFormat>
    {
        /// <summary>
        /// Default format matching QEMU's VNC audio defaults (S16 stereo 44100 Hz).
        /// </summary>
        public static QemuAudioFormat Default { get; } = new QemuAudioFormat(QemuAudioSampleFormat.S16, 2, 44100);

        public QemuAudioFormat(QemuAudioSampleFormat sampleFormat, int channels, int frequency)
        {
            if (channels != 1 && channels != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1 (mono) or 2 (stereo).");
            }

            if (frequency <= 0 || frequency > 48000)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be in 1..48000 Hz (QEMU client limit).");
            }

            this.SampleFormat = sampleFormat;
            this.Channels = channels;
            this.Frequency = frequency;
        }

        public QemuAudioSampleFormat SampleFormat { get; }

        public int Channels { get; }

        public int Frequency { get; }

        /// <summary>Bytes per single-channel sample.</summary>
        public int BytesPerSample
        {
            get
            {
                switch (this.SampleFormat)
                {
                    case QemuAudioSampleFormat.U8:
                    case QemuAudioSampleFormat.S8:
                        return 1;
                    case QemuAudioSampleFormat.U16:
                    case QemuAudioSampleFormat.S16:
                        return 2;
                    case QemuAudioSampleFormat.U32:
                    case QemuAudioSampleFormat.S32:
                        return 4;
                    default:
                        throw new InvalidOperationException("Unknown sample format.");
                }
            }
        }

        /// <summary>Bytes per interleaved frame (all channels).</summary>
        public int BytesPerFrame => this.BytesPerSample * this.Channels;

        public bool Equals(QemuAudioFormat other) =>
            this.SampleFormat == other.SampleFormat
            && this.Channels == other.Channels
            && this.Frequency == other.Frequency;

        public override bool Equals(object obj) => obj is QemuAudioFormat other && this.Equals(other);

        public override int GetHashCode() =>
            ((int)this.SampleFormat * 397) ^ (this.Channels * 31) ^ this.Frequency;

        public override string ToString() =>
            $"{this.SampleFormat} {this.Channels}ch {this.Frequency}Hz";

        public static bool operator ==(QemuAudioFormat left, QemuAudioFormat right) => left.Equals(right);

        public static bool operator !=(QemuAudioFormat left, QemuAudioFormat right) => !left.Equals(right);
    }
}
