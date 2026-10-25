using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioHub.Platform.Windows;

/// <summary>
/// Watches Windows for changes to the default multimedia render endpoint and raises an event
/// when it moves.
/// </summary>
/// <remarks>
/// <para>
/// A WASAPI sink binds to a specific endpoint at open time. When Windows silently moves the
/// default device — a Bluetooth headset connecting, HDMI speakers appearing, a cable being
/// unplugged — the sink keeps rendering into the endpoint it was opened with and either goes
/// silent or keeps playing where the user no longer expects. Reopening on notification is the
/// fix; this class is the trigger for that.
/// </para>
/// <para>
/// The callback arrives on a COM MTA thread, so handlers must not block and must marshal any UI
/// work themselves.
/// </para>
/// </remarks>
public sealed class DefaultDeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;

    public DefaultDeviceWatcher()
    {
        _enumerator = new MMDeviceEnumerator();
        _client = new NotificationClient(this);

        // The enumerator holds the callback for the lifetime of the registration, so the client
        // and the enumerator must stay alive together; Dispose unregisters before releasing them.
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    /// <summary>Raised when the default multimedia render endpoint changes.</summary>
    public event EventHandler? DefaultRenderDeviceChanged;

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Only the multimedia render role drives playback. The console/communications default
        // moving (e.g. a VoIP call switching to a headset) must not reopen the audio graph.
        if (flow != DataFlow.Render || role != Role.Multimedia)
        {
            return;
        }

        DefaultRenderDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        _enumerator.Dispose();
    }

    /// <summary>Managed implementor of the COM callback interface.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly DefaultDeviceWatcher _owner;

        public NotificationClient(DefaultDeviceWatcher owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            => _owner.OnDefaultDeviceChanged(flow, role, defaultDeviceId);

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
