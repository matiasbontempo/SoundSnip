using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundSnip.Core;

namespace SoundSnip;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const double MinSel = 0.05;
    private const double PreviewSeconds = 2.0;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ToolManager _tools = new();
    private readonly YtDlpService _yt;
    private readonly FfmpegService _ff;
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _timer;

    private ClipPlayer? _player;
    private string? _sourcePath, _workDir, _pendingDir;
    private CancellationTokenSource? _loadCts, _exportCts, _setupCts;
    private Task _maintenance = Task.CompletedTask;
    private bool _triedUpdate, _playIsSelection;

    /// <summary>(suggested file name without extension, extension, initial folder) → chosen full path or null.</summary>
    public Func<string, string, string?, string?>? PickSavePath { get; set; }

    public MainViewModel()
    {
        _yt = new YtDlpService(_tools);
        _ff = new FfmpegService(_tools);

        Formats = new[]
        {
            new FormatOption(ExportFormat.Original, "Original quality — no re-encode (recommended)"),
            new FormatOption(ExportFormat.Mp3, "MP3 — 320 kbps"),
            new FormatOption(ExportFormat.M4a, "M4A (AAC) — 256 kbps"),
            new FormatOption(ExportFormat.Flac, "FLAC — lossless"),
            new FormatOption(ExportFormat.Wav, "WAV — uncompressed"),
        };
        _selectedFormat = Formats.FirstOrDefault(f => f.Format.ToString() == _settings.Format) ?? Formats[0];
        _volume = Math.Clamp(_settings.Volume, 0, 1);
        _loop = _settings.Loop;
        _fadeIn = Math.Max(0, _settings.FadeIn);
        _fadeOut = Math.Max(0, _settings.FadeOut);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            if (_player is { IsPlaying: true }) Playhead = _player.Position;
            else _timer.Stop();
        };

        PasteCommand = new AsyncRelayCommand(PasteAsync, () => IsToolsReady && !IsBusy && !IsExporting);
        PrimaryActionCommand = new AsyncRelayCommand(PrimaryActionAsync, () => IsToolsReady && !IsExporting);
        PlayPauseCommand = new RelayCommand(PlayPause, () => IsEditorReady);
        PreviewStartCommand = new RelayCommand(PreviewStart, () => IsEditorReady);
        PreviewEndCommand = new RelayCommand(PreviewEnd, () => IsEditorReady);
        SetStartHereCommand = new RelayCommand(SetStartHere, () => IsEditorReady);
        SetEndHereCommand = new RelayCommand(SetEndHere, () => IsEditorReady);
        SelectAllCommand = new RelayCommand(() => SetSelection(0, Duration), () => IsEditorReady);
        NudgeStartCommand = new RelayCommand(p => Nudge(true, p), _ => IsEditorReady);
        NudgeEndCommand = new RelayCommand(p => Nudge(false, p), _ => IsEditorReady);
        ExportCommand = new AsyncRelayCommand(ExportOrCancelAsync, () => IsEditorReady && !IsBusy);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => LastExportPath is not null);
        UpdateToolsCommand = new AsyncRelayCommand(UpdateToolsAsync, () => IsToolsReady && !IsBusy && !IsUpdatingTools && !IsExporting);
        RetrySetupCommand = new AsyncRelayCommand(RunSetupAsync);
    }

    // ================================================================== commands
    public ICommand PasteCommand { get; }
    public ICommand PrimaryActionCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand PreviewStartCommand { get; }
    public ICommand PreviewEndCommand { get; }
    public ICommand SetStartHereCommand { get; }
    public ICommand SetEndHereCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand NudgeStartCommand { get; }
    public ICommand NudgeEndCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand UpdateToolsCommand { get; }
    public ICommand RetrySetupCommand { get; }

    private static void Requery() => CommandManager.InvalidateRequerySuggested();

    // ================================================================== first-run setup
    private bool _isToolsReady, _isSettingUp;
    private string _setupText = "Preparing…";
    private double _setupFraction;
    private string? _setupError;

    public bool IsToolsReady { get => _isToolsReady; private set { if (Set(ref _isToolsReady, value)) Requery(); } }
    public bool IsSettingUp { get => _isSettingUp; private set => Set(ref _isSettingUp, value); }
    public string SetupText { get => _setupText; private set => Set(ref _setupText, value); }
    public double SetupFraction { get => _setupFraction; private set => Set(ref _setupFraction, value); }
    public string? SetupError { get => _setupError; private set { if (Set(ref _setupError, value)) OnPropertyChanged(nameof(HasSetupError)); } }
    public bool HasSetupError => SetupError is not null;

    public async Task InitializeAsync(string? startUrl)
    {
        CleanOldWorkFolders();
        if (_tools.RequiredToolsReady) IsToolsReady = true;
        else await RunSetupAsync();

        if (IsToolsReady)
        {
            _maintenance = BackgroundMaintenanceAsync();
            _ = _tools.TryInstallJsRuntimeAsync(CancellationToken.None); // optional extra; must never delay the first load
            await RefreshToolsTextAsync();
            if (startUrl is not null)
            {
                Url = startUrl;
                await LoadAsync();
            }
        }
    }

    private async Task RunSetupAsync()
    {
        _setupCts?.Cancel();
        _setupCts = new CancellationTokenSource();
        var ct = _setupCts.Token;
        IsSettingUp = true;
        SetupError = null;
        SetupFraction = 0;
        SetupText = "Starting…";
        try
        {
            await _tools.EnsureRequiredAsync(new Progress<SetupProgress>(p => { SetupText = p.Message; SetupFraction = p.Fraction; }), ct);
            IsToolsReady = true;
            IsSettingUp = false;
        }
        catch (OperationCanceledException) { }
        catch (UserFacingException ex) { SetupError = ex.Message; }
        catch (Exception ex)
        {
            Log.Write("Setup failed: " + ex);
            SetupError = "Setup failed: " + ex.Message;
        }
    }

    private async Task BackgroundMaintenanceAsync()
    {
        try
        {
            await _tools.MaybeUpdateYtDlpAsync(CancellationToken.None);
            await RefreshToolsTextAsync();
        }
        catch (Exception ex) { Log.Write("Maintenance failed: " + ex.Message); }
    }

    private async Task RefreshToolsTextAsync()
    {
        var y = await _tools.GetYtDlpVersionAsync();
        var f = await _tools.GetFfmpegVersionAsync();
        ToolsText = $"yt-dlp {y}  ·  ffmpeg {f}";
    }

    private static void CleanOldWorkFolders()
    {
        try
        {
            if (!Directory.Exists(AppPaths.WorkDir)) return;
            foreach (var d in Directory.EnumerateDirectories(AppPaths.WorkDir))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(12))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    // ================================================================== status line
    private string _statusText = "Paste a YouTube link above to get started.";
    private bool _statusIsError, _progressIndeterminate, _progressVisible;
    private double _progress;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Set(ref _progressVisible, value); }

    /// <param name="progress">null = no bar, -1 = indeterminate, 0..1 = determinate</param>
    private void SetStatus(string text, bool error = false, double? progress = null)
    {
        StatusText = text;
        StatusIsError = error;
        ProgressVisible = progress is not null;
        ProgressIndeterminate = progress is < 0;
        Progress = progress is >= 0 ? progress.Value : 0;
    }

    // ================================================================== loading a video
    private string _url = "";
    private bool _isBusy;
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(PrimaryText)); OnPropertyChanged(nameof(PrimaryGlyph)); OnPropertyChanged(nameof(ShowWelcome)); Requery(); } } }
    public string PrimaryText => IsBusy ? "Cancel" : "Load";
    public string PrimaryGlyph => IsBusy ? "" : "";

    private async Task PasteAsync()
    {
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { /* clipboard can be locked by another app */ }
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("The clipboard doesn't contain a link. Copy a YouTube video link first.", error: true);
            return;
        }
        Url = text.Trim();
        await LoadAsync();
    }

    private async Task PrimaryActionAsync()
    {
        if (IsBusy) { _loadCts?.Cancel(); return; }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        if (!YtDlpService.TryNormalizeUrl(Url, out var url))
        {
            SetStatus("That doesn't look like a link. Paste the address of a YouTube video.", error: true);
            return;
        }

        ResetVideoState();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        IsBusy = true;
        try
        {
            SetStatus("Getting ready…", progress: -1);
            await _maintenance.WaitAsync(ct); // don't race a background yt-dlp update

            SetStatus("Looking up the video…", progress: -1);
            var info = await GetInfoWithRecoveryAsync(url, ct);
            Info = info;
            _ = LoadThumbnailAsync(info, ct);

            var dir = _pendingDir = Path.Combine(AppPaths.WorkDir, FileNames.Sanitize(info.Id));
            SetStatus("Downloading the best audio…", progress: 0);
            var dl = new Progress<DownloadProgress>(p =>
            {
                var speed = p.BytesPerSecond is > 0 ? $"  ·  {p.BytesPerSecond / 1_048_576:0.0} MB/s" : "";
                SetStatus($"Downloading the best audio… {p.Fraction:P0}{speed}", progress: p.Fraction);
            });
            var src = await _yt.DownloadAudioAsync(url, dir, dl, ct);

            SetStatus("Preparing the waveform and instant preview…", progress: 0);
            var wav = Path.Combine(dir, "preview.wav");
            await _ff.DecodePreviewAsync(src, wav, info.DurationSeconds,
                new Progress<double>(f => SetStatus("Preparing the waveform and instant preview…", progress: f)), ct);

            SetStatus("Drawing the waveform…", progress: -1);
            var wf = await Task.Run(() => WaveformData.Build(wav, ct), ct);
            ct.ThrowIfCancellationRequested();

            _player = new ClipPlayer(wav) { Volume = (float)Volume };
            _player.Ended += OnPlayerEnded;
            _sourcePath = src;
            _workDir = dir;
            _pendingDir = null;

            Waveform = wf;
            Duration = wf.DurationSeconds;
            SelStart = 0;
            SelEnd = Duration;
            Playhead = 0;
            IsEditorReady = true;
            SetStatus("Ready. Drag the handles to choose your clip, press Space to preview it, then Export.");
        }
        catch (OperationCanceledException)
        {
            ResetVideoState();
            SetStatus("Cancelled.");
        }
        catch (UserFacingException ex)
        {
            ResetVideoState(keepInfo: false);
            SetStatus(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            Log.Write("Load failed: " + ex);
            ResetVideoState();
            SetStatus("Something went wrong: " + ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            _loadCts = null;
            cts.Dispose();
        }
    }

    private async Task<VideoInfo> GetInfoWithRecoveryAsync(string url, CancellationToken ct)
    {
        try { return await _yt.GetInfoAsync(url, ct); }
        catch (YtDlpException ex) when (ex.LooksOutdated && !_triedUpdate)
        {
            // YouTube changes often; a fresh yt-dlp usually fixes "it suddenly stopped working".
            _triedUpdate = true;
            SetStatus("YouTube may have changed — updating the downloader and retrying…", progress: -1);
            try { await _tools.UpdateYtDlpAsync(ct); await RefreshToolsTextAsync(); }
            catch (UserFacingException) { /* fall through and retry anyway */ }
            return await _yt.GetInfoAsync(url, ct);
        }
    }

    private async Task LoadThumbnailAsync(VideoInfo info, CancellationToken ct)
    {
        foreach (var url in new[] { info.ThumbnailUrl, $"https://i.ytimg.com/vi/{info.Id}/hqdefault.jpg" })
        {
            if (url is null) continue;
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 352;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                Thumbnail = img;
                return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* try the next candidate */ }
        }
    }

    private void ResetVideoState(bool keepInfo = false)
    {
        StopPlayback();
        if (_player is not null) { _player.Ended -= OnPlayerEnded; _player.Dispose(); _player = null; }
        IsEditorReady = false;
        Waveform = null;
        if (!keepInfo) { Info = null; Thumbnail = null; }
        Duration = 0; SelStart = 0; SelEnd = 0; Playhead = 0;
        ExportMessage = null; LastExportPath = null; ExportIsError = false;
        _sourcePath = null;
        TryDeleteDir(_workDir); _workDir = null;
        TryDeleteDir(_pendingDir); _pendingDir = null;
    }

    private static void TryDeleteDir(string? dir)
    {
        try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // ================================================================== video card
    private VideoInfo? _info;
    private ImageSource? _thumbnail;
    public VideoInfo? Info
    {
        get => _info;
        private set
        {
            if (!Set(ref _info, value)) return;
            foreach (var n in new[] { nameof(HasVideo), nameof(ShowWelcome), nameof(TitleText), nameof(SubtitleText), nameof(QualityText), nameof(FormatHint) })
                OnPropertyChanged(n);
        }
    }
    public ImageSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }
    public bool HasVideo => Info is not null;
    public bool ShowWelcome => Info is null && !IsBusy;
    public string TitleText => Info?.Title ?? "";
    public string SubtitleText => Info is null ? "" : $"{(string.IsNullOrWhiteSpace(Info.Uploader) ? "" : Info.Uploader + "  ·  ")}{TimeFormat.FormatShort(Info.DurationSeconds)}";
    public string QualityText => Info is null ? "" : $"Best audio available: {Info.QualityDescription}";

    // ================================================================== trimming
    private bool _isEditorReady;
    private WaveformData? _waveform;
    private double _duration, _selStart, _selEnd, _playhead;

    public bool IsEditorReady { get => _isEditorReady; private set { if (Set(ref _isEditorReady, value)) Requery(); } }
    public WaveformData? Waveform { get => _waveform; private set => Set(ref _waveform, value); }
    public double Duration { get => _duration; private set { if (Set(ref _duration, value)) OnPropertyChanged(nameof(PlayheadText)); } }

    public double SelStart
    {
        get => _selStart;
        private set { if (Set(ref _selStart, value)) SelectionChanged(); }
    }
    public double SelEnd
    {
        get => _selEnd;
        private set { if (Set(ref _selEnd, value)) SelectionChanged(); }
    }
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(IsFullRange));
    }

    public double Playhead
    {
        get => _playhead;
        private set { if (Set(ref _playhead, value)) OnPropertyChanged(nameof(PlayheadText)); }
    }

    public bool IsFullRange => SelStart <= 0.01 && SelEnd >= Duration - 0.01;
    public string LengthText => TimeFormat.Format(Math.Max(0, SelEnd - SelStart));
    public string PlayheadText => $"{TimeFormat.Format(Playhead)}  /  {TimeFormat.Format(Duration)}";

    public string StartText
    {
        get => TimeFormat.Format(SelStart);
        set
        {
            if (TimeFormat.TryParse(value, out var t)) SetSelection(Math.Min(t, SelEnd - MinSel), SelEnd);
            OnPropertyChanged(); // always refresh so rejected input snaps back to the real value
        }
    }
    public string EndText
    {
        get => TimeFormat.Format(SelEnd);
        set
        {
            if (TimeFormat.TryParse(value, out var t)) SetSelection(SelStart, Math.Max(t, SelStart + MinSel));
            OnPropertyChanged();
        }
    }

    public void SetSelection(double start, double end)
    {
        if (Duration <= 0) return;
        start = Math.Clamp(start, 0, Duration);
        end = Math.Clamp(end, 0, Duration);
        if (end - start < MinSel)
        {
            if (start + MinSel <= Duration) end = start + MinSel;
            else start = Math.Max(0, end - MinSel);
        }
        SelStart = start;
        SelEnd = end;
    }

    public void Seek(double t)
    {
        if (!IsEditorReady) return;
        t = Math.Clamp(t, 0, Duration);
        bool wasPlaying = IsPlaying;
        if (wasPlaying) StopPlayback();
        Playhead = t;
        if (wasPlaying && t >= SelStart && t < SelEnd - MinSel)
            StartPlayback(t, SelEnd, 0, EffectiveFadeOut, isSelection: true);
    }

    private void SetStartHere()
    {
        if (Playhead <= SelEnd - MinSel) SelStart = Playhead;
        else SetSelection(Playhead, Duration);
    }

    private void SetEndHere()
    {
        if (Playhead >= SelStart + MinSel) SelEnd = Playhead;
        else SetSelection(0, Playhead);
    }

    /// <summary>parameter is "-1" or "1"; Shift = 1 s, Ctrl = 10 ms, otherwise 100 ms.</summary>
    private void Nudge(bool start, object? parameter)
    {
        double dir = parameter is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 1;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.0 : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.01 : 0.1;
        if (start) SetSelection(Math.Clamp(SelStart + dir * step, 0, SelEnd - MinSel), SelEnd);
        else SetSelection(SelStart, Math.Clamp(SelEnd + dir * step, SelStart + MinSel, Duration));
    }

    // ================================================================== fades
    private double _fadeIn, _fadeOut;
    public double FadeIn
    {
        get => _fadeIn;
        set { var v = double.IsNaN(value) ? 0 : Math.Max(0, value); Set(ref _fadeIn, v); }
    }
    public double FadeOut
    {
        get => _fadeOut;
        set { var v = double.IsNaN(value) ? 0 : Math.Max(0, value); Set(ref _fadeOut, v); }
    }
    private double EffectiveFadeIn => FadeEnabled ? Math.Min(FadeIn, (SelEnd - SelStart) / 2) : 0;
    private double EffectiveFadeOut => FadeEnabled ? Math.Min(FadeOut, (SelEnd - SelStart) / 2) : 0;

    // ================================================================== playback / previews
    private bool _isPlaying, _loop;
    private double _volume;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) { OnPropertyChanged(nameof(PlayGlyph)); OnPropertyChanged(nameof(PlayLabel)); } }
    }
    public string PlayGlyph => IsPlaying ? "" : "";
    public string PlayLabel => IsPlaying ? "Stop" : "Play selection";

    public bool Loop { get => _loop; set { if (Set(ref _loop, value)) _settings.Loop = value; } }
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 1))) return;
            _settings.Volume = _volume;
            if (_player is not null) _player.Volume = (float)_volume;
        }
    }

    public void PlayPause()
    {
        if (!IsEditorReady) return;
        if (IsPlaying) { StopPlayback(); return; }
        double from = Playhead >= SelStart && Playhead < SelEnd - MinSel ? Playhead : SelStart;
        bool atStart = Math.Abs(from - SelStart) < 0.001;
        StartPlayback(from, SelEnd, atStart ? EffectiveFadeIn : 0, EffectiveFadeOut, isSelection: true);
    }

    public void StopPlayback()
    {
        if (_player is { IsPlaying: true }) Playhead = _player.Position;
        _player?.Stop();
        IsPlaying = false;
        _timer.Stop();
    }

    private void PreviewStart() =>
        StartPlayback(SelStart, Math.Min(SelEnd, SelStart + PreviewSeconds), EffectiveFadeIn, 0, isSelection: false);

    private void PreviewEnd() =>
        StartPlayback(Math.Max(SelStart, SelEnd - PreviewSeconds), SelEnd, 0, EffectiveFadeOut, isSelection: false);

    private void StartPlayback(double from, double to, double fadeIn, double fadeOut, bool isSelection)
    {
        if (_player is null) return;
        _playIsSelection = isSelection;
        _player.Volume = (float)Volume;
        try
        {
            Playhead = from;
            _player.Play(from, to, fadeIn, fadeOut);
            IsPlaying = _player.IsPlaying;
            if (IsPlaying) _timer.Start();
        }
        catch (Exception ex)
        {
            Log.Write("Playback failed: " + ex);
            IsPlaying = false;
            SetStatus("Couldn't play audio (" + ex.Message + "). Check that a sound output device is connected.", error: true);
        }
    }

    private void OnPlayerEnded(bool natural)
    {
        IsPlaying = false;
        _timer.Stop();
        if (!natural) return;
        if (_playIsSelection && Loop && IsEditorReady)
        {
            StartPlayback(SelStart, SelEnd, EffectiveFadeIn, EffectiveFadeOut, isSelection: true);
            return;
        }
        Playhead = _playIsSelection ? SelStart : Playhead;
    }

    // ================================================================== export
    public FormatOption[] Formats { get; }
    private FormatOption _selectedFormat;
    public FormatOption SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (value is null || !Set(ref _selectedFormat, value)) return;
            OnPropertyChanged(nameof(FadeEnabled));
            OnPropertyChanged(nameof(FormatHint));
        }
    }
    public bool FadeEnabled => SelectedFormat.Format != ExportFormat.Original;

    public string FormatHint => SelectedFormat.Format switch
    {
        ExportFormat.Original => Info is null
            ? "Cuts YouTube's original stream without re-encoding, so there is zero quality loss. Fades need a re-encode and are off."
            : $"Cuts YouTube's original {Info.CodecName} stream without re-encoding: zero quality loss. Saved as .{Info.OriginalExtension}. Fades need a re-encode, so they're off in this mode.",
        ExportFormat.Mp3 => "Re-encoded at 320 kbps, the highest MP3 quality. Plays everywhere.",
        ExportFormat.M4a => "Re-encoded as AAC at 256 kbps. Plays on Apple devices, Android and Windows.",
        ExportFormat.Flac => "Lossless file. Note: YouTube's audio is already lossy, so FLAC keeps what's there but can't add quality.",
        ExportFormat.Wav => "Uncompressed 16-bit PCM. Large files; handy for editors and samplers.",
        _ => "",
    };

    private bool _isExporting, _exportIsError;
    private double _exportProgress;
    private string? _exportMessage, _lastExportPath;
    public bool IsExporting { get => _isExporting; private set { if (Set(ref _isExporting, value)) { OnPropertyChanged(nameof(ExportButtonLabel)); OnPropertyChanged(nameof(ExportButtonGlyph)); Requery(); } } }
    public double ExportProgress { get => _exportProgress; private set => Set(ref _exportProgress, value); }
    public string? ExportMessage { get => _exportMessage; private set { if (Set(ref _exportMessage, value)) OnPropertyChanged(nameof(HasExportMessage)); } }
    public bool ExportIsError { get => _exportIsError; private set => Set(ref _exportIsError, value); }
    public bool HasExportMessage => ExportMessage is not null;
    public string? LastExportPath { get => _lastExportPath; private set { if (Set(ref _lastExportPath, value)) { OnPropertyChanged(nameof(CanShowInFolder)); Requery(); } } }
    public bool CanShowInFolder => LastExportPath is not null;
    public string ExportButtonLabel => IsExporting ? "Cancel export" : "Export clip…";
    public string ExportButtonGlyph => IsExporting ? "" : "";

    private async Task ExportOrCancelAsync()
    {
        if (IsExporting) { _exportCts?.Cancel(); return; }
        if (Info is null || _sourcePath is null || PickSavePath is null) return;

        StopPlayback();
        var fmt = SelectedFormat.Format;
        var ext = FfmpegService.ExtensionFor(fmt, Info);
        var name = FileNames.Sanitize(Info.Title) +
                   (IsFullRange ? "" : $" ({TimeFormat.ForFileName(SelStart)}-{TimeFormat.ForFileName(SelEnd, roundUp: true)})");
        var path = PickSavePath(name, ext, _settings.LastFolder);
        if (path is null) return;
        if (!path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)) path += "." + ext;

        _settings.LastFolder = Path.GetDirectoryName(path);
        _settings.Format = fmt.ToString();
        _settings.FadeIn = FadeIn;
        _settings.FadeOut = FadeOut;
        _settings.Save();

        var cts = _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ExportMessage = null;
        ExportIsError = false;
        LastExportPath = null;
        try
        {
            var artist = Info.Uploader.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ? Info.Uploader[..^8] : Info.Uploader;
            var opts = new ExportOptions(fmt, SelStart, SelEnd, IsFullRange, EffectiveFadeIn, EffectiveFadeOut, Info.Title, artist);
            await _ff.ExportAsync(_sourcePath, path, opts, new Progress<double>(p => ExportProgress = p), cts.Token);
            ExportProgress = 1;
            LastExportPath = path;
            ExportMessage = $"Saved {Path.GetFileName(path)}  ({FormatSize(new FileInfo(path).Length)})";
        }
        catch (OperationCanceledException) { ExportMessage = "Export cancelled."; }
        catch (UserFacingException ex) { ExportMessage = ex.Message; ExportIsError = true; }
        catch (Exception ex)
        {
            Log.Write("Export failed: " + ex);
            ExportMessage = "Export failed: " + ex.Message;
            ExportIsError = true;
        }
        finally
        {
            IsExporting = false;
            _exportCts = null;
            cts.Dispose();
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024.0):0} KB";

    private void ShowInFolder()
    {
        if (LastExportPath is null || !File.Exists(LastExportPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastExportPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Show in folder failed: " + ex.Message); }
    }

    // ================================================================== footer
    private string _toolsText = "";
    private bool _isUpdatingTools;
    public string ToolsText { get => _toolsText; private set => Set(ref _toolsText, value); }
    public bool IsUpdatingTools { get => _isUpdatingTools; private set { if (Set(ref _isUpdatingTools, value)) Requery(); } }

    private async Task UpdateToolsAsync()
    {
        IsUpdatingTools = true;
        SetStatus("Checking for a newer YouTube downloader…", progress: -1);
        try
        {
            await _maintenance;
            var msg = await _tools.UpdateYtDlpAsync(CancellationToken.None);
            await RefreshToolsTextAsync();
            SetStatus(msg);
        }
        catch (UserFacingException ex) { SetStatus(ex.Message, error: true); }
        finally { IsUpdatingTools = false; }
    }

    // ================================================================== cleanup
    public void Dispose()
    {
        _timer.Stop();
        _loadCts?.Cancel();
        _exportCts?.Cancel();
        _setupCts?.Cancel();
        _settings.Save();
        ResetVideoState();
    }
}
