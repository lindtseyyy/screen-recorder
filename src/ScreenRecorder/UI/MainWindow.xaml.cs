using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Infrastructure;
using ScreenRecorder.Recording;

namespace ScreenRecorder.UI;

public partial class MainWindow : Window
{
    private readonly MonitorService _monitors = new();
    private readonly AudioDeviceService _audio = new();
    private readonly MainViewModel _vm;
    private readonly SelectionFrameWindow _frame = new();
    private RecordingBar? _bar;
    private bool _closingAfterStop;

    public MainWindow()
    {
        InitializeComponent();

        var settings = SettingsStore.Load();
        var recorder = new Recorder(_audio);
        _vm = new MainViewModel(_monitors, _audio, recorder, settings);
        DataContext = _vm;

        _vm.RequestRegionSelect += OnRequestRegionSelect;
        _vm.TargetChanged += OnTargetChanged;
        _vm.RecordingUiStarted += OnRecordingUiStarted;
        _vm.SavingUiStarted += OnSavingUiStarted;
        _vm.RecordingUiStopped += OnRecordingUiStopped;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ExcludeFromCapture(this);
        WindowEffects.TryEnableMica(this);
    }

    private async void OnRequestRegionSelect()
    {
        try
        {
            _frame.Hide();
            var monitors = _monitors.Enumerate();
            var target = await RegionSelectorWindow.ShowAsync(this, monitors);
            _vm.SetRegionTarget(target);
        }
        catch (Exception ex)
        {
            Log.Error("Region selection failed: " + ex);
        }
    }

    private void OnTargetChanged(CaptureTarget? target)
    {
        try
        {
            if (_vm.IsBusy || target is null)
            {
                if (!_vm.IsBusy)
                    _frame.Hide();
                return;
            }
            if (target.IsFullScreen)
                _frame.FlashForMonitor(target.Monitor);
            else
                _frame.ShowForTarget(target, recording: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Outline update failed: " + ex.Message);
        }
    }

    private void OnRecordingUiStarted()
    {
        try
        {
            var target = _vm.CurrentTarget;
            if (target is { IsFullScreen: false })
                _frame.ShowForTarget(target, recording: true); // red border stays while recording
            else
                _frame.Hide();

            WindowState = WindowState.Minimized;

            if (target is not null)
            {
                _bar = new RecordingBar(_vm);
                _bar.PlaceAtTopCenter(target.Monitor);
                _bar.Show();
            }

            var drawing = new GeometryDrawing(Brushes.Red, null,
                new EllipseGeometry(new Point(8, 8), 7, 7));
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            TaskbarItemInfo = new TaskbarItemInfo
            {
                Overlay = image,
                Description = "Recording",
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Recording UI start failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Stop pressed (or an error/low disk stopped the recording): drop the
    /// recording UI and bring the window back with the saving screen right
    /// away, rather than when the file is finished.
    /// </summary>
    private void OnSavingUiStarted()
    {
        try
        {
            _bar?.Close();
            _bar = null;
            _frame.Hide();

            TaskbarItemInfo = new TaskbarItemInfo
            {
                ProgressState = TaskbarItemProgressState.Normal,
                ProgressValue = _vm.SaveProgressValue / 100,
                Description = "Saving recording",
            };

            WindowState = WindowState.Normal;
            Show();
            Activate();
        }
        catch (Exception ex)
        {
            Log.Warn("Saving UI start failed: " + ex.Message);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SaveProgressValue) && _vm.IsSaving
            && TaskbarItemInfo is { } info)
            info.ProgressValue = _vm.SaveProgressValue / 100;
    }

    private void OnRecordingUiStopped()
    {
        try
        {
            _bar?.Close();
            _bar = null;
            _frame.Hide();

            TaskbarItemInfo = null;

            WindowState = WindowState.Normal;
            Show();
            Activate();

            // Re-show the outline for the current target.
            OnTargetChanged(_vm.CurrentTarget);
        }
        catch (Exception ex)
        {
            Log.Warn("Recording UI stop failed: " + ex.Message);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    /// <summary>
    /// Opens/closes the dark dropdowns (DarkComboBox template): the click box
    /// is a plain Border, so it toggles the templated ComboBox directly.
    /// </summary>
    private void OnComboBoxToggle(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { TemplatedParent: ComboBox combo })
        {
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            e.Handled = true;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsBusy && !_closingAfterStop)
        {
            // Stop the recording (or let the save in progress finish) first, then
            // close; closing mid-save would leave an unplayable .part file.
            e.Cancel = true;
            try
            {
                // A recording still starting can't be stopped yet: let it start
                // (a failed start was already reported), then stop and save it.
                try { await _vm.StartTask; } catch { /* handled by the view-model */ }
                await _vm.Recorder.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Stop-on-close failed: " + ex.Message);
            }
            _closingAfterStop = true;
            Close();
            return;
        }
        _frame.Close();
        _monitors.Dispose();
        _audio.Dispose();
        _vm.Recorder.Dispose();
        base.OnClosing(e);
    }
}
