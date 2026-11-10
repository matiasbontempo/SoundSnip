using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundSnip;
using SoundSnip.Core;

// Usage: SoundSnip.UiTest <youtube-url> <outputDir>
// Drives the real view-model and waveform control on a WPF dispatcher (no screen or mouse needed).

internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        int result = 1;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { result = await RunAsync(args); }
                catch (Exception ex) { Console.WriteLine("UNHANDLED: " + ex); result = 2; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(40);
        }
        return cond();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: UiTest <url> <outDir>"); return 2; }
        // "url=https://…" keeps the link out of a bare "http…" argument, which the real MainWindow would auto-load.
        var url = args[0].StartsWith("url=") ? args[0][4..] : args[0];
        var outDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outDir);

        // ============================================================ view-model: invalid input
        Console.WriteLine("== ViewModel: input validation");
        var vm = new MainViewModel();
        Check("tools resolve (env overrides)", new ToolManager().RequiredToolsReady);
        await vm.InitializeAsync(null);
        Check("IsToolsReady after init", vm.IsToolsReady);
        vm.Url = "hello world";
        vm.PrimaryActionCommand.Execute(null);
        await WaitUntil(() => vm.StatusIsError, 2000);
        Check("junk link shows a friendly error", vm.StatusIsError && vm.StatusText.Contains("link"), vm.StatusText);
        Check("welcome screen visible before loading", vm.ShowWelcome && !vm.IsEditorReady);

        // ============================================================ view-model: real load
        Console.WriteLine("== ViewModel: load a real video");
        vm.Url = url;
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.StatusText)) statuses.Add(vm.StatusText); };
        vm.PrimaryActionCommand.Execute(null);
        bool ready = await WaitUntil(() => vm.IsEditorReady, 90_000);
        Check("editor becomes ready", ready, vm.StatusText);
        if (!ready) { Console.WriteLine("\nCannot continue without a loaded video."); return 1; }
        Check("title / quality text populated", vm.TitleText.Length > 0 && vm.QualityText.Contains("kbps"), $"{vm.TitleText} | {vm.QualityText}");
        Check("status went through lookup → download → prepare",
            statuses.Any(s => s.StartsWith("Looking up")) && statuses.Any(s => s.StartsWith("Downloading")) && statuses.Any(s => s.StartsWith("Preparing")));
        await WaitUntil(() => vm.Thumbnail is not null, 8000);
        Check("thumbnail loaded", vm.Thumbnail is not null);
        Check("selection starts as whole track", vm.SelStart == 0 && Math.Abs(vm.SelEnd - vm.Duration) < 1e-6 && vm.IsFullRange);
        double dur = vm.Duration;

        // ============================================================ loading again (same video, then cancel mid-way)
        Console.WriteLine("== ViewModel: reloading");
        vm.SetSelection(3, 5);
        vm.PrimaryActionCommand.Execute(null); // press "Load" again on the same link
        Check("reload resets the editor while loading", await WaitUntil(() => vm.IsBusy, 3000));
        bool ready2 = await WaitUntil(() => vm.IsEditorReady && !vm.IsBusy, 90_000);
        Check("reloading the same video works", ready2 && !vm.StatusIsError, vm.StatusText);
        Check("…and starts with the whole track selected again", vm.SelStart == 0 && Math.Abs(vm.SelEnd - vm.Duration) < 1e-6);
        vm.PrimaryActionCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy, 3000);
        vm.PrimaryActionCommand.Execute(null); // the button now says "Cancel"
        Check("cancel mid-load stops cleanly", await WaitUntil(() => !vm.IsBusy, 10_000) && !vm.IsEditorReady && !vm.StatusIsError, vm.StatusText);
        vm.PrimaryActionCommand.Execute(null);
        Check("loading works again after a cancel", await WaitUntil(() => vm.IsEditorReady && !vm.IsBusy, 90_000) && !vm.StatusIsError, vm.StatusText);
        dur = vm.Duration;

        // ============================================================ selection logic
        Console.WriteLine("== ViewModel: trim logic");
        vm.SetSelection(3.8, 12.5);
        Check("SetSelection", Math.Abs(vm.SelStart - 3.8) < 1e-9 && Math.Abs(vm.SelEnd - 12.5) < 1e-9 && !vm.IsFullRange);
        Check("length text", vm.LengthText == "0:08.700", vm.LengthText);
        vm.SetSelection(-5, dur + 50);
        Check("selection clamps to the track", vm.SelStart == 0 && Math.Abs(vm.SelEnd - dur) < 1e-9);
        vm.SetSelection(5, 5.0);
        Check("selection keeps a minimum length", vm.SelEnd - vm.SelStart >= 0.049, $"{vm.SelEnd - vm.SelStart:F3}");
        vm.StartText = "0:03.8";
        Check("typing start time '0:03.8'", Math.Abs(vm.SelStart - 3.8) < 1e-9 && vm.StartText == "0:03.800", vm.StartText);
        vm.EndText = "12.5";
        Check("typing end time '12.5'", Math.Abs(vm.SelEnd - 12.5) < 1e-9, vm.EndText);
        vm.StartText = "garbage";
        Check("garbage input is rejected (value kept)", Math.Abs(vm.SelStart - 3.8) < 1e-9);
        vm.StartText = "99";
        Check("start typed past the end is held below the end", vm.SelStart <= vm.SelEnd - 0.049, $"start {vm.SelStart:F3} end {vm.SelEnd:F3}");
        vm.SetSelection(3.8, 12.5);
        vm.NudgeStartCommand.Execute("1");
        Check("nudge start +0.1 s", Math.Abs(vm.SelStart - 3.9) < 1e-9, vm.StartText);
        vm.NudgeEndCommand.Execute("-1");
        Check("nudge end −0.1 s", Math.Abs(vm.SelEnd - 12.4) < 1e-9, vm.EndText);
        vm.SetSelection(3.8, 12.5);
        vm.Seek(6);
        vm.SetStartHereCommand.Execute(null);
        Check("'set start to playhead' (I)", Math.Abs(vm.SelStart - 6) < 1e-9);
        vm.Seek(10);
        vm.SetEndHereCommand.Execute(null);
        Check("'set end to playhead' (O)", Math.Abs(vm.SelEnd - 10) < 1e-9);
        vm.Seek(15);
        vm.SetStartHereCommand.Execute(null);
        Check("start set beyond the end moves the end to the track end", Math.Abs(vm.SelStart - 15) < 1e-9 && Math.Abs(vm.SelEnd - dur) < 1e-9);
        vm.SelectAllCommand.Execute(null);
        Check("select whole track", vm.IsFullRange);

        // The app remembers the last export format between runs, so don't assume the starting one.
        vm.SelectedFormat = vm.Formats.First(f => f.Format == ExportFormat.Original);
        Check("Original hides fades", !vm.FadeEnabled);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == ExportFormat.Mp3);
        Check("MP3 enables fades", vm.FadeEnabled);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == ExportFormat.Original);
        Check("Original format hint names the real codec", vm.FormatHint.Contains("Opus") && vm.FormatHint.Contains(".opus"), vm.FormatHint);

        // ============================================================ playback
        Console.WriteLine("== ViewModel: playback & previews");
        try
        {
            vm.SetSelection(3.8, 12.5);
            vm.PlayPauseCommand.Execute(null);
            Check("Play starts", vm.IsPlaying && vm.PlayLabel == "Stop");
            await Task.Delay(500);
            Check("playhead follows playback", vm.Playhead > 3.9 && vm.Playhead < 5.0, $"{vm.Playhead:F3}");
            vm.PlayPauseCommand.Execute(null);
            Check("Stop stops", !vm.IsPlaying && vm.PlayLabel == "Play selection");
            double stoppedAt = vm.Playhead;
            await Task.Delay(200);
            Check("playhead stays where it stopped", Math.Abs(vm.Playhead - stoppedAt) < 0.001);
            vm.PlayPauseCommand.Execute(null);
            Check("Play resumes from the playhead", vm.IsPlaying);
            await Task.Delay(150);
            Check("…and continues from there, not from the start", vm.Playhead >= stoppedAt - 0.01, $"{vm.Playhead:F3} vs {stoppedAt:F3}");
            vm.StopPlayback();

            vm.PreviewStartCommand.Execute(null);
            Check("'Hear start' begins at the start handle", vm.IsPlaying && Math.Abs(vm.Playhead - 3.8) < 0.1, $"{vm.Playhead:F3}");
            Check("'Hear start' ends by itself after ~2 s", await WaitUntil(() => !vm.IsPlaying, 4000));
            vm.PreviewEndCommand.Execute(null);
            Check("'Hear end' begins 2 s before the end handle", vm.IsPlaying && Math.Abs(vm.Playhead - 10.5) < 0.1, $"{vm.Playhead:F3}");
            Check("'Hear end' ends by itself", await WaitUntil(() => !vm.IsPlaying, 4000));

            vm.SetSelection(3.8, 4.4);
            vm.Loop = true;
            vm.Seek(3.8);
            vm.PlayPauseCommand.Execute(null);
            await Task.Delay(1900);
            Check("loop: still playing after > 3 × clip length", vm.IsPlaying);
            vm.Loop = false;
            Check("loop off: stops at the end of the clip", await WaitUntil(() => !vm.IsPlaying, 2500));
            Check("…and the playhead returns to the start of the clip", Math.Abs(vm.Playhead - 3.8) < 0.05, $"{vm.Playhead:F3}");
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            Console.WriteLine("  [SKIP] playback needs an audio output device: " + ex.Message);
        }
        vm.Loop = false;

        // ============================================================ export through the view-model
        Console.WriteLine("== ViewModel: export");
        vm.SetSelection(3.8, 12.5);
        foreach (var (fmt, ext) in new[] { (ExportFormat.Original, "opus"), (ExportFormat.Mp3, "mp3") })
        {
            vm.SelectedFormat = vm.Formats.First(f => f.Format == fmt);
            vm.FadeIn = 0.5; vm.FadeOut = 0.5;
            string? asked = null;
            vm.PickSavePath = (name, e, _) => { asked = name + "|" + e; return Path.Combine(outDir, $"vm-{fmt}"); }; // no extension: VM must add it
            vm.ExportCommand.Execute(null);
            Check($"{fmt}: save dialog asked for a sensible name", await WaitUntil(() => asked is not null, 2000) && asked!.StartsWith("Me at the zoo (00m03s-00m13s)|" + ext), asked ?? "null");
            Check($"{fmt}: export finishes", await WaitUntil(() => vm.HasExportMessage && !vm.IsExporting, 60_000), vm.ExportMessage ?? "");
            var file = Path.Combine(outDir, $"vm-{fmt}.{ext}");
            Check($"{fmt}: extension added, file written", File.Exists(file) && vm.LastExportPath == file, vm.ExportMessage ?? "");
            Check($"{fmt}: success message", vm.ExportMessage!.StartsWith("Saved") && !vm.ExportIsError, vm.ExportMessage);
            var len = File.Exists(file) ? await DurationAsync(file) : -1;
            Check($"{fmt}: exported length matches the selection", Math.Abs(len - 8.7) < 0.08, $"{len:F3}s");
        }

        // cancelling the save dialog must change nothing
        vm.PickSavePath = (_, _, _) => null;
        var before = vm.LastExportPath;
        vm.ExportCommand.Execute(null);
        await Task.Delay(300);
        Check("cancelling the save dialog exports nothing", !vm.IsExporting && vm.LastExportPath == before);

        // ============================================================ waveform control (no real mouse)
        Console.WriteLine("== WaveformView: handles, selection, zoom, pan");
        var wav = Directory.EnumerateFiles(AppPaths.WorkDir, "preview.wav", SearchOption.AllDirectories).First();
        var wf = WaveformData.Build(wav, CancellationToken.None);
        var view = new WaveformView { Width = 1000, Height = 220, Waveform = wf, SelectionStart = 0, SelectionEnd = wf.DurationSeconds };
        view.Measure(new Size(1000, 220));
        view.Arrange(new Rect(0, 0, 1000, 220));
        view.UpdateLayout();

        var drags = new List<(double S, double E)>(); var seeks = new List<double>();
        view.SelectionDragged += (s, e) => { drags.Add((s, e)); view.SelectionStart = s; view.SelectionEnd = e; }; // what the VM binding does
        view.SeekRequested += t => seeks.Add(t);

        Check("the whole track is in view at first", view.ViewStart == 0 && Math.Abs(view.ViewSpan - wf.DurationSeconds) < 1e-6);
        Check("time 0 maps inside the control, not on its edge", view.XAtTime(0) >= 8 && view.XAtTime(wf.DurationSeconds) <= 992, $"{view.XAtTime(0):F1} … {view.XAtTime(wf.DurationSeconds):F1}");
        Check("handle hit-test: start / end / none",
            view.HitTest(view.XAtTime(0) + 3) == "start" && view.HitTest(view.XAtTime(wf.DurationSeconds) - 3) == "end" && view.HitTest(500) == "none");

        view.PointerDown(view.XAtTime(0)); view.PointerMove(300); view.PointerUp(300);
        Check("dragging the start handle moves only the start", drags.Count > 0 && Math.Abs(drags[^1].S - view.TimeAtX(300)) < 0.01 && Math.Abs(drags[^1].E - wf.DurationSeconds) < 1e-6, $"{drags[^1].S:F3}");

        view.PointerDown(view.XAtTime(wf.DurationSeconds)); view.PointerMove(800); view.PointerUp(800);
        Check("dragging the end handle moves only the end", Math.Abs(view.SelectionEnd - view.TimeAtX(800)) < 0.01, $"{view.SelectionEnd:F3}");

        view.PointerDown(view.XAtTime(view.SelectionEnd)); view.PointerMove(100); view.PointerUp(100);
        Check("end handle can't cross the start handle", view.SelectionEnd >= view.SelectionStart + 0.049, $"{view.SelectionStart:F3} / {view.SelectionEnd:F3}");

        view.SelectionStart = 2; view.SelectionEnd = 8;
        drags.Clear(); seeks.Clear();
        view.PointerDown(view.XAtTime(9)); view.PointerUp(view.XAtTime(9));
        Check("a plain click seeks instead of selecting", seeks.Count == 1 && Math.Abs(seeks[0] - 9) < 0.01 && drags.Count == 0, seeks.Count > 0 ? $"{seeks[0]:F3}" : "none");

        view.PointerDown(view.XAtTime(10)); view.PointerMove(view.XAtTime(14)); view.PointerUp(view.XAtTime(14));
        Check("dragging on empty waveform draws a new range", drags.Count > 0 && Math.Abs(drags[^1].S - 10) < 0.05 && Math.Abs(drags[^1].E - 14) < 0.05, $"{drags[^1].S:F2}-{drags[^1].E:F2}");
        view.PointerDown(view.XAtTime(15)); view.PointerMove(view.XAtTime(12)); view.PointerUp(view.XAtTime(12));
        Check("…also when dragged right-to-left", Math.Abs(drags[^1].S - 12) < 0.05 && Math.Abs(drags[^1].E - 15) < 0.05, $"{drags[^1].S:F2}-{drags[^1].E:F2}");

        // zoom keeps the point under the cursor fixed
        view.SelectionStart = 4; view.SelectionEnd = 9;
        double tUnder = view.TimeAtX(600);
        view.Wheel(600, 120, shift: false);
        Check("wheel up zooms in", view.ViewSpan < wf.DurationSeconds * 0.9, $"{view.ViewSpan:F2}s");
        Check("zoom keeps the time under the cursor in place", Math.Abs(view.TimeAtX(600) - tUnder) < 0.02, $"{tUnder:F3} → {view.TimeAtX(600):F3}");
        for (int i = 0; i < 30; i++) view.Wheel(600, 120, false);
        Check("zoom stops at the minimum span", view.ViewSpan >= 0.249 && view.ViewSpan <= 0.26, $"{view.ViewSpan:F3}s");
        double beforePan = view.ViewStart;
        view.Wheel(500, -120, shift: true);
        Check("Shift+wheel pans", view.ViewStart != beforePan);
        view.PanDown(500); view.PointerMove(300); view.PointerUp(300);
        Check("right-drag pans", view.ViewStart >= 0 && view.ViewStart + view.ViewSpan <= wf.DurationSeconds + 1e-9);
        for (int i = 0; i < 60; i++) view.Wheel(500, -120, shift: true);
        Check("panning never leaves the track", view.ViewStart >= 0 && view.ViewStart + view.ViewSpan <= wf.DurationSeconds + 1e-9, $"{view.ViewStart:F2}…{view.ViewStart + view.ViewSpan:F2}");
        view.ZoomAll();
        Check("Show all restores the full view", view.ViewStart == 0 && Math.Abs(view.ViewSpan - wf.DurationSeconds) < 1e-6);
        view.SelectionStart = 6; view.SelectionEnd = 8;
        view.ZoomToSelection();
        Check("Zoom to selection frames the selection", view.ViewStart < 6 && view.ViewStart + view.ViewSpan > 8 && view.ViewSpan < 4, $"{view.ViewStart:F2}…{view.ViewStart + view.ViewSpan:F2}");
        view.ZoomAll();
        view.ZoomAround(5, 0.2);
        view.AutoScroll = true;
        view.Playhead = 18.5;
        Check("auto-scroll follows the playhead out of view", view.Playhead >= view.ViewStart && view.Playhead <= view.ViewStart + view.ViewSpan, $"view {view.ViewStart:F2}…{view.ViewStart + view.ViewSpan:F2}");

        // render to PNGs so the drawing can be inspected
        view.AutoScroll = false;
        view.ZoomAll(); view.SelectionStart = 3.8; view.SelectionEnd = 12.5; view.Playhead = 7.2;
        Render(view, Path.Combine(outDir, "wave-full.png"));
        view.ZoomAround(8, 0.2);
        Render(view, Path.Combine(outDir, "wave-zoomed.png"));
        Check("rendered PNGs written", File.Exists(Path.Combine(outDir, "wave-full.png")) && File.Exists(Path.Combine(outDir, "wave-zoomed.png")));

        vm.Dispose();
        if (args.Length > 2 && args[2] == "--screens") await ScreensAsync(url, outDir);
        Console.WriteLine(_failures == 0 ? "\nALL UI CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- real window, forced states (no network needed for the setup screens)
    private static void SetPrivate(object target, string prop, object? value) =>
        target.GetType().GetProperty(prop, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static async Task ScreensAsync(string url, string outDir)
    {
        Console.WriteLine("== Screens (real MainWindow)");
        var app = new SoundSnip.App();
        app.InitializeComponent();
        var win = new MainWindow { Left = 40, Top = 40 };
        win.Show();
        var vm = (MainViewModel)win.DataContext;
        await WaitUntil(() => vm.IsToolsReady, 5000);
        await Task.Delay(400);
        Snap(win, Path.Combine(outDir, "screen-welcome.png"));

        // setup overlay, in progress and in error
        SetPrivate(vm, "IsSettingUp", true);
        SetPrivate(vm, "SetupText", "Downloading ffmpeg… 48.3 MB / 115.0 MB");
        SetPrivate(vm, "SetupFraction", 0.42);
        await Task.Delay(300);
        Snap(win, Path.Combine(outDir, "screen-setup.png"));
        SetPrivate(vm, "SetupError", "Couldn't reach the download server. Please check your internet connection and try again.");
        await Task.Delay(300);
        Snap(win, Path.Combine(outDir, "screen-setup-error.png"));
        SetPrivate(vm, "SetupError", null);
        SetPrivate(vm, "IsSettingUp", false);

        // loading state mid-download
        var setStatus = typeof(MainViewModel).GetMethod("SetStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setStatus.Invoke(vm, new object?[] { "Downloading the best audio… 63%  ·  4.2 MB/s", false, 0.63 });
        vm.Url = url;
        await Task.Delay(300);
        Snap(win, Path.Combine(outDir, "screen-downloading.png"));

        // a real loaded video, dark then light
        vm.PrimaryActionCommand.Execute(null);
        await WaitUntil(() => vm.IsEditorReady, 90_000);
        await WaitUntil(() => vm.Thumbnail is not null, 8000);
        vm.SetSelection(3.8, 12.5);
        vm.Seek(7.2);
        vm.SelectedFormat = vm.Formats.First(f => f.Format == ExportFormat.Mp3);
        vm.FadeIn = 0.5; vm.FadeOut = 1.5;
        await Task.Delay(500);
        Snap(win, Path.Combine(outDir, "screen-editor-dark.png"));
        ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;
        await Task.Delay(600);
        Snap(win, Path.Combine(outDir, "screen-editor-light.png"));
        Check("screens captured", Directory.GetFiles(outDir, "screen-*.png").Length == 6);
        win.Close();
    }

    private static void Snap(Window win, string path)
    {
        var el = (FrameworkElement)win.Content;
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(win.Background, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(bg);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Render(FrameworkElement el, string path)
    {
        // An element that isn't in a window only re-runs OnRender after an explicit layout pass.
        el.InvalidateVisual();
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static async Task<double> DurationAsync(string file)
    {
        var ff = new ToolManager().FfmpegPath;
        var r = await ProcessRunner.RunAsync(ff, new[] { "-hide_banner", "-nostdin", "-i", file, "-f", "null", "-" });
        var m = Regex.Matches(r.StdErr, @"time=(\d+):(\d+):(\d+\.\d+)");
        if (m.Count == 0) return -1;
        var l = m[^1];
        return int.Parse(l.Groups[1].Value) * 3600 + int.Parse(l.Groups[2].Value) * 60 + double.Parse(l.Groups[3].Value, CultureInfo.InvariantCulture);
    }
}
