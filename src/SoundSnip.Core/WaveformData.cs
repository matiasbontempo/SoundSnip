using NAudio.Wave;

namespace SoundSnip.Core;

/// <summary>Min/max amplitude per small time bucket, so the waveform can be drawn at any zoom level.</summary>
public sealed class WaveformData
{
    public double DurationSeconds { get; }
    public int SampleRate { get; }
    public int BucketSize { get; }
    public float[] Min { get; }
    public float[] Max { get; }
    /// <summary>Scale so the loudest peak fills most of the height (quiet recordings stay readable).</summary>
    public float DisplayGain { get; }

    public double BucketsPerSecond => SampleRate / (double)BucketSize;
    public int BucketCount => Min.Length;

    private WaveformData(double duration, int sampleRate, int bucketSize, float[] min, float[] max, float gain)
    {
        DurationSeconds = duration; SampleRate = sampleRate; BucketSize = bucketSize; Min = min; Max = max; DisplayGain = gain;
    }

    public static WaveformData Build(string wavPath, CancellationToken ct)
    {
        using var wave = WavFiles.Open(wavPath);
        var reader = wave.ToSampleProvider();
        int sr = wave.WaveFormat.SampleRate;
        int ch = wave.WaveFormat.Channels;
        int bucket = sr >= 32000 ? 128 : 64;

        long totalFrames = wave.Length / wave.WaveFormat.BlockAlign;
        int est = (int)Math.Min(int.MaxValue / 2, totalFrames / bucket + 2);
        var min = new float[est];
        var max = new float[est];
        int bi = 0;

        var buf = new float[bucket * ch * 256];
        float curMin = float.MaxValue, curMax = float.MinValue, peak = 0;
        int inBucket = 0;
        long frames = 0;
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = read / ch;
            for (int f = 0; f < n; f++)
            {
                float s = 0;
                int o = f * ch;
                for (int c = 0; c < ch; c++) s += buf[o + c];
                s /= ch;
                if (s < curMin) curMin = s;
                if (s > curMax) curMax = s;
                if (++inBucket == bucket)
                {
                    if (bi < est) { min[bi] = curMin; max[bi] = curMax; bi++; }
                    peak = Math.Max(peak, Math.Max(Math.Abs(curMin), Math.Abs(curMax)));
                    curMin = float.MaxValue; curMax = float.MinValue; inBucket = 0;
                }
            }
            frames += n;
        }
        if (inBucket > 0 && bi < est)
        {
            min[bi] = curMin; max[bi] = curMax; bi++;
            peak = Math.Max(peak, Math.Max(Math.Abs(curMin), Math.Abs(curMax)));
        }

        if (bi == 0) throw new UserFacingException("The audio appears to be empty.");
        Array.Resize(ref min, bi);
        Array.Resize(ref max, bi);
        float gain = peak > 0.0001f ? Math.Min(8f, 0.95f / peak) : 1f;
        return new WaveformData(frames / (double)sr, sr, bucket, min, max, gain);
    }
}
