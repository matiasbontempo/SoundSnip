using NAudio.Wave;

namespace SoundSnip.Core;

internal static class WavFiles
{
    /// <summary>
    /// Opens a 16-bit PCM .wav for reading. Uses permissive sharing and a short retry because Windows
    /// antivirus scanners and indexers sometimes hold a freshly written file open for a moment, which
    /// makes a plain File.OpenRead (share-read only) fail with "in use by another process".
    /// </summary>
    public static WaveFileReader Open(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new WaveFileReader(fs); // takes ownership of the stream
            }
            catch (IOException) when (attempt < 8)
            {
                fs?.Dispose();
                Thread.Sleep(150);
            }
        }
    }
}
