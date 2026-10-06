using ScreenRecorder.Audio;

namespace ScreenRecorder.Tests;

public sealed class MicUiStateTests
{
    private static MicDevice Mic(string id, string name, bool isDefault = false) => new(id, name, isDefault);

    [Fact]
    public void NoMicrophones_ChipDisabled()
    {
        var state = MicUiState.Compute([], null, micOn: false);

        Assert.False(state.ChipEnabled);
        Assert.Equal("No microphone found", state.ChipTooltip);
        Assert.False(state.ShowPicker);
        Assert.False(state.ShowNameOnly);
        Assert.Null(state.SelectedId);
    }

    [Fact]
    public void OneMicrophone_NameShown_NoPicker()
    {
        var mics = new[] { Mic("id1", "Microphone (Realtek Audio)", isDefault: true) };

        var state = MicUiState.Compute(mics, null, micOn: true);

        Assert.True(state.ChipEnabled);
        Assert.False(state.ShowPicker);
        Assert.True(state.ShowNameOnly);
        Assert.Equal("Microphone (Realtek Audio)", state.DisplayName);
        Assert.Equal("id1", state.SelectedId);
    }

    [Fact]
    public void ThreeMicrophones_PickerShownOnlyWhileMicOn()
    {
        var mics = new[]
        {
            Mic("usb", "Microphone (USB Audio)", isDefault: true),
            Mic("builtin", "Microphone Array (Realtek)"),
            Mic("bt", "Headset (Bluetooth)"),
        };

        var on = MicUiState.Compute(mics, null, micOn: true);
        Assert.True(on.ShowPicker);
        Assert.False(on.ShowNameOnly);
        Assert.Equal("usb", on.SelectedId); // Windows default wins with no saved choice

        var off = MicUiState.Compute(mics, null, micOn: false);
        Assert.False(off.ShowPicker);
    }

    [Fact]
    public void SavedMicrophone_RememberedById()
    {
        var mics = new[]
        {
            Mic("usb", "Microphone (USB Audio)", isDefault: true),
            Mic("builtin", "Microphone Array (Realtek)"),
        };

        var state = MicUiState.Compute(mics, "builtin", micOn: true);

        Assert.Equal("builtin", state.SelectedId);
        Assert.Equal("Microphone Array (Realtek)", state.DisplayName);
    }

    [Fact]
    public void SavedMicrophoneMissing_FallsBackToDefault()
    {
        var mics = new[]
        {
            Mic("usb", "Microphone (USB Audio)", isDefault: true),
            Mic("builtin", "Microphone Array (Realtek)"),
        };

        var state = MicUiState.Compute(mics, "unplugged-id", micOn: true);

        Assert.Equal("usb", state.SelectedId);
    }

    [Fact]
    public void SavedMicrophoneMissing_NoDefault_FallsBackToFirst()
    {
        var mics = new[]
        {
            Mic("a", "Mic A"),
            Mic("b", "Mic B"),
        };

        var state = MicUiState.Compute(mics, "gone", micOn: true);

        Assert.Equal("a", state.SelectedId);
    }
}
