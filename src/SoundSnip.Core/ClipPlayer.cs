using NAudio.Wave;

namespace SoundSnip.Core;

/// <summary>Plays one range of the decoded preview, applying the same linear fades the export will apply.</summary>
internal sealed class ClipSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly long _totalFrames;
    private readonly long _fadeInFrames;
    private readonly long _fadeOutFrames;
    private long _framesDone;

    public ClipSampleProvider(ISampleProvider source, long totalFrames, long fadeInFrames, long fadeOutFrames)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _totalFrames = Math.Max(0, totalFrames);
        _fadeInFrames = Math.Max(0, fadeInFrames);
        _fadeOutFrames = Math.Max(0, fadeOutFrames);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        long remaining = (_totalFrames - _framesDone) * _channels;
        if (remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, remaining);
        int read = _source.Read(buffer, offset, toRead);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            long idx = _framesDone + f;
            float gain = 1f;
            if (_fadeInFrames > 0 && idx < _fadeInFrames) gain = (float)idx / _fadeInFrames;
            long fromEnd = _totalFrames - idx;
            if (_fadeOutFrames > 0 && fromEnd <= _fadeOutFrames) gain *= (float)fromEnd / _fadeOutFrames;
            if (gain < 1f)
                for (int c = 0; c < _channels; c++) buffer[offset + f * _channels + c] *= gain;
        }
        _framesDone += frames;
        return read;
    }
}

public sealed class ClipPlayer : IDisposable
{
    private readonly string _wavPath;
    private WaveOutEvent? _out;
    private WaveFileReader? _reader;
    private double _start;
    private double _length;
    private int _generation;
    private bool _stopRequested;

    /// <summary>Raised on the thread that created the player (the UI thread). true = the clip played to its end.</summary>
    public event Action<bool>? Ended;

    public bool IsPlaying { get; private set; }
    public double TotalSeconds { get; }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); if (_out != null) _out.Volume = _volume; }
    }

    public ClipPlayer(string wavPath)
    {
        _wavPath = wavPath;
        using var probe = WavFiles.Open(wavPath);
        TotalSeconds = probe.TotalTime.TotalSeconds;
    }

    /// <summary>Absolute time in the source that is being heard right now.</summary>
    public double Position
    {
        get
        {
            if (!IsPlaying || _out is null) return _start;
            double played = _out.GetPosition() / (double)_out.OutputWaveFormat.AverageBytesPerSecond;
            return _start + Math.Min(played, _length);
        }
    }

    public void Play(double start, double end, double fadeInSeconds, double fadeOutSeconds)
    {
        Stop();
        start = Math.Clamp(start, 0, TotalSeconds);
        end = Math.Clamp(end, start, TotalSeconds);
        if (end - start < 0.01) { Ended?.Invoke(true); return; }

        _reader = WavFiles.Open(_wavPath);
        _reader.CurrentTime = TimeSpan.FromSeconds(start);
        int rate = _reader.WaveFormat.SampleRate;
        var clip = new ClipSampleProvider(_reader.ToSampleProvider(),
            totalFrames: (long)Math.Round((end - start) * rate),
            fadeInFrames: (long)Math.Round(fadeInSeconds * rate),
            fadeOutFrames: (long)Math.Round(fadeOutSeconds * rate));

        _start = start;
        _length = end - start;
        _stopRequested = false;
        int gen = ++_generation;

        var output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3, Volume = _volume };
        output.PlaybackStopped += (_, _) =>
        {
            if (gen != _generation) return; // a newer Play/Stop already took over
            bool natural = !_stopRequested;
            IsPlaying = false;
            Release();
            Ended?.Invoke(natural);
        };
        _out = output;
        output.Init(clip);
        IsPlaying = true;
        output.Play();
    }

    public void Stop()
    {
        if (_out is null) { IsPlaying = false; return; }
        _stopRequested = true;
        _generation++; // ignore the PlaybackStopped of the output we're about to kill
        IsPlaying = false;
        Release();
    }

    private void Release()
    {
        var o = _out; var r = _reader;
        _out = null; _reader = null;
        try { o?.Stop(); } catch { }
        try { o?.Dispose(); } catch { }
        try { r?.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
