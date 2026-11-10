using System.IO.Compression;

namespace SoundSnip.Core;

/// <summary>
/// Owns the helper programs SoundSnip needs (yt-dlp, ffmpeg, optionally a JS runtime).
/// Everything lives in a per-user folder, so nothing is installed system-wide and no admin rights are needed.
/// Environment overrides (mostly for development): SOUNDSNIP_YTDLP, SOUNDSNIP_FFMPEG, SOUNDSNIP_JSRUNTIME.
/// </summary>
public sealed class ToolManager
{
    // Official release locations. Each download is verified against a checksum published next to it.
    private const string YtDlpBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const string DenoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private sealed record FfmpegSource(string ZipUrl, string ChecksumUrl);
    private static readonly FfmpegSource[] FfmpegSources =
    {
        new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256"),
        new("https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
            "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/checksums.sha256"),
    };

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public string YtDlpPath => Env("SOUNDSNIP_YTDLP") ?? Path.Combine(AppPaths.ToolsDir, "yt-dlp", "yt-dlp.exe");
    public string FfmpegPath => Env("SOUNDSNIP_FFMPEG") ?? Path.Combine(AppPaths.ToolsDir, "ffmpeg", "ffmpeg.exe");
    public string DenoPath => Path.Combine(AppPaths.ToolsDir, "deno", "deno.exe");

    public bool HasYtDlp => File.Exists(YtDlpPath);
    public bool HasFfmpeg => File.Exists(FfmpegPath);
    public bool RequiredToolsReady => HasYtDlp && HasFfmpeg;
    private bool YtDlpIsExternal => Env("SOUNDSNIP_YTDLP") is not null;

    /// <summary>Value for yt-dlp's --js-runtimes (e.g. "deno:C:\...\deno.exe"), or null if none is available.</summary>
    public string? JsRuntimeArgument
    {
        get
        {
            var o = Env("SOUNDSNIP_JSRUNTIME");
            if (o is not null) return o;
            if (File.Exists(DenoPath)) return $"deno:{DenoPath}";
            foreach (var name in new[] { "deno", "node", "bun" })
                if (FindOnPath(name) is { } p) return $"{name}:{p}";
            return null;
        }
    }

    private static string? FindOnPath(string baseName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), baseName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    // ----- first-run setup -----------------------------------------------------------------

    /// <summary>Downloads whatever required tool is missing. Safe to call every launch (no-op when ready).</summary>
    public async Task EnsureRequiredAsync(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        // yt-dlp is ~24 MB and ffmpeg ~115 MB, so weight the bar accordingly.
        const double ytShare = 0.17;
        if (!HasYtDlp)
        {
            await InstallYtDlpAsync((msg, f) => progress.Report(new SetupProgress(msg, f * ytShare)), ct);
        }
        if (!HasFfmpeg)
        {
            double offset = HasYtDlp ? ytShare : 0;
            await InstallFfmpegAsync((msg, f) => progress.Report(new SetupProgress(msg, offset + f * (1 - offset))), ct);
        }
        progress.Report(new SetupProgress("Ready", 1));
    }

