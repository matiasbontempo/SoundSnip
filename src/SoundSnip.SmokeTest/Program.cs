using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SoundSnip.Core;

// Usage: SoundSnip.SmokeTest <youtube-url> <outputDir>
// Run with SOUNDSNIP_HOME / SOUNDSNIP_YTDLP / SOUNDSNIP_FFMPEG set to isolated/dev locations.

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  — " + detail : "")}");
    if (!ok) failures++;
}

// ---------------------------------------------------------------- pure logic
Console.WriteLine("== Unit checks");
Check("TimeFormat 1:23.456", TimeFormat.Format(83.456) == "1:23.456", TimeFormat.Format(83.456));
Check("TimeFormat hours", TimeFormat.Format(3723.5) == "1:02:03.500", TimeFormat.Format(3723.5));
foreach (var (text, expected) in new[] { ("90", 90.0), ("1:30", 90.0), ("1:30.25", 90.25), ("1:02:03.5", 3723.5), ("0:05,5", 5.5) })
    Check($"TimeFormat.TryParse(\"{text}\")", TimeFormat.TryParse(text, out var v) && Math.Abs(v - expected) < 1e-9, v.ToString(CultureInfo.InvariantCulture));
Check("TryParse rejects junk", !TimeFormat.TryParse("abc", out _) && !TimeFormat.TryParse("1:2:3:4", out _) && !TimeFormat.TryParse("-5", out _));
Check("Sanitize strips illegal chars", FileNames.Sanitize("a/b:c*?\"<>|d. ") == "a_b_c______d", FileNames.Sanitize("a/b:c*?\"<>|d. "));
Check("Sanitize reserved name", FileNames.Sanitize("CON") == "_CON");
Check("URL: bare youtu.be accepted", YtDlpService.TryNormalizeUrl("youtu.be/jNQXAC9IVRw", out var u1) && u1.StartsWith("https://youtu.be/"));
Check("URL: junk rejected", !YtDlpService.TryNormalizeUrl("hello world", out _) && !YtDlpService.TryNormalizeUrl("ftp://x.com/a", out _));
Check("FindHash by name", VerifiedDownloader.FindHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  one.exe\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *two.exe", "two.exe")?.StartsWith("bbbb") == true);
Check("FindHash single-hash file", VerifiedDownloader.FindHash("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\n", "whatever.zip")?.StartsWith("cccc") == true);
Check("FindHash Get-FileHash layout (as Deno publishes it)", VerifiedDownloader.FindHash("\nAlgorithm : SHA256\nHash      : CBA84B26E0DF5AE7F74FC66B86A95B866872A86292EBD84FA1E959927EDD93BF\nPath      : C:\\a\\deno.zip\n", "deno.zip") == "CBA84B26E0DF5AE7F74FC66B86A95B866872A86292EBD84FA1E959927EDD93BF");
Check("FindHash ambiguous → null",VerifiedDownloader.FindHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  one\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  two", "three") is null);

// ---------------------------------------------------------------- downloader vs. a local server
Console.WriteLine("== Verified downloader (local HTTP server, no internet)");
{
    var payload = new byte[3_000_000];
    new Random(42).NextBytes(payload);
    var goodHash = Convert.ToHexString(SHA256.HashData(payload));
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:18765/");
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.Close();
            }
            catch { }
        }
    });

    var dir = Path.Combine(AppPaths.Home, "dltest");
    Directory.CreateDirectory(dir);
    var dest = Path.Combine(dir, "ok.bin");
    long lastDone = 0; bool sawProgress = false;
    await VerifiedDownloader.DownloadAsync("http://127.0.0.1:18765/f", dest, goodHash,
        new Progress<(long Done, long? Total)>(p => { sawProgress = true; lastDone = p.Done; }), CancellationToken.None);
    await Task.Delay(100);
    Check("good hash: file kept", File.Exists(dest) && new FileInfo(dest).Length == payload.Length);
    Check("progress reported", sawProgress && lastDone > 0);

    var bad = Path.Combine(dir, "bad.bin");
    bool rejected = false;
    try { await VerifiedDownloader.DownloadAsync("http://127.0.0.1:18765/f", bad, new string('0', 64), null, CancellationToken.None); }
    catch (UserFacingException) { rejected = true; }
    Check("wrong hash: rejected and nothing left behind", rejected && !File.Exists(bad) && !File.Exists(bad + ".download"));
    listener.Stop();
}

