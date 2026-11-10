# SoundSnip

A native Windows app (WPF, .NET 8): paste a YouTube link, download the best audio YouTube offers,
trim it on a waveform with live previews, and save it.

## Download

1. Open the **[latest release](https://github.com/matiasbontempo/SoundSnip/releases/latest)**.
2. Under **Assets**, click **SoundSnip.exe** to download it.
3. Double-click the downloaded file. That's it: there is nothing to install.

You need Windows 10 or 11 (64-bit) and an internet connection.

**Windows says "Windows protected your PC"?** That's normal for any new app that isn't signed with a paid
certificate. Click **More info**, then **Run anyway**.

**The first time you open it,** SoundSnip downloads the tools it needs (about 140 MB, one time only). This takes a
minute or two; after that it opens instantly.

## Using it

`SoundSnip.exe` is a single self-contained file. There is nothing to install: no .NET, no Python, no ffmpeg.

On first launch it downloads the open-source tools it drives (about 140 MB, once) into
`%LOCALAPPDATA%\SoundSnip\tools`. No admin rights are needed and nothing is installed system-wide.
Delete that folder to remove them.

1. Paste a link and press **Load** (or press **Paste** to paste and load in one go).
2. Drag the two handles on the waveform (or type times) to choose the clip.
   Drag on empty waveform to draw a new range. Mouse wheel zooms, Shift+wheel or right-drag pans.
3. Press **Space** to hear exactly what you'll get (fades included). **Hear start / Hear end** replay the
   first / last 2 seconds so you can check the cut points. **I** / **O** set start / end at the playhead
   while it plays. Loop with **L**.
4. Choose a format and **Export clip…**.

### Quality

* The download is YouTube's own best audio-only stream (usually Opus, ~130–160 kbps; sometimes AAC), untouched.
* **Original quality** cuts that stream without re-encoding: zero quality loss (cut accuracy is one audio
  packet, ~20 ms). This is the best you can get.
* MP3 320 / AAC 256 re-encode once. FLAC / WAV are lossless containers but cannot add quality that
  YouTube's lossy source doesn't have.
* Fades require a re-encode, so they are available for every format except **Original**.

## How it works

| Piece | Role |
|---|---|
| `yt-dlp` | looks the video up and downloads its best audio stream |
| `ffmpeg` | decodes a preview, and writes the final trimmed/converted file |
| NAudio | plays the preview with sample-accurate range, fades and looping |
| optional JS runtime (Deno) | lets yt-dlp keep working as YouTube changes; fetched in the background, best-effort |

Downloaded tools are verified against the SHA-256 checksum published next to them before they are kept.
yt-dlp is updated at most once a day (and automatically once if a lookup fails in a way that looks like
YouTube changed), because YouTube breaks downloaders regularly. There is also an **Update downloader** button.

Environment overrides (development): `SOUNDSNIP_HOME` (data folder), `SOUNDSNIP_YTDLP`, `SOUNDSNIP_FFMPEG`,
`SOUNDSNIP_JSRUNTIME`. Optional argument: `SoundSnip.exe "https://youtu.be/…"` loads that link at start.
Errors are logged to `%LOCALAPPDATA%\SoundSnip\soundsnip.log`.

## Building

Needs the .NET 8 SDK.

```
dotnet build SoundSnip.sln
dotnet publish src/SoundSnip/SoundSnip.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true -p:DebugType=none -o dist
```

## Tests

Two console test projects exercise the real pipeline against a short public video (they need network and an
installed yt-dlp/ffmpeg, pointed at via the override variables above):

* `src/SoundSnip.SmokeTest`: URL parsing, checksum-verifying downloader (against a local server), lookup,
  download, decode, waveform, every export format with duration and cut-accuracy checks, player.
* `src/SoundSnip.UiTest`: the real view-model and waveform control on a WPF dispatcher: load, reload, cancel,
  trim logic, previews, loop, export, handle dragging, zoom and pan. `--screens` also captures screenshots.

```
SoundSnip.SmokeTest.exe <youtube-url> <output-dir>
SoundSnip.UiTest.exe url=<youtube-url> <output-dir> [--screens]
```

## Please note

Only download audio you have the right to use. YouTube's Terms of Service restrict downloading content
without permission, and many videos are copyrighted.
