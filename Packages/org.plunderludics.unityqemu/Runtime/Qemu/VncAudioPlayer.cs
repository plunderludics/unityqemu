using System;
using System.Threading;
using Plunderludics.RemoteViewing.Vnc;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Plays QEMU VNC PCM into Unity via a ring buffer and <see cref="OnAudioFilterRead"/>.
/// AudioSource volume / pitch / pan are applied here (Unity does not apply them after a
/// custom filter that replaces the buffer). PCM may arrive from the VNC thread.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class VncAudioPlayer : MonoBehaviour
{
    const int RingSeconds = 2;

    readonly object _lock = new object();
    float[] _ring;
    int _write;
    int _read;
    int _count;
    int _channels = 2;
    int _qemuFrequency = 44100;
    bool _formatReady;

    // Fractional read cursor in *frames* (for pitch). Advanced under _lock.
    double _readFrame;

    AudioSource _source;
    int _unitySampleRate = 48000;
    volatile bool _acceptingPcm;
    int _startPlaybackRequested;

    // Cached on main thread — audio thread must not touch AudioSource.
    volatile float _volume = 1f;
    volatile float _pitch = 1f;
    volatile float _pan = 0f;

    public bool IsPlaying => _source != null && _source.isPlaying;

    void Awake() => EnsureSource();

    void OnEnable()
    {
        EnsureSource();
        CacheSourceControls();
        RequestStartPlayback();
    }

    void OnDisable()
    {
        _acceptingPcm = false;
        if (_source != null && _source.isPlaying)
            _source.Stop();
        Clear();
    }

    void Update() => MainThreadTick();

    public void MainThreadTick()
    {
        CacheSourceControls();
        if (Interlocked.Exchange(ref _startPlaybackRequested, 0) == 1)
            StartPlaybackMainThread();
    }

    void CacheSourceControls()
    {
        if (_source == null)
            return;
        _volume = _source.volume;
        float p = _source.pitch;
        if (p < 0.05f) p = 0.05f;
        if (p > 3f) p = 3f;
        _pitch = p;
        _pan = Mathf.Clamp(_source.panStereo, -1f, 1f);
    }

    void EnsureSource()
    {
        if (_source != null)
            return;
        _source = GetComponent<AudioSource>();
        if (_source == null)
            _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _unitySampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        if (_source.clip == null)
        {
            _source.clip = AudioClip.Create(
                "UnityQemuSilent", _unitySampleRate, 2, _unitySampleRate, false);
        }
    }

    public void RequestStartPlayback()
    {
        Interlocked.Exchange(ref _startPlaybackRequested, 1);
    }

    public void StartPlayback() => StartPlaybackMainThread();

    void StartPlaybackMainThread()
    {
        EnsureSource();
        CacheSourceControls();
        _acceptingPcm = true;
        if (!_source.isPlaying)
            _source.Play();
    }

    public void StopPlayback()
    {
        _acceptingPcm = false;
        Interlocked.Exchange(ref _startPlaybackRequested, 0);
        if (_source != null && _source.isPlaying)
            _source.Stop();
        Clear();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _write = 0;
            _read = 0;
            _count = 0;
            _readFrame = 0;
        }
    }

    public void PushPcm(byte[] data, QemuAudioFormat format)
    {
        if (!_acceptingPcm || data == null || data.Length == 0)
            return;

        EnsureRing(format.Channels, format.Frequency);

        float[] converted = ConvertToFloatInterleaved(data, format);
        if (converted == null || converted.Length == 0)
            return;

        int unityRate = _unitySampleRate > 0 ? _unitySampleRate : format.Frequency;
        float[] toWrite = converted;
        if (unityRate != format.Frequency && format.Frequency > 0)
            toWrite = ResampleLinear(converted, format.Channels, format.Frequency, unityRate);

        lock (_lock)
        {
            for (int i = 0; i < toWrite.Length; i++)
            {
                if (_count >= _ring.Length)
                {
                    // Drop one frame (all channels) so _readFrame stays aligned.
                    int drop = _channels;
                    if (drop > _count)
                        drop = _count;
                    _read = (_read + drop) % _ring.Length;
                    _count -= drop;
                    if (_readFrame >= 1.0)
                        _readFrame -= 1.0;
                    else
                        _readFrame = 0;
                }
                _ring[_write] = toWrite[i];
                _write = (_write + 1) % _ring.Length;
                _count++;
            }
        }
    }

    void EnsureRing(int channels, int qemuFrequency)
    {
        int unityRate = _unitySampleRate > 0 ? _unitySampleRate : qemuFrequency;
        int need = Math.Max(unityRate, qemuFrequency) * Math.Max(channels, 1) * RingSeconds;
        lock (_lock)
        {
            if (_ring != null && _ring.Length >= need &&
                _channels == channels && _qemuFrequency == qemuFrequency && _formatReady)
                return;

            _ring = new float[need];
            _write = 0;
            _read = 0;
            _count = 0;
            _readFrame = 0;
            _channels = Math.Max(1, channels);
            _qemuFrequency = qemuFrequency;
            _formatReady = true;
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        float volume = _volume;
        float pitch = _pitch;
        float pan = _pan;
        // Constant-power pan (-1..+1).
        float angle = (pan + 1f) * 0.25f * Mathf.PI;
        float panL = Mathf.Cos(angle) * volume;
        float panR = Mathf.Sin(angle) * volume;

        lock (_lock)
        {
            int outCh = Math.Max(channels, 1);
            int frames = data.Length / outCh;
            int availableFrames = _channels > 0 ? _count / _channels : 0;

            for (int f = 0; f < frames; f++)
            {
                float left = 0f;
                float right = 0f;

                if (_ring != null && availableFrames > 0)
                {
                    // Fractional frame read for pitch.
                    int i0 = (int)_readFrame;
                    if (i0 >= availableFrames)
                        i0 = availableFrames - 1;
                    int i1 = i0 + 1;
                    if (i1 >= availableFrames)
                        i1 = i0;
                    float t = (float)(_readFrame - i0);

                    SampleFrame(i0, out float l0, out float r0);
                    SampleFrame(i1, out float l1, out float r1);
                    left = l0 + (l1 - l0) * t;
                    right = r0 + (r1 - r0) * t;

                    _readFrame += pitch;
                    while (_readFrame >= 1.0 && availableFrames > 0)
                    {
                        _readFrame -= 1.0;
                        DiscardFrame();
                        availableFrames--;
                    }
                }

                left *= panL;
                right *= panR;

                if (outCh == 1)
                {
                    data[f] = 0.5f * (left + right);
                }
                else
                {
                    int i = f * outCh;
                    data[i] = left;
                    data[i + 1] = right;
                    for (int c = 2; c < outCh; c++)
                        data[i + c] = 0f;
                }
            }
        }
    }

    void SampleFrame(int frameIndex, out float left, out float right)
    {
        int sampleIndex = (_read + frameIndex * _channels) % _ring.Length;
        left = _ring[sampleIndex];
        if (_channels > 1)
            right = _ring[(sampleIndex + 1) % _ring.Length];
        else
            right = left;
    }

    void DiscardFrame()
    {
        int drop = _channels;
        if (drop > _count)
            drop = _count;
        _read = (_read + drop) % _ring.Length;
        _count -= drop;
    }

    static float[] ConvertToFloatInterleaved(byte[] data, QemuAudioFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BytesPerSample;
        int frameBytes = bytesPerSample * channels;
        if (frameBytes <= 0 || data.Length < frameBytes)
            return null;

        int frames = data.Length / frameBytes;
        var samples = new float[frames * channels];
        int o = 0;
        int i = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                float s;
                switch (format.SampleFormat)
                {
                    case QemuAudioSampleFormat.U8:
                        s = (data[i] - 128) / 128f;
                        i += 1;
                        break;
                    case QemuAudioSampleFormat.S8:
                        s = ((sbyte)data[i]) / 128f;
                        i += 1;
                        break;
                    case QemuAudioSampleFormat.U16:
                    {
                        ushort u = (ushort)(data[i] | (data[i + 1] << 8));
                        s = (u - 32768) / 32768f;
                        i += 2;
                        break;
                    }
                    case QemuAudioSampleFormat.S16:
                    {
                        short v = (short)(data[i] | (data[i + 1] << 8));
                        s = v / 32768f;
                        i += 2;
                        break;
                    }
                    case QemuAudioSampleFormat.U32:
                    {
                        uint u = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
                        s = (u - 2147483648f) / 2147483648f;
                        i += 4;
                        break;
                    }
                    default:
                    {
                        int v = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24);
                        s = v / 2147483648f;
                        i += 4;
                        break;
                    }
                }
                if (s < -1f) s = -1f;
                else if (s > 1f) s = 1f;
                samples[o++] = s;
            }
        }
        return samples;
    }

    static float[] ResampleLinear(float[] input, int channels, int srcRate, int dstRate)
    {
        int srcFrames = input.Length / channels;
        int dstFrames = Math.Max(1, (int)((long)srcFrames * dstRate / srcRate));
        var output = new float[dstFrames * channels];
        for (int df = 0; df < dstFrames; df++)
        {
            double srcPos = (double)df * srcRate / dstRate;
            int i0 = (int)srcPos;
            int i1 = Math.Min(i0 + 1, srcFrames - 1);
            float t = (float)(srcPos - i0);
            for (int c = 0; c < channels; c++)
            {
                float a = input[i0 * channels + c];
                float b = input[i1 * channels + c];
                output[df * channels + c] = a + (b - a) * t;
            }
        }
        return output;
    }
}
}