// ---------------------------------------------------------------- real pipeline
if (args.Length < 2)
{
    Console.WriteLine("(skipping pipeline test: pass <url> <outputDir>)");
    return failures == 0 ? 0 : 1;
}

var url = args[0];
var outDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outDir);

var tools = new ToolManager();
var yt = new YtDlpService(tools);
var ff = new FfmpegService(tools);

if (!tools.RequiredToolsReady)
{
    // Real first-run provisioning: official downloads + checksum verification, into SOUNDSNIP_HOME.
    Console.WriteLine("== First-run setup (real downloads)");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int lastPct = -10;
    await tools.EnsureRequiredAsync(new Progress<SetupProgress>(p =>
    {
        int pct = (int)(p.Fraction * 100);
        if (pct >= lastPct + 10) { lastPct = pct; Console.WriteLine($"  {pct,3}%  {p.Message}"); }
    }), CancellationToken.None);
    Console.WriteLine($"  required tools ready in {sw.Elapsed.TotalSeconds:F0}s");
    Check("yt-dlp downloaded, verified and runs", tools.HasYtDlp && (await tools.GetYtDlpVersionAsync()) != "unknown", await tools.GetYtDlpVersionAsync());
    Check("ffmpeg downloaded, extracted and runs", tools.HasFfmpeg && (await tools.GetFfmpegVersionAsync()) != "unknown", await tools.GetFfmpegVersionAsync());
    var encoders = await ProcessRunner.RunAsync(tools.FfmpegPath, new[] { "-hide_banner", "-encoders" });
    Check("this ffmpeg build can encode MP3, AAC, FLAC", encoders.StdOut.Contains("libmp3lame") && encoders.StdOut.Contains(" aac ") && encoders.StdOut.Contains(" flac "));
    Console.WriteLine("  leftover temp files: " + string.Join(", ", Directory.EnumerateFiles(AppPaths.ToolsDir, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".zip") || f.EndsWith(".download") || f.EndsWith(".extract")).DefaultIfEmpty("none")));
    Check("no leftover zip/temp files", !Directory.EnumerateFiles(AppPaths.ToolsDir, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(".zip") || f.EndsWith(".download") || f.EndsWith(".extract")));

    var again = System.Diagnostics.Stopwatch.StartNew();
    await tools.EnsureRequiredAsync(new Progress<SetupProgress>(_ => { }), CancellationToken.None);
    Check("running setup again is an instant no-op", again.ElapsedMilliseconds < 1500, $"{again.ElapsedMilliseconds} ms");

}

if (!File.Exists(tools.DenoPath) && Environment.GetEnvironmentVariable("SOUNDSNIP_JSRUNTIME") is null)
{
    Console.WriteLine("== Optional JS runtime (real download)");
    var swd = System.Diagnostics.Stopwatch.StartNew();
    await tools.TryInstallJsRuntimeAsync(CancellationToken.None);
    Check("Deno downloaded, checksum-verified and extracted", File.Exists(tools.DenoPath), $"{swd.Elapsed.TotalSeconds:F0}s");
    if (File.Exists(tools.DenoPath))
    {
        var dv = await ProcessRunner.RunAsync(tools.DenoPath, new[] { "--version" });
        Check("Deno runs", dv.ExitCode == 0, dv.StdOut.Split('\n')[0].Trim());
        Check("yt-dlp is told to use it", tools.JsRuntimeArgument == $"deno:{tools.DenoPath}", tools.JsRuntimeArgument ?? "null");
    }
}

Console.WriteLine("== Pipeline");
Console.WriteLine($"  yt-dlp : {tools.YtDlpPath}  ({await tools.GetYtDlpVersionAsync()})");
Console.WriteLine($"  ffmpeg : {tools.FfmpegPath}  ({await tools.GetFfmpegVersionAsync()})");
Console.WriteLine($"  js rt  : {tools.JsRuntimeArgument ?? "(none)"}");

Check("tools present", tools.RequiredToolsReady);
Check("URL normalises", YtDlpService.TryNormalizeUrl(url, out var normUrl));

