using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SoundSnip.Core;

/// <summary>Downloads a file over HTTPS, hashing it as it streams, and refuses to keep it if the SHA-256 doesn't match.</summary>
public static class VerifiedDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SoundSnip/1.0");
        return c;
    }

    public static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <param name="expectedSha256">Hex digest. Pass null only for files that have no published checksum.</param>
    public static async Task DownloadAsync(
        string url, string destPath, string? expectedSha256,
        IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        var tmp = destPath + ".download";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                long done = 0;
                var lastReport = DateTime.MinValue;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buf, 0, n);
                    done += n;
                    if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report((done, total));
                    }
                }
                progress?.Report((done, total ?? done));
                if (total is not null && done != total)
                    throw new UserFacingException("The download was cut off. Please check your connection and try again.");
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (expectedSha256 is not null && !actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException(
                    "A downloaded file failed its integrity check (SHA-256 mismatch), so it was discarded. Please try again.");

            File.Move(tmp, destPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static readonly Regex Sha256 = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Reads a checksum file. Understands "hash  name" / "hash *name" lines (sha256sum style)
    /// and files that contain only a single hash.
    /// </summary>
    public static string? FindHash(string checksumText, string fileName)
    {
        string? onlyHash = null;
        int hashLines = 0;
        foreach (var raw in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var tokens = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !Sha256.IsMatch(tokens[0])) continue;
            hashLines++;
            onlyHash ??= tokens[0];
            if (tokens.Length > 1 && string.Equals(Path.GetFileName(tokens[^1].Replace('/', '\\')), fileName, StringComparison.OrdinalIgnoreCase))
                return tokens[0];
        }
        if (hashLines == 1) return onlyHash;

        // Other layouts (e.g. PowerShell's Get-FileHash dump: "Hash : ABC…" on its own line): accept a single, unambiguous digest.
        if (hashLines == 0)
        {
            var found = Regex.Matches(checksumText, @"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])")
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (found.Count == 1) return found[0];
        }
        return null;
    }
}
