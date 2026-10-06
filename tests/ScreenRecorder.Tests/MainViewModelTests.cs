using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Infrastructure;
using ScreenRecorder.Recording;
using ScreenRecorder.UI;

namespace ScreenRecorder.Tests;

/// <summary>Focused behavior tests for MainViewModel audio options.</summary>
public sealed class MainViewModelTests : IDisposable
{
    private readonly string? _settingsBackup;
    private readonly bool _settingsExisted;
    private MonitorService? _monitors;
    private AudioDeviceService? _audio;
    private Recorder? _recorder;

    public MainViewModelTests()
    {
        // The VM persists audio choices; never let a test clobber real settings.
        _settingsExisted = File.Exists(SettingsStore.Path);
        _settingsBackup = _settingsExisted ? File.ReadAllText(SettingsStore.Path) : null;
    }

    public void Dispose()
    {
        _recorder?.Dispose();
        _audio?.Dispose();
        _monitors?.Dispose();
        try
        {
            if (_settingsExisted)
                File.WriteAllText(SettingsStore.Path, _settingsBackup!);
            else if (File.Exists(SettingsStore.Path))
                File.Delete(SettingsStore.Path);
        }
        catch { /* best effort */ }
    }

    private MainViewModel CreateVm()
    {
        // Requires a real desktop + audio subsystem (these are desktop-app tests).
        _monitors = new MonitorService();
        _audio = new AudioDeviceService();
        _recorder = new Recorder(_audio);
        return new MainViewModel(_monitors, _audio, _recorder, new AppSettings());
    }

    [Fact]
    public void NoAudioCommand_TurnsBothSourcesOff()
    {
        var vm = CreateVm();
        vm.SystemAudioOn = true;
        if (vm.Microphones.Count > 0)
            vm.MicOn = true;

        Assert.True(vm.NoAudioCommand.CanExecute(null));
        vm.NoAudioCommand.Execute(null);

        Assert.False(vm.SystemAudioOn);
        Assert.False(vm.MicOn);
        Assert.True(vm.NoAudioCommand.CanExecute(null));
    }

    [Fact]
    public void NoAudioCommand_StaysEnabledWhenAlreadySilent()
    {
        var vm = CreateVm();
        vm.SystemAudioOn = false;
        vm.MicOn = false;

        Assert.True(vm.NoAudioCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleMicMuteCommand_DisabledWhenIdle()
    {
        var vm = CreateVm();

        Assert.False(vm.ToggleMicMuteCommand.CanExecute(null));
        vm.ToggleMicMuteCommand.Execute(null);

        Assert.False(vm.MicMuted);
    }
}
