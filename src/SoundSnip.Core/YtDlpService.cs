using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoundSnip.Core;

/// <summary>Talks to yt-dlp: look a video up, then download its best audio stream untouched.</summary>
public sealed class YtDlpService
{
    private readonly ToolManager _tools;
    public YtDlpService(ToolManager tools) => _tools = tools;

    public static bool TryNormalizeUrl(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!t.Contains("://")) t = "https://" + t; // allow "youtu.be/xyz" without scheme
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!u.Host.Contains('.')) return false;
        url = u.AbsoluteUri;
        return true;
    }

    private IEnumerable<string> CommonArgs()
    {
        yield return "--no-playlist";
        yield return "--no-warnings";
        yield return "--no-update";
        yield return "--color"; yield return "never";
        // "bestaudio" = yt-dlp's own ranking of the audio-only streams the site offers (Opus beats AAC at equal bitrate).
        yield return "-f"; yield return "bestaudio/best";
        var js = _tools.JsRuntimeArgument;
        if (js is not null) { yield return "--js-runtimes"; yield return js; }
    }

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        var args = new List<string> { "-J" };
        args.AddRange(CommonArgs());
        args.Add("--"); args.Add(url); // "--" so a link can never be mistaken for an option

        var r = await ProcessRunner.RunAsync(_tools.YtDlpPath, args, ct: ct);
        if (r.ExitCode != 0) throw MakeException(r.StdErr);

        var json = r.StdOut.Trim();
        var brace = json.IndexOf('{');
        if (brace < 0) throw new YtDlpException("yt-dlp returned nothing readable for that link.", looksOutdated: true);
        try { return Parse(json[brace..]); }
        catch (JsonException ex)
        {
            Log.Write("Bad yt-dlp JSON: " + ex.Message);
            throw new YtDlpException("Couldn't understand yt-dlp's answer for that link.", looksOutdated: true);
        }
    }

    private static VideoInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        if (Str(r, "_type") == "playlist")
            throw new UserFacingException("That link is a playlist. Paste the link of a single video instead.");
        if (Bool(r, "is_live"))
            throw new UserFacingException("Live streams can't be downloaded until they've ended.");

        var id = Str(r, "id") ?? throw new UserFacingException("yt-dlp didn't report a video id.");
        string? thumb = null;
        if (r.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            // WPF can't show .webp, so prefer a jpg/png (the list is ordered worst → best).
            foreach (var t in thumbs.EnumerateArray().Reverse())
            {
                var u = Str(t, "url");
                if (u is null) continue;
                var path = u.Split('?')[0].ToLowerInvariant();
                if (path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png")) { thumb = u; break; }
            }
        }
        thumb ??= Str(r, "thumbnail");

        return new VideoInfo(
            Id: id,
            Title: Str(r, "title") ?? id,
            Uploader: Str(r, "uploader") ?? Str(r, "channel") ?? "",
            DurationSeconds: Num(r, "duration") ?? 0,
            ThumbnailUrl: thumb,
            AudioCodec: Str(r, "acodec"),
            AudioBitrateKbps: Num(r, "abr"),
            SampleRate: (int?)Num(r, "asr"),
            SourceExtension: Str(r, "ext"));
    }

    /// <summary>Downloads the best audio stream as-is (no re-encoding) to <paramref name="dir"/>\source.&lt;ext&gt;.</summary>
    public async Task<string> DownloadAudioAsync(string url, string dir, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "source.*")) { try { File.Delete(old); } catch { } }

        var args = new List<string>
        {
            "--newline", "--progress", "--no-mtime",
            "--progress-template",
            "download:SNP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
            "-o", Path.Combine(dir, "source.%(ext)s"),
        };
        args.AddRange(CommonArgs());
        args.Add("--"); args.Add(url);

        var r = await ProcessRunner.RunAsync(_tools.YtDlpPath, args, onStdOutLine: line =>
        {
            if (progress is null || !line.StartsWith("SNP|", StringComparison.Ordinal)) return;
            var f = line.Split('|');
            if (f.Length < 6) return;
            var done = (long?)D(f[1]) ?? 0;
            var total = (long?)D(f[2]) ?? (long?)D(f[3]);
            progress.Report(new DownloadProgress(
                total is > 0 ? Math.Clamp((double)done / total.Value, 0, 1) : 0,
                D(f[4]), D(f[5]), done, total));
        }, ct: ct);

        if (r.ExitCode != 0) throw MakeException(r.StdErr);

        var file = Directory.EnumerateFiles(dir, "source.*")
            .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                              && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                              && !f.EndsWith(".temp", StringComparison.OrdinalIgnoreCase));
        return file ?? throw new UserFacingException("The download finished but no audio file was produced.");
    }

    // ----- error handling ------------------------------------------------------------------

    private static readonly string[] ContentProblems =
    {
        "unavailable", "private video", "sign in", "age", "copyright", "members", "removed", "terminated",
        "country", "geo", "premiere", "live event", "is not a valid url", "unsupported url", "no video formats",
        "has been blocked", "account associated"
    };
    private static readonly string[] NetworkProblems =
    {
        "getaddrinfo", "timed out", "temporary failure", "connection", "unable to download", "network is unreachable",
        "ssl", "name resolution"
    };

    private static YtDlpException MakeException(string stderr)
    {
        Log.Write("yt-dlp stderr: " + stderr.Trim());
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var err = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault() ?? "yt-dlp failed for an unknown reason.";
        var msg = Regex.Replace(err, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]{6,}:\s*)?", "", RegexOptions.IgnoreCase).Trim();
        if (msg.Length == 0) msg = err;
        var low = msg.ToLowerInvariant();

        if (NetworkProblems.Any(low.Contains))
            return new YtDlpException("Couldn't reach YouTube. Check your internet connection and try again.", looksOutdated: false);
        bool content = ContentProblems.Any(low.Contains);
        return new YtDlpException(msg, looksOutdated: !content);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;
}
