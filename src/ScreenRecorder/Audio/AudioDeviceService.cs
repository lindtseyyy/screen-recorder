using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.Audio;

/// <summary>A microphone (capture endpoint): its Windows endpoint ID + friendly name.</summary>
public sealed record MicDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Pure microphone UI state (PLAN §5.1): 0 mics → chip disabled; 1 → name shown,
/// no picker; 2+ → picker shown only while the mic chip is on. The saved mic is
/// remembered by endpoint ID with fallback to the Windows default.
/// </summary>
public sealed record MicUiState(
    bool ChipEnabled,
    string ChipTooltip,
    bool ShowPicker,
    bool ShowNameOnly,
    string? DisplayName,
    string? SelectedId)
{
    public static MicUiState Compute(IReadOnlyList<MicDevice> mics, string? savedId, bool micOn)
    {
        if (mics.Count == 0)
            return new MicUiState(false, "No microphone found", false, false, null, null);

        string? selected = null;
        if (savedId is not null && mics.Any(m => m.Id == savedId))
            selected = savedId;
        else
            selected = mics.FirstOrDefault(m => m.IsDefault)?.Id ?? mics[0].Id;

        var name = mics.First(m => m.Id == selected).Name;
        return mics.Count switch
        {
            1 => new MicUiState(true, name, false, true, name, selected),
            _ => new MicUiState(true, "Choose microphone", micOn, false, name, selected),
        };
    }
}

/// <summary>
/// Lists microphones and the default output device, and notifies on plug/unplug
/// and default-device changes (PLAN §5.2). Events are marshaled to the thread
/// that created the service (the UI thread).
/// </summary>
public sealed class AudioDeviceService : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SynchronizationContext? _sync;

    public AudioDeviceService()
    {
        _sync = SynchronizationContext.Current;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Raised when capture devices change (list UI should refresh).</summary>
    public event Action? CaptureDevicesChanged;

    /// <summary>Raised when the default *output* device changes (loopback must reopen).</summary>
    public event Action? DefaultRenderDeviceChanged;

    public IReadOnlyList<MicDevice> GetMicrophones()
    {
        var result = new List<MicDevice>();
        string? defaultId = null;
        try
        {
            defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)?.ID;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not get default microphone: " + ex.Message);
        }
        try
        {
            foreach (var endpoint in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    result.Add(new MicDevice(endpoint.ID, endpoint.FriendlyName, endpoint.ID == defaultId));
                }
                catch (Exception ex)
                {
                    // One unreadable endpoint mustn't hide the rest of the list.
                    Log.Warn("Skipping microphone that could not be read: " + ex.Message);
                }
                finally
                {
                    endpoint.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate microphones: " + ex.Message);
        }
        return result;
    }

    public MMDevice? GetMicDevice(string? id)
    {
        try
        {
            if (id is not null)
            {
                foreach (var endpoint in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (endpoint.ID == id)
                        return endpoint;
                    endpoint.Dispose();
                }
            }
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open microphone device: " + ex.Message);
            return null;
        }
    }

    public MMDevice? GetDefaultRenderDevice()
    {
        try
        {
            // Multimedia: the endpoint NAudio's loopback capture opens.
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not get default output device: " + ex.Message);
            return null;
        }
    }

    // IMMNotificationClient (called on a system thread; marshal to the UI thread).

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => RaiseCaptureChanged();
    public void OnDeviceAdded(string deviceId) => RaiseCaptureChanged();
    public void OnDeviceRemoved(string deviceId) => RaiseCaptureChanged();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void OnDefaultDeviceChanged(DataFlow dataFlow, Role deviceRole, string defaultDeviceId)
    {
        // Windows reports one change per role (console, multimedia, communications).
        // Reopening loopback for each would cut the system sound three times.
        if (dataFlow == DataFlow.Render)
        {
            if (deviceRole == Role.Multimedia)
                Raise(DefaultRenderDeviceChanged);
        }
        else
        {
            RaiseCaptureChanged();
        }
    }

    private void RaiseCaptureChanged() => Raise(CaptureDevicesChanged);

    private void Raise(Action? handler)
    {
        if (handler is null)
            return;
        if (_sync is not null)
            _sync.Post(_ => handler(), null);
        else
            handler();
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* best effort */ }
        _enumerator.Dispose();
    }
}
