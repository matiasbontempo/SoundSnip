using System.Globalization;

namespace SoundSnip.Core;

/// <summary>Decodes audio for preview and writes the final trimmed file.</summary>
public sealed class FfmpegService
{
    private readonly ToolManager _tools;
    public FfmpegService(ToolManager tools) => _tools = tools;

    private static string Inv(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    public static string ExtensionFor(ExportFormat format, VideoInfo info) => format switch
    {
        ExportFormat.Original => info.OriginalExtension,
        ExportFormat.Mp3 => "mp3",
        ExportFormat.M4a => "m4a",
        ExportFormat.Flac => "flac",
        ExportFormat.Wav => "wav",
        _ => "bin",
    };

    /// <summary>
    /// Decodes the downloaded stream to a plain PCM .wav used for the waveform and for instant, sample-accurate previews.
    /// Very long sources use a lighter mono/22 kHz preview so the temp file stays reasonable; exports never use this file.
    /// </summary>
    public async Task DecodePreviewAsync(string source, string wavPath, double durationHint, IProgress<double>? progress, CancellationToken ct)
    {
        bool lightweight = durationHint > 90 * 60;
        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-i", source, "-vn", "-map", "0:a:0",
        };
        if (lightweight) args.AddRange(new[] { "-ac", "1", "-ar", "22050" });
        else args.AddRange(new[] { "-ac", "2" });
        args.AddRange(new[] { "-c:a", "pcm_s16le", wavPath });

        var r = await ProcessRunner.RunAsync(_tools.FfmpegPath, args,
            onStdOutLine: line => ReportProgress(line, durationHint, progress), ct: ct);
        if (r.ExitCode != 0 || !File.Exists(wavPath))
            throw new UserFacingException("Couldn't prepare the audio for preview. " + LastLines(r.StdErr));
    }

    /// <summary>Writes the selected range to <paramref name="destPath"/>. Returns the final path.</summary>
    public async Task<string> ExportAsync(string source, string destPath, ExportOptions o, IProgress<double>? progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(destPath).TrimStart('.');
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        Directory.CreateDirectory(dir);
        // Write next to the destination under a temp name so a failed/cancelled export never leaves a broken file behind.
        var tmp = Path.Combine(dir, $".soundsnip-{Guid.NewGuid():N}.{ext}");

        double length = o.FullRange ? double.NaN : Math.Max(0.01, o.EndSeconds - o.StartSeconds);
        bool copy = o.Format == ExportFormat.Original;

        var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

        // Re-encoding: seek on the input side (accurate, fast). Stream copy: read from the start and drop packets
        // before the cut point, which lands on the exact packet boundary instead of an earlier cue point.
        if (!o.FullRange && !copy) args.AddRange(new[] { "-ss", Inv(o.StartSeconds) });
        args.AddRange(new[] { "-i", source });
        if (!o.FullRange && !copy) args.AddRange(new[] { "-t", Inv(length) });
        if (!o.FullRange && copy) args.AddRange(new[] { "-ss", Inv(o.StartSeconds), "-t", Inv(length) });

        args.AddRange(new[] { "-vn", "-map", "0:a:0", "-map_metadata", "-1" });

        if (!copy)
        {
            var filters = new List<string>();
            double effLen = o.FullRange ? double.NaN : length;
            if (o.FadeInSeconds > 0.001)
                filters.Add($"afade=t=in:st=0:d={Inv(o.FadeInSeconds)}");
            if (o.FadeOutSeconds > 0.001 && !double.IsNaN(effLen))
                filters.Add($"afade=t=out:st={Inv(Math.Max(0, effLen - o.FadeOutSeconds))}:d={Inv(o.FadeOutSeconds)}");
            if (filters.Count > 0) args.AddRange(new[] { "-af", string.Join(",", filters) });
        }

        switch (o.Format)
        {
            case ExportFormat.Original:
                args.AddRange(new[] { "-c:a", "copy" });
                if (ext == "m4a") args.AddRange(new[] { "-movflags", "+faststart" });
                break;
            case ExportFormat.Mp3:
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3" });
                break;
            case ExportFormat.M4a:
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart" });
                break;
            case ExportFormat.Flac:
                args.AddRange(new[] { "-c:a", "flac", "-compression_level", "8" });
                break;
            case ExportFormat.Wav:
                args.AddRange(new[] { "-c:a", "pcm_s16le" });
                break;
        }

        if (!string.IsNullOrWhiteSpace(o.Title)) args.AddRange(new[] { "-metadata", "title=" + o.Title });
        if (!string.IsNullOrWhiteSpace(o.Artist)) args.AddRange(new[] { "-metadata", "artist=" + o.Artist });
        args.Add(tmp);

        double expected = double.IsNaN(length) ? 0 : length;
        try
        {
            var r = await ProcessRunner.RunAsync(_tools.FfmpegPath, args,
                onStdOutLine: line => ReportProgress(line, expected, progress), ct: ct);
            if (r.ExitCode != 0 || !File.Exists(tmp))
                throw new UserFacingException("Export failed. " + LastLines(r.StdErr));
            File.Move(tmp, destPath, overwrite: true);
            progress?.Report(1);
            return destPath;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void ReportProgress(string line, double totalSeconds, IProgress<double>? progress)
    {
        if (progress is null || totalSeconds <= 0) return;
        // ffmpeg -progress prints out_time_us (and a legacy out_time_ms that is also microseconds).
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            var v = line[(line.IndexOf('=') + 1)..];
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var us) && us >= 0)
                progress.Report(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 0.99));
        }
    }

    private static string LastLines(string stderr)
    {
        Log.Write("ffmpeg stderr: " + stderr.Trim());
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(2));
    }
}
