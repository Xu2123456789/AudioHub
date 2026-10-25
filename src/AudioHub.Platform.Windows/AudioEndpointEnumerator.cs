using NAudio.CoreAudioApi;

namespace AudioHub.Platform.Windows;

/// <summary>Lists WASAPI render endpoints, which is where Bluetooth headsets show up.</summary>
public static class AudioEndpointEnumerator
{
    /// <summary>Every active playback endpoint, default first.</summary>
    public static IReadOnlyList<AudioEndpointInfo> RenderDevices()
    {
        using MMDeviceEnumerator enumerator = new();

        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
        }
        catch (Exception)
        {
            // No default endpoint is a legitimate state on machines with no audio device.
        }

        List<AudioEndpointInfo> devices = new();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(new AudioEndpointInfo(device.ID, device.FriendlyName, device.ID == defaultId));
        }

        return devices
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Resolves an endpoint by id, or returns null when it is gone.</summary>
    public static MMDevice? FindRenderDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        using MMDeviceEnumerator enumerator = new();

        try
        {
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
