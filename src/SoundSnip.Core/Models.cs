namespace SoundSnip.Core;

public sealed record VideoInfo(
    string Id,
    string Title,
    string Uploader,
    double DurationSeconds,
    string? ThumbnailUrl,
    string? AudioCodec,
    double? AudioBitrateKbps,
    int? SampleRate,
    string? SourceExtension)
{
    public string CodecName
    {
        get
        {
            var c = (AudioCodec ?? "").ToLowerInvariant();
            if (c.StartsWith("opus")) return "Opus";
            if (c.StartsWith("mp4a.40.5") || c.StartsWith("mp4a.40.29")) return "HE-AAC";
            if (c.StartsWith("mp4a")) return "AAC";
            if (c.StartsWith("vorbis")) return "Vorbis";
            if (c.StartsWith("mp3") || c.StartsWith("mp4a.6b")) return "MP3";
            if (c.StartsWith("flac")) return "FLAC";
            return string.IsNullOrWhiteSpace(c) || c == "none" ? "Audio" : c.ToUpperInvariant();
        }
    }

    /// <summary>"Opus · 129 kbps · 48 kHz"</summary>
    public string QualityDescription
    {
        get
        {
            var parts = new List<string> { CodecName };
            if (AudioBitrateKbps is > 0) parts.Add($"{AudioBitrateKbps:0} kbps");
            if (SampleRate is > 0) parts.Add($"{SampleRate / 1000.0:0.#} kHz");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>File extension that can hold the untouched audio stream (used by "Original" export).</summary>
    public string OriginalExtension
    {
        get
        {
            var c = (AudioCodec ?? "").ToLowerInvariant();
            if (c.StartsWith("opus")) return "opus";
            if (c.StartsWith("mp4a") && !c.StartsWith("mp4a.6b")) return "m4a";
            if (c.StartsWith("vorbis")) return "ogg";
            if (c.StartsWith("mp3") || c.StartsWith("mp4a.6b")) return "mp3";
            if (c.StartsWith("flac")) return "flac";
            return (SourceExtension ?? "").ToLowerInvariant() switch
            {
                "m4a" or "mp4" => "m4a",
                "webm" => "opus",
                "opus" => "opus",
                "ogg" => "ogg",
                "mp3" => "mp3",
                "flac" => "flac",
                _ => "mka",
            };
        }
    }
}

public readonly record struct DownloadProgress(double Fraction, double? BytesPerSecond, double? EtaSeconds, long DownloadedBytes, long? TotalBytes);

public readonly record struct SetupProgress(string Message, double Fraction);

public enum ExportFormat { Original, Mp3, M4a, Flac, Wav }

public sealed record ExportOptions(
    ExportFormat Format,
    double StartSeconds,
    double EndSeconds,
    bool FullRange,
    double FadeInSeconds,
    double FadeOutSeconds,
    string? Title,
    string? Artist);