    /// <summary>Best-effort: fetch a JS runtime so YouTube extraction keeps working as YouTube evolves. Never throws.</summary>
    public async Task TryInstallJsRuntimeAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(DenoPath) || Env("SOUNDSNIP_JSRUNTIME") is not null) return;
            var dir = Path.GetDirectoryName(DenoPath)!;
            Directory.CreateDirectory(dir);
            var sums = await VerifiedDownloader.GetTextAsync(DenoZipUrl + ".sha256sum", ct);
            var hash = VerifiedDownloader.FindHash(sums, Path.GetFileName(DenoZipUrl));
            if (hash is null)
            {
                Log.Write("Optional JS runtime install skipped: couldn't read its published checksum.");
                return;
            }
            var zip = Path.Combine(dir, "deno.zip");
            await VerifiedDownloader.DownloadAsync(DenoZipUrl, zip, hash, null, ct);
            ExtractEntry(zip, e => e.Equals("deno.exe", StringComparison.OrdinalIgnoreCase), DenoPath);
            TryDelete(zip);
            Log.Write("Installed deno JS runtime");
        }
        catch (Exception ex)
        {
            Log.Write("Optional JS runtime install skipped: " + ex.Message);
        }
    }

    private async Task InstallYtDlpAsync(Action<string, double> report, CancellationToken ct)
    {
        report("Downloading yt-dlp (YouTube engine)…", 0);
        var sums = await GetChecksumsAsync(YtDlpBase + "SHA2-256SUMS", ct);
        var hash = VerifiedDownloader.FindHash(sums, "yt-dlp.exe")
                   ?? throw new UserFacingException("Couldn't verify the yt-dlp download (no checksum was published).");
        var progress = new Progress<(long Done, long? Total)>(p =>
            report($"Downloading yt-dlp… {Mb(p.Done)} / {Mb(p.Total)}", Fraction(p)));
        await WithNetworkErrors(() => VerifiedDownloader.DownloadAsync(YtDlpBase + "yt-dlp.exe", YtDlpPath, hash, progress, ct));
        await EnsureRunsAsync(YtDlpPath, "--version", "yt-dlp", ct);
        MarkYtDlpChecked(); // a fresh download is the latest release, so no update check is needed today
        Log.Write("Installed yt-dlp");
    }

    private static string UpdateStampPath => Path.Combine(AppPaths.Home, "ytdlp-last-update-check.txt");

    private static void MarkYtDlpChecked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.WriteAllText(UpdateStampPath, DateTime.UtcNow.ToString("o"));
        }
        catch { }
    }

    private async Task InstallFfmpegAsync(Action<string, double> report, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var src in FfmpegSources)
        {
            try
            {
                report("Downloading ffmpeg (audio engine)…", 0);
                var sums = await GetChecksumsAsync(src.ChecksumUrl, ct);
                var hash = VerifiedDownloader.FindHash(sums, Path.GetFileName(new Uri(src.ZipUrl).AbsolutePath))
                           ?? throw new UserFacingException("Couldn't verify the ffmpeg download (no checksum was published).");

                var dir = Path.GetDirectoryName(FfmpegPath)!;
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, "ffmpeg-download.zip");
                var progress = new Progress<(long Done, long? Total)>(p =>
                    report($"Downloading ffmpeg… {Mb(p.Done)} / {Mb(p.Total)}", Fraction(p) * 0.97));
                try
                {
                    await WithNetworkErrors(() => VerifiedDownloader.DownloadAsync(src.ZipUrl, zip, hash, progress, ct));
                    report("Unpacking ffmpeg…", 0.98);
                    ExtractEntry(zip, e => e.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase), FfmpegPath);
                }
                finally { TryDelete(zip); }

                await EnsureRunsAsync(FfmpegPath, "-version", "ffmpeg", ct);
                Log.Write("Installed ffmpeg from " + src.ZipUrl);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Write($"ffmpeg source failed ({src.ZipUrl}): {ex.Message}");
            }
        }
        throw last is UserFacingException ? last : new UserFacingException("Couldn't download ffmpeg: " + last?.Message, last);
    }

    private static async Task<string> GetChecksumsAsync(string url, CancellationToken ct)
    {
        try { return await VerifiedDownloader.GetTextAsync(url, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw NetworkError(ex); }
    }

    private static async Task WithNetworkErrors(Func<Task> action)
    {
        try { await action(); }
        catch (HttpRequestException ex) { throw NetworkError(ex); }
        catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException) { throw NetworkError(ex); }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException) { throw NetworkError(ex); }
    }

    private static UserFacingException NetworkError(Exception ex) =>
        new("Couldn't reach the download server. Please check your internet connection and try again.", ex);

    private static async Task EnsureRunsAsync(string exe, string arg, string name, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(exe, new[] { arg }, ct: ct);
            if (r.ExitCode == 0) return;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        TryDelete(exe);
        throw new UserFacingException($"The downloaded {name} doesn't run on this computer (it may have been blocked by security software).");
    }

    private static void ExtractEntry(string zipPath, Func<string, bool> match, string destFile)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => match(e.FullName))
                    ?? throw new UserFacingException("The downloaded archive didn't contain the expected program.");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        var tmp = destFile + ".extract";
        entry.ExtractToFile(tmp, overwrite: true);
        File.Move(tmp, destFile, overwrite: true);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static string Mb(long? bytes) => bytes is null ? "?" : $"{bytes / 1_048_576.0:0.#} MB";
    private static double Fraction((long Done, long? Total) p) => p.Total is > 0 ? (double)p.Done / p.Total.Value : 0;

    // ----- keeping yt-dlp fresh ------------------------------------------------------------

    /// <summary>YouTube changes constantly; yt-dlp releases fixes within days. Update our private copy at most once a day.</summary>
    public async Task MaybeUpdateYtDlpAsync(CancellationToken ct)
    {
        if (YtDlpIsExternal || !HasYtDlp) return;
        var stamp = UpdateStampPath;
        try
        {
            if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromHours(24)) return;
            await UpdateYtDlpAsync(ct);
            MarkYtDlpChecked();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Write("yt-dlp auto-update failed: " + ex.Message); }
    }

    /// <summary>Runs yt-dlp's built-in updater on our private copy. Returns a one-line summary.</summary>
    public async Task<string> UpdateYtDlpAsync(CancellationToken ct)
    {
        if (YtDlpIsExternal) return "Using an externally supplied yt-dlp; not updating it.";
        if (!HasYtDlp) return "yt-dlp isn't installed yet.";
        var r = await ProcessRunner.RunAsync(YtDlpPath, new[] { "-U", "--color", "never" }, ct: ct);
        var lines = (r.StdOut + "\n" + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var summary = lines.LastOrDefault() ?? "Done.";
        Log.Write("yt-dlp -U: " + summary);
        if (r.ExitCode != 0) throw new UserFacingException("Couldn't update yt-dlp: " + summary);
        return summary;
    }

    public async Task<string> GetYtDlpVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(YtDlpPath, new[] { "--version" }, ct: ct);
            return r.ExitCode == 0 ? r.StdOut.Trim() : "unknown";
        }
        catch (OperationCanceledException) { throw; }
        catch { return "unknown"; }
    }

    public async Task<string> GetFfmpegVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(FfmpegPath, new[] { "-version" }, ct: ct);
            var first = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[2] : "unknown";
        }
        catch (OperationCanceledException) { throw; }
        catch { return "unknown"; }
    }
}
