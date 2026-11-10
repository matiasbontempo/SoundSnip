using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SoundSnip;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _syncingScroll;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PickSavePath = PickSavePath;

        Wave.SelectionDragged += (s, e) => _vm.SetSelection(s, e);
        Wave.SeekRequested += t => _vm.Seek(t);
        Wave.ViewChanged += (_, _) => SyncScroll();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Optional: SoundSnip.exe "https://youtu.be/…" loads that link straight away.
        var startUrl = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        UrlBox.Focus();
        await _vm.InitializeAsync(startUrl);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _vm.Dispose();

    private string? PickSavePath(string suggestedName, string extension, string? initialDir)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save audio clip",
            FileName = suggestedName + "." + extension,
            DefaultExt = "." + extension,
            AddExtension = true,
            Filter = $"{extension.ToUpperInvariant()} audio (*.{extension})|*.{extension}|All files (*.*)|*.*",
            OverwritePrompt = true,
            InitialDirectory = !string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir)
                ? initialDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    // ------------------------------------------------------------------ keyboard
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsEditorReady) return;
        // Don't steal keys while the user is typing or choosing from a list.
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        switch (e.Key)
        {
            case Key.Space:
                if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return; // let buttons use Space
                _vm.PlayPause();
                e.Handled = true;
                break;
            case Key.I: _vm.SetStartHereCommand.Execute(null); e.Handled = true; break;
            case Key.O: _vm.SetEndHereCommand.Execute(null); e.Handled = true; break;
            case Key.L: _vm.Loop = !_vm.Loop; e.Handled = true; break;
            case Key.Escape: _vm.StopPlayback(); e.Handled = true; break;
            case Key.Home: _vm.Seek(_vm.SelStart); e.Handled = true; break;
        }
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm.PrimaryActionCommand.CanExecute(null)) _vm.PrimaryActionCommand.Execute(null);
        e.Handled = true;
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        Wave.Focus();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ zoom / scroll
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Wave.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Wave.ZoomOut();
    private void ZoomAll_Click(object sender, RoutedEventArgs e) => Wave.ZoomAll();
    private void ZoomSelection_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();

    private void SyncScroll()
    {
        _syncingScroll = true;
        try
        {
            double dur = Wave.Duration, span = Wave.ViewSpan;
            WaveScroll.Maximum = Math.Max(0, dur - span);
            WaveScroll.ViewportSize = span;
            WaveScroll.LargeChange = span * 0.9;
            WaveScroll.SmallChange = span * 0.1;
            WaveScroll.Value = Wave.ViewStart;
            WaveScroll.IsEnabled = dur - span > 0.001;
        }
        finally { _syncingScroll = false; }
    }

    private void WaveScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingScroll) Wave.ScrollTo(e.NewValue);
    }
}
