using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SoundSnip.Core;

/// <summary>Where SoundSnip keeps its tools, scratch files and settings.</summary>
public static class AppPaths
{
    /// <summary>Per-user data root. Override with SOUNDSNIP_HOME (used by tests).</summary>
    public static string Home { get; } = ResolveHome();

    private static string ResolveHome()
    {
        var env = Environment.GetEnvironmentVariable("SOUNDSNIP_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundSnip");
    }

    public static string ToolsDir => Path.Combine(Home, "tools");
    public static string WorkDir => Path.Combine(Home, "work");
    public static string LogPath => Path.Combine(Home, "soundsnip.log");
    public static string SettingsPath => Path.Combine(Home, "settings.json");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Home);
                var path = AppPaths.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

/// <summary>An error whose message is safe and useful to show to the user as-is.</summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>yt-dlp failed. <see cref="LooksOutdated"/> means an update + retry is worth trying.</summary>
public sealed class YtDlpException : UserFacingException
{
    public bool LooksOutdated { get; }
    public YtDlpException(string message, bool looksOutdated) : base(message) => LooksOutdated = looksOutdated;
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    /// <summary>Runs a console program without a window. Arguments are passed as a list (no shell, no quoting bugs).</summary>
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // yt-dlp is a Python program: make it speak UTF-8 on pipes so titles survive.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        ct.ThrowIfCancellationRequested();
        p.Start();
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        p.WaitForExit(); // make sure the async readers have flushed
        ct.ThrowIfCancellationRequested();

        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        return new ProcessResult(p.ExitCode, o, e2);
    }
}

public static class TimeFormat
{
    /// <summary>1:23.456 or 1:02:03.456</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        long ms = (long)Math.Round(seconds * 1000);
        long h = ms / 3_600_000, m = ms / 60_000 % 60, s = ms / 1000 % 60, f = ms % 1000;
        return h > 0 ? $"{h}:{m:00}:{s:00}.{f:000}" : $"{m}:{s:00}.{f:000}";
    }

    /// <summary>1:23 or 1:02:03 (no fraction) for labels.</summary>
    public static string FormatShort(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        long t = (long)Math.Round(seconds);
        long h = t / 3600, m = t / 60 % 60, s = t % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>
    /// Safe for file names: 01m23s. Whole seconds, so the label always covers the real clip:
    /// start times round down, end times round up.
    /// </summary>
    public static string ForFileName(double seconds, bool roundUp = false)
    {
        long t = (long)(roundUp ? Math.Ceiling(seconds - 0.001) : Math.Floor(seconds + 0.001));
        if (t < 0) t = 0;
        long h = t / 3600, m = t / 60 % 60, s = t % 60;
        return h > 0 ? $"{h}h{m:00}m{s:00}s" : $"{m:00}m{s:00}s";
    }

    /// <summary>Accepts "90", "90.5", "1:30", "1:30.25", "1:02:03.5" (comma decimals too).</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || double.IsInfinity(v))
                return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}

public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public static string Sanitize(string? name, int maxLength = 120)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd('.', ' ');
        if (s.Length == 0) s = "audio";
        if (Reserved.Contains(s)) s = "_" + s;
        return s;
    }
}