var info = await yt.GetInfoAsync(normUrl, CancellationToken.None);
Console.WriteLine($"  title  : {info.Title}\n  by     : {info.Uploader}\n  length : {info.DurationSeconds}s\n  audio  : {info.QualityDescription} (.{info.SourceExtension} → original .{info.OriginalExtension})\n  thumb  : {info.ThumbnailUrl}");
Check("info has id/title/duration", info.Id.Length > 0 && info.Title.Length > 0 && info.DurationSeconds > 0);
Check("thumbnail is jpg/png (WPF-safe)", info.ThumbnailUrl is not null && Regex.IsMatch(info.ThumbnailUrl.Split('?')[0], @"\.(jpe?g|png)$", RegexOptions.IgnoreCase), info.ThumbnailUrl ?? "null");

var work = Path.Combine(AppPaths.WorkDir, info.Id);
int progressEvents = 0; double lastFraction = 0;
var src = await yt.DownloadAudioAsync(normUrl, work, new Progress<DownloadProgress>(p => { progressEvents++; lastFraction = p.Fraction; }), CancellationToken.None);
await Task.Delay(200);
Console.WriteLine($"  source : {src} ({new FileInfo(src).Length:N0} bytes), {progressEvents} progress events, last={lastFraction:P0}");
Check("downloaded source exists", File.Exists(src) && new FileInfo(src).Length > 10_000);
Check("download progress was reported", progressEvents > 0);

var wav = Path.Combine(work, "preview.wav");
await ff.DecodePreviewAsync(src, wav, info.DurationSeconds, null, CancellationToken.None);
Check("preview wav created", File.Exists(wav));

var wf = WaveformData.Build(wav, CancellationToken.None);
Console.WriteLine($"  waveform: {wf.BucketCount:N0} buckets, {wf.DurationSeconds:F3}s @ {wf.SampleRate} Hz, gain x{wf.DisplayGain:F2}");
Check("waveform duration ≈ yt-dlp duration (±1s)", Math.Abs(wf.DurationSeconds - info.DurationSeconds) < 1.0);
Check("waveform has dynamic range", wf.Max.Max() > 0.01f && wf.Min.Min() < -0.01f);

// ---- exports: trim 4.0 → 12.5 (8.5 s) in every format
double s0 = Math.Min(4.0, info.DurationSeconds * 0.2), e0 = Math.Min(12.5, info.DurationSeconds - 1);
double want = e0 - s0;
Console.WriteLine($"== Exports of {s0:F3}s → {e0:F3}s (expect {want:F3}s)");
foreach (var fmt in Enum.GetValues<ExportFormat>())
{
    var ext = FfmpegService.ExtensionFor(fmt, info);
    var dest = Path.Combine(outDir, $"clip-{fmt}.{ext}");
    var o = new ExportOptions(fmt, s0, e0, false, fmt == ExportFormat.Original ? 0 : 0.5, fmt == ExportFormat.Original ? 0 : 0.5, info.Title, info.Uploader);
    double lastP = 0;
    await ff.ExportAsync(src, dest, o, new Progress<double>(p => lastP = p), CancellationToken.None);
    var dur = await MeasureAsync(tools, dest);
    var tol = fmt == ExportFormat.Original ? 0.08 : 0.06;
    Check($"{fmt,-8} .{ext,-5} {new FileInfo(dest).Length,9:N0} B  duration {dur:F3}s", Math.Abs(dur - want) <= tol, $"want {want:F3}±{tol}");
}

// full-range original export
{
    var dest = Path.Combine(outDir, "full-original." + info.OriginalExtension);
    await ff.ExportAsync(src, dest, new ExportOptions(ExportFormat.Original, 0, info.DurationSeconds, true, 0, 0, info.Title, info.Uploader), null, CancellationToken.None);
    var dur = await MeasureAsync(tools, dest);
    Check($"Original full-range duration {dur:F3}s", Math.Abs(dur - wf.DurationSeconds) < 0.1, $"source {wf.DurationSeconds:F3}s");
}

