using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Encoding;
using ScreenRecorder.Infrastructure;
using ScreenRecorder.Recording;

namespace ScreenRecorder.UI;

/// <summary>One tile in the source picker: a full monitor or the region option.</summary>
public sealed class SourceItem : INotifyPropertyChanged
{
    private string _subtitle;

    public SourceItem(bool isRegion, MonitorInfo? monitor, string title, string subtitle)
    {
        IsRegion = isRegion;
        Monitor = monitor;
        Title = title;
        _subtitle = subtitle;
    }

    public bool IsRegion { get; }
    public MonitorInfo? Monitor { get; }
    public string Title { get; }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle != value)
            {
                _subtitle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Main window view-model: binds the UI to Recorder/services/settings
/// (plain INotifyPropertyChanged, no framework).
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly MonitorService _monitors;
    private readonly AudioDeviceService _audio;
    private readonly AppSettings _settings;

    private SourceItem? _selectedSource;
    private CaptureTarget? _regionTarget;
    private bool _systemAudioOn;
    private bool _micOn;
    private MicDevice? _selectedMicrophone;
    private bool _micPickerVisible;
    private bool _micNameVisible;
    private bool _micChipEnabled = true;
    private string _micChipTooltip = string.Empty;
    private string _micDisplayName = string.Empty;
    private string _saveFolder = string.Empty;
    private int _fps = 30;
    private VideoQuality _quality = VideoQuality.Medium;
    private bool _showCursor = true;
    private bool _isRecording;
    private bool _isPaused;
    private bool _recordEnabled = true;
    private bool _statusVisible;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private string? _savedPath;
    private bool _showPrivacyButton;
    private bool _recordingSystemOn;
    private bool _recordingMicOn;
    private bool _micMuted;
    private string _storageEstimate = string.Empty;
    private bool _isStarting;
    private Task _startTask = Task.CompletedTask;
    private bool _isSaving;
    private double _saveProgressValue;
    private string _saveStageText = string.Empty;
    private string _saveDetailsText = string.Empty;
    private TimeSpan _savingDuration;
    private readonly Stopwatch _saveWatch = new();

    public MainViewModel(
        MonitorService monitors, AudioDeviceService audio, Recorder recorder, AppSettings settings)
    {
        _monitors = monitors;
        _audio = audio;
        Recorder = recorder;
        _settings = settings;

        _systemAudioOn = settings.SystemAudio;
        _micOn = settings.Microphone;
        _saveFolder = settings.SaveFolder;
        _fps = settings.Fps;
        _quality = settings.Quality;
        _showCursor = settings.ShowCursor;

        Sources = new ObservableCollection<SourceItem>();
        Microphones = new ObservableCollection<MicDevice>();

        RecordCommand = new RelayCommand(
            async () => await RecordAsync(), () => !IsBusy && RecordEnabled);
        NoAudioCommand = new RelayCommand(
            () =>
            {
                SystemAudioOn = false;
                MicOn = false;
            },
            // Stays enabled while idle so the pill highlights (rather than grays
            // out) when the no-audio state is active; clicking it then is a no-op.
            () => !IsBusy);
        PauseResumeCommand = new RelayCommand(PauseOrResume, () => IsRecording);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsRecording);
        ToggleMicMuteCommand = new RelayCommand(
            ToggleMicMute, () => IsRecording && RecordingMicOn);
        ChangeFolderCommand = new RelayCommand(ChangeFolder, () => !IsBusy);
        OpenFileCommand = new RelayCommand(OpenFile, () => SavedPath is not null);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => SavedPath is not null);
        OpenMicSettingsCommand = new RelayCommand(OpenMicSettings);

        _monitors.MonitorsChanged += RefreshMonitors;
        _audio.CaptureDevicesChanged += RefreshMics;
        Recorder.RecordingStarted += OnRecordingStarted;
        Recorder.FinalizingStarted += OnFinalizingStarted;
        Recorder.SaveProgressChanged += OnSaveProgress;
        Recorder.RecordingStopped += OnRecordingStopped;
        Recorder.RecordingFailed += OnRecordingFailed;

        RefreshMonitors();
        RefreshMics();
        RefreshEstimate();
    }

    public Recorder Recorder { get; }
    public ObservableCollection<SourceItem> Sources { get; }
    public ObservableCollection<MicDevice> Microphones { get; }
    public IReadOnlyList<int> FpsOptions { get; } = [30, 60];
    public IReadOnlyList<VideoQuality> QualityOptions { get; } =
        [VideoQuality.Low, VideoQuality.Medium, VideoQuality.High];

    public ICommand RecordCommand { get; }
    public ICommand NoAudioCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleMicMuteCommand { get; }
    public ICommand ChangeFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand OpenMicSettingsCommand { get; }

    /// <summary>Raised when the Region tile is clicked; the window shows the selector
    /// and reports back via <see cref="SetRegionTarget"/>.</summary>
    public event Action? RequestRegionSelect;

    /// <summary>Raised when the current target changes (outline window follows).</summary>
    public event Action<CaptureTarget?>? TargetChanged;

    public event Action? RecordingUiStarted;

    /// <summary>Recording ended and saving began: the window shows the saving screen.</summary>
    public event Action? SavingUiStarted;

    /// <summary>Saving finished (or failed): back to the idle window.</summary>
    public event Action? RecordingUiStopped;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureTarget? CurrentTarget { get; private set; }

    public SourceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || value == _selectedSource)
                return;
            if (value.IsRegion)
            {
                // Opens the selector; selection changes only if a region is confirmed.
                RequestRegionSelect?.Invoke();
                return;
            }
            _selectedSource = value;
            OnPropertyChanged();
            UpdateTarget();
        }
    }

    /// <summary>Called by the window after the region selector closes.</summary>
    public void SetRegionTarget(CaptureTarget? target)
    {
        if (target is null)
        {
            OnPropertyChanged(nameof(SelectedSource)); // revert the visual selection
            return;
        }
        _regionTarget = target;
        var regionItem = Sources.FirstOrDefault(s => s.IsRegion);
        if (regionItem is not null)
            regionItem.Subtitle = $"{target.Width} × {target.Height} on Display {target.Monitor.Number}";
        _selectedSource = regionItem;
        OnPropertyChanged(nameof(SelectedSource));
        UpdateTarget();
    }

    public bool SystemAudioOn
    {
        get => _systemAudioOn;
        set
        {
            if (IsBusy || !Set(ref _systemAudioOn, value))
                return;
            _settings.SystemAudio = value;
            SettingsStore.Save(_settings);
            CommandManager.InvalidateRequerySuggested();
            RefreshEstimate();
        }
    }

    public bool MicOn
    {
        get => _micOn;
        set
        {
            if (IsBusy || value == _micOn)
                return;
            if (value && Microphones.Count == 0)
                return; // no microphone: refuse (chip is disabled anyway)
            _micOn = value;
            OnPropertyChanged();
            _settings.Microphone = value;
            SettingsStore.Save(_settings);
            RefreshMicVisibility();
            CommandManager.InvalidateRequerySuggested();
            RefreshEstimate();
        }
    }

    public bool MicChipEnabled
    {
        get => _micChipEnabled;
        private set => Set(ref _micChipEnabled, value);
    }

    public string MicChipTooltip
    {
        get => _micChipTooltip;
        private set => Set(ref _micChipTooltip, value);
    }

    public bool MicPickerVisible
    {
        get => _micPickerVisible;
        private set => Set(ref _micPickerVisible, value);
    }

    public bool MicNameVisible
    {
        get => _micNameVisible;
        private set => Set(ref _micNameVisible, value);
    }

    public string MicDisplayName
    {
        get => _micDisplayName;
        private set => Set(ref _micDisplayName, value);
    }

    public MicDevice? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (!Set(ref _selectedMicrophone, value))
                return;
            _settings.MicrophoneId = value?.Id;
            SettingsStore.Save(_settings);
        }
    }

    public string SaveFolder
    {
        get => _saveFolder;
        private set => Set(ref _saveFolder, value);
    }

    /// <summary>Free-space recording-time estimate for the current quality settings.</summary>
    public string StorageEstimate
    {
        get => _storageEstimate;
        private set
        {
            if (Set(ref _storageEstimate, value))
                OnPropertyChanged(nameof(HasStorageEstimate));
        }
    }

    public bool HasStorageEstimate => !string.IsNullOrEmpty(StorageEstimate);

    public int Fps
    {
        get => _fps;
        set
        {
            if (IsBusy || !Set(ref _fps, value))
                return;
            _settings.Fps = value;
            SettingsStore.Save(_settings);
            RefreshEstimate();
        }
    }

    public VideoQuality Quality
    {
        get => _quality;
        set
        {
            if (IsBusy || !Set(ref _quality, value))
                return;
            _settings.Quality = value;
            SettingsStore.Save(_settings);
            RefreshEstimate();
        }
    }

    public bool ShowCursor
    {
        get => _showCursor;
        set
        {
            if (IsBusy || !Set(ref _showCursor, value))
                return;
            _settings.ShowCursor = value;
            SettingsStore.Save(_settings);
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (Set(ref _isRecording, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Recording ended and the file is being finished (saving screen).</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (Set(ref _isSaving, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Record was pressed and the recorder is opening devices and the encoder.</summary>
    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (Set(ref _isStarting, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>The last start; the window waits for it before closing.</summary>
    public Task StartTask => _startTask;

    /// <summary>Starting, recording or saving: settings are locked and Record is unavailable.</summary>
    public bool IsBusy => IsStarting || IsRecording || IsSaving;

    /// <summary>Saving progress, 0–100; never goes back.</summary>
    public double SaveProgressValue
    {
        get => _saveProgressValue;
        private set
        {
            if (Set(ref _saveProgressValue, value))
                OnPropertyChanged(nameof(SavePercentText));
        }
    }

    public string SavePercentText => $"{Math.Floor(SaveProgressValue):F0}%";

    public string SaveStageText
    {
        get => _saveStageText;
        private set => Set(ref _saveStageText, value);
    }

    /// <summary>"Length 1:02:03 · 2.31 GB · saving for 0:00:04".</summary>
    public string SaveDetailsText
    {
        get => _saveDetailsText;
        private set => Set(ref _saveDetailsText, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => Set(ref _isPaused, value);
    }

    public bool RecordEnabled
    {
        get => _recordEnabled;
        private set => Set(ref _recordEnabled, value);
    }

    public bool StatusVisible
    {
        get => _statusVisible;
        private set => Set(ref _statusVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public string? SavedPath
    {
        get => _savedPath;
        private set
        {
            if (Set(ref _savedPath, value))
                OnPropertyChanged(nameof(HasSavedPath));
        }
    }

    public bool HasSavedPath => SavedPath is not null;

    public string SavedFileName => SavedPath is null ? string.Empty : Path.GetFileName(SavedPath);

    public bool ShowPrivacyButton
    {
        get => _showPrivacyButton;
        private set => Set(ref _showPrivacyButton, value);
    }

    /// <summary>Audio sources of the ongoing recording (for the recording bar icons).</summary>
    public bool RecordingSystemOn
    {
        get => _recordingSystemOn;
        private set => Set(ref _recordingSystemOn, value);
    }

    public bool RecordingMicOn
    {
        get => _recordingMicOn;
        private set => Set(ref _recordingMicOn, value);
    }

    /// <summary>Whether the mic is muted mid-recording (recording bar toggle).</summary>
    public bool MicMuted
    {
        get => _micMuted;
        private set => Set(ref _micMuted, value);
    }

    // ---- monitors ----

    private void RefreshMonitors()
    {
        var monitors = _monitors.Enumerate();
        if (monitors.Count == 0)
        {
            ShowStatus("No monitors found.", isError: true);
            return;
        }

        var previousDevice = _selectedSource?.Monitor?.DeviceName;
        var regionWasSelected = _selectedSource?.IsRegion == true;

        // A display change can give the region's monitor a new handle, position
        // or DPI; recording with the stale one fails. Rebind it to the fresh
        // monitor, or drop it if that monitor is gone or changed size.
        if (_regionTarget is not null)
        {
            var fresh = monitors.FirstOrDefault(m => m.DeviceName == _regionTarget.Monitor.DeviceName);
            _regionTarget = fresh is not null
                            && fresh.Width == _regionTarget.Monitor.Width
                            && fresh.Height == _regionTarget.Monitor.Height
                ? _regionTarget with { Monitor = fresh }
                : null;
        }

        Sources.Clear();

        if (monitors.Count == 1)
        {
            var m = monitors[0];
            Sources.Add(new SourceItem(false, m, "Full screen", $"{m.Width} × {m.Height}"));
        }
        else
        {
            foreach (var m in monitors)
                Sources.Add(new SourceItem(false, m, $"Display {m.Number}", $"{m.Width} × {m.Height}"));
        }
        Sources.Add(new SourceItem(true, null, "Region",
            _regionTarget is null ? "Select…" :
            $"{_regionTarget.Width} × {_regionTarget.Height} on Display {_regionTarget.Monitor.Number}"));

        // Restore selection: the region if it still exists (silently switching to
        // full screen would record more than the user chose), else the previous
        // monitor, else last-used, else primary.
        SourceItem? pick = null;
        if (regionWasSelected && _regionTarget is not null)
            pick = Sources[^1];
        if (previousDevice is not null)
            pick ??= Sources.FirstOrDefault(s => s.Monitor?.DeviceName == previousDevice);
        pick ??= Sources.FirstOrDefault(s =>
            s.Monitor?.DeviceName == _settings.LastMonitorDeviceName);
        pick ??= Sources.FirstOrDefault(s => s.Monitor?.IsPrimary == true) ?? Sources[0];

        _selectedSource = pick;
        OnPropertyChanged(nameof(SelectedSource));
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (_selectedSource?.IsRegion == true)
        {
            CurrentTarget = _regionTarget;
        }
        else if (_selectedSource?.Monitor is not null)
        {
            CurrentTarget = CaptureTarget.FullScreen(_selectedSource.Monitor);
            _settings.LastMonitorDeviceName = _selectedSource.Monitor.DeviceName;
            SettingsStore.Save(_settings);
        }
        else
        {
            CurrentTarget = null;
        }
        TargetChanged?.Invoke(CurrentTarget);
        RefreshEstimate();
    }

    private void RefreshEstimate() => StorageEstimate = ComputeEstimate();

    private string ComputeEstimate()
    {
        if (CurrentTarget is null || string.IsNullOrWhiteSpace(SaveFolder))
            return string.Empty;
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(SaveFolder));
            if (string.IsNullOrEmpty(root))
                return string.Empty;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return string.Empty; // unknown drive: hide the estimate
        }
        var (outW, outH) = EncodingMath.FitInside(CurrentTarget.Width, CurrentTarget.Height);
        var audio = SystemAudioOn || MicOn;
        var duration = EncodingMath.EstimateMaxDuration(free, outW, outH, Fps, audio, Quality);
        if (duration <= TimeSpan.Zero)
            return "⚠ Save drive is full";
        var videoBitrate = EncodingMath.ApplyQuality(
            EncodingMath.ComputeBitrate(outW, outH, Fps), Quality);
        var mbps = (videoBitrate + (audio ? EncodingMath.AudioBitrate : 0)) / 1_000_000.0;
        return $"{EncodingMath.FormatEstimate(duration)} of recording · {outW}×{outH}, {Fps} fps, {mbps:F1} Mbps";
    }

    // ---- microphones ----

    private void RefreshMics()
    {
        var mics = _audio.GetMicrophones();
        Microphones.Clear();
        foreach (var mic in mics)
            Microphones.Add(mic);

        var state = MicUiState.Compute(mics, _settings.MicrophoneId, MicOn);
        MicChipEnabled = state.ChipEnabled;
        MicChipTooltip = state.ChipTooltip;
        MicDisplayName = state.DisplayName ?? string.Empty;
        _selectedMicrophone = mics.FirstOrDefault(m => m.Id == state.SelectedId);
        OnPropertyChanged(nameof(SelectedMicrophone));
        if (state.SelectedId != _settings.MicrophoneId)
        {
            _settings.MicrophoneId = state.SelectedId;
            SettingsStore.Save(_settings);
        }
        if (!state.ChipEnabled && MicOn)
        {
            _micOn = false; // the last microphone vanished; silently turn the chip off
            OnPropertyChanged(nameof(MicOn));
            _settings.Microphone = false;
            SettingsStore.Save(_settings);
            RefreshEstimate();
        }
        RefreshMicVisibility();
    }

    private void RefreshMicVisibility()
    {
        MicPickerVisible = MicOn && Microphones.Count > 1;
        MicNameVisible = MicOn && Microphones.Count == 1;
    }

    // ---- recording ----

    private async Task RecordAsync()
    {
        if (IsBusy || !RecordEnabled)
            return;
        if (CurrentTarget is null)
        {
            ShowStatus("Choose what to record first.", isError: true);
            return;
        }
        HideStatus();
        var options = new RecordOptions
        {
            Fps = Fps,
            Quality = Quality,
            ShowCursor = ShowCursor,
            SystemAudio = SystemAudioOn,
            Microphone = MicOn,
            MicDeviceId = SelectedMicrophone?.Id,
            SaveFolder = SaveFolder,
        };
        // Busy until RecordingStarted arrives (or the start fails): starting takes
        // a second or two, and a second click or a settings change in that time
        // would hit a recorder that's half set up.
        IsStarting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            _startTask = Recorder.StartAsync(CurrentTarget, options);
            await _startTask;
        }
        catch (RecorderException ex)
        {
            IsStarting = false;
            Log.Error("Record failed: " + ex.Message);
            if (ex.IsCaptureUnsupported)
            {
                RecordEnabled = false;
                ShowStatus(ex.Message + " Recording is disabled.", isError: true);
            }
            else
            {
                ShowStatus(ex.Message, isError: true, showPrivacyButton: ex.IsPrivacyError);
            }
        }
        catch (Exception ex)
        {
            IsStarting = false;
            Log.Error("Record failed unexpectedly: " + ex);
            ShowStatus("Could not start recording: " + ex.Message, isError: true);
        }
    }

    private void PauseOrResume()
    {
        if (!IsRecording)
            return;
        try
        {
            if (Recorder.State == RecorderState.Paused)
            {
                Recorder.Resume();
                IsPaused = false;
            }
            else
            {
                Recorder.Pause();
                IsPaused = true;
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn("Pause/resume rejected: " + ex.Message);
        }
    }

    private void ToggleMicMute()
    {
        if (!IsRecording || !RecordingMicOn)
            return;
        Recorder.SetMicrophoneMuted(!MicMuted);
        MicMuted = Recorder.MicrophoneMuted;
    }

    private async Task StopAsync()
    {
        if (!IsRecording)
            return;
        await Recorder.StopAsync(); // outcome arrives via RecordingStopped/Failed
    }

    private void OnRecordingStarted()
    {
        IsRecording = true;
        IsStarting = false;
        IsPaused = false;
        RecordingSystemOn = SystemAudioOn;
        RecordingMicOn = MicOn;
        MicMuted = false;
        RecordingUiStarted?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnFinalizingStarted(TimeSpan duration)
    {
        IsRecording = false;
        IsPaused = false;
        HideStatus();
        _savingDuration = duration;
        _saveWatch.Restart();
        ApplySaveProgress(new SaveProgress(SaveStage.Encoding, 0, 0));
        IsSaving = true;
        SavingUiStarted?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnSaveProgress(SaveProgress progress)
    {
        if (IsSaving)
            ApplySaveProgress(progress);
    }

    private void ApplySaveProgress(SaveProgress progress)
    {
        SaveProgressValue = progress.Fraction * 100; // monotonic per save (tracker)
        SaveStageText = progress.Stage switch
        {
            SaveStage.Encoding => "Encoding the last frames and audio…",
            SaveStage.WritingFile => "Writing the video file to disk…",
            _ => "Finishing up…",
        };
        SaveDetailsText =
            $"Length {RecordingBar.FormatElapsed(_savingDuration)} · " +
            $"{SaveProgressTracker.FormatSize(progress.BytesWritten)} · " +
            $"saving for {RecordingBar.FormatElapsed(_saveWatch.Elapsed)}";
    }

    private void EndSaving()
    {
        _saveWatch.Stop();
        IsSaving = false;
    }

    private void OnRecordingStopped(RecordResult result)
    {
        IsRecording = false;
        IsPaused = false;
        EndSaving();
        SavedPath = result.FilePath;
        OnPropertyChanged(nameof(SavedFileName));
        var message = $"Saved {SavedFileName}";
        if (result.Notices.Count > 0)
            message += " — " + string.Join(" ", result.Notices);
        ShowStatus(message, isError: false);
        RecordingUiStopped?.Invoke();
        RefreshEstimate(); // the new file shrank the free space
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnRecordingFailed(string message)
    {
        IsRecording = false;
        IsPaused = false;
        EndSaving();
        ShowStatus(message, isError: true);
        RecordingUiStopped?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- misc UI ----

    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save recordings",
            InitialDirectory = SaveFolder,
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            SaveFolder = dialog.FolderName;
            _settings.SaveFolder = SaveFolder;
            SettingsStore.Save(_settings);
            RefreshEstimate();
        }
    }

    private void OpenFile()
    {
        if (SavedPath is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(SavedPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the file: " + ex.Message, isError: true);
        }
    }

    private void ShowInFolder()
    {
        if (SavedPath is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SavedPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the folder: " + ex.Message, isError: true);
        }
    }

    private void OpenMicSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open Settings: " + ex.Message, isError: true);
        }
    }

    private void ShowStatus(string message, bool isError, bool showPrivacyButton = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
        ShowPrivacyButton = showPrivacyButton;
        if (!isError)
            ShowPrivacyButton = false;
        StatusVisible = true;
    }

    private void HideStatus()
    {
        StatusVisible = false;
        SavedPath = null;
        ShowPrivacyButton = false;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