// fade really changes the audio: first 0.25 s of the faded MP3 must be quieter than the un-faded Original
{
    var faded = await RmsAsync(tools, Path.Combine(outDir, "clip-Flac.flac"), 0, 0.25);
    var plain = await RmsAsync(tools, Path.Combine(outDir, "clip-Original." + info.OriginalExtension), 0, 0.25);
    Check("fade-in is audible in the data (faded start quieter than plain start)", faded < plain * 0.7 || plain < 0.003, $"faded rms {faded:F4} vs plain rms {plain:F4}");
}

// ---- how accurate is the lossless cut? compare the copied clip to the same range of the preview audio
{
    double offsetMs = await CutOffsetMsAsync(tools, wav, Path.Combine(outDir, "clip-Original." + info.OriginalExtension), s0, 8000);
    Check($"lossless cut lands within 50 ms of the requested start (measured {offsetMs:F1} ms)", Math.Abs(offsetMs) <= 50);
}

// ---- player
Console.WriteLine("== Player");
try
{
    using var player = new ClipPlayer(wav);
    var ended = new TaskCompletionSource<bool>();
    player.Ended += n => ended.TrySetResult(n);
    player.Play(s0, s0 + 0.6, 0.1, 0.1);
    await Task.Delay(300);
    var pos = player.Position;
    Check("player position advances", pos > s0 + 0.05 && pos < s0 + 0.6, $"pos {pos:F3}");
    var done = await Task.WhenAny(ended.Task, Task.Delay(3000));
    Check("player reports natural end", done == ended.Task && ended.Task.Result);
}
catch (Exception ex)
{
    Console.WriteLine($"  [SKIP] no usable audio output device here: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// ================================================================ helpers
static async Task<double> MeasureAsync(ToolManager tools, string file)
{
    // decode the whole thing to null: the final "time=" is the true playable length
    var r = await ProcessRunner.RunAsync(tools.FfmpegPath, new[] { "-hide_banner", "-nostdin", "-i", file, "-f", "null", "-" });
    var m = Regex.Matches(r.StdErr, @"time=(\d+):(\d+):(\d+\.\d+)");
    if (m.Count == 0) return -1;
    var l = m[^1];
    return int.Parse(l.Groups[1].Value) * 3600 + int.Parse(l.Groups[2].Value) * 60 + double.Parse(l.Groups[3].Value, CultureInfo.InvariantCulture);
}

static async Task<double> RmsAsync(ToolManager tools, string file, double from, double len)
{
    var samples = await DecodeMono8kAsync(tools, file, from, len);
    return samples.Length == 0 ? 0 : Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);
}

static async Task<float[]> DecodeMono8kAsync(ToolManager tools, string file, double from, double len)
{
    var tmp = Path.Combine(Path.GetTempPath(), $"snip-{Guid.NewGuid():N}.raw");
    try
    {
        var a = new List<string> { "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-i", file };
        a.AddRange(new[] { "-ss", from.ToString("0.000", CultureInfo.InvariantCulture) });
        if (len > 0) a.AddRange(new[] { "-t", len.ToString("0.000", CultureInfo.InvariantCulture) });
        a.AddRange(new[] { "-ac", "1", "-ar", "8000", "-f", "f32le", tmp });
        await ProcessRunner.RunAsync(tools.FfmpegPath, a);
        var bytes = File.ReadAllBytes(tmp);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
        return f;
    }
    finally { try { File.Delete(tmp); } catch { } }
}

// Cross-correlates the copied clip against the reference audio around the requested start; returns the lag in ms.
static async Task<double> CutOffsetMsAsync(ToolManager tools, string referenceWav, string clip, double requestedStart, int clipSamples)
{
    var clipPcm = await DecodeMono8kAsync(tools, clip, 0, 0);
    // reference: from 0.5 s before the requested start, 2 s long window
    double refFrom = Math.Max(0, requestedStart - 0.5);
    var refPcm = await DecodeMono8kAsync(tools, referenceWav, refFrom, 3.0);
    int n = Math.Min(clipPcm.Length, 6000);
    double best = double.MinValue; int bestLag = 0;
    for (int lag = 0; lag + n < refPcm.Length; lag++)
    {
        double dot = 0;
        for (int i = 0; i < n; i++) dot += clipPcm[i] * refPcm[lag + i];
        if (dot > best) { best = dot; bestLag = lag; }
    }
    double clipStartInRef = refFrom + bestLag / 8000.0;
    return (clipStartInRef - requestedStart) * 1000.0;
}
