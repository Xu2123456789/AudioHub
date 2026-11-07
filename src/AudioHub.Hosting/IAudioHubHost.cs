using AudioHub.Core;
using AudioHub.Core.Discovery;
using AudioHub.Platform.Windows;
using AudioHub.Sources.Vban;

namespace AudioHub.Hosting;

/// <summary>
/// The running audio graph, as seen by a user interface.
/// </summary>
/// <remarks>
/// Every front end — tray, web console, command line — talks to this interface and
/// nothing else. That is what keeps the WPF app, the web API and the CLI from each
/// growing their own copy of the wiring logic.
/// </remarks>
public interface IAudioHubHost : IAsyncDisposable
{
    /// <summary>The mixer, for per-channel gain, muting and level metering.</summary>
    IMixerEngine Mixer { get; }

    /// <summary>Whether the graph is running.</summary>
    bool IsRunning { get; }

    /// <summary>Playback endpoints currently available. Bluetooth headsets appear here.</summary>
    IReadOnlyList<AudioEndpointInfo> OutputDevices { get; }

    /// <summary>Endpoint currently being rendered to. Null means the system default.</summary>
    string? OutputDeviceId { get; }

    /// <summary>
    /// Endpoint the local loopback tap is reading, or null when local capture is off or was
    /// skipped. Tapping the same endpoint that is being rendered to feeds the mixer back into
    /// itself, so a host that refuses to do that is not being awkward.
    /// </summary>
    string? LocalCaptureDeviceId { get; }

    /// <summary>Why the local tap is not running, or null when it is.</summary>
    string? LocalCaptureSkippedReason { get; }

    /// <summary>
    /// Live counters for the incoming network stream, or null when no transport is running.
    /// </summary>
    /// <remarks>
    /// On the interface rather than left on the concrete host so that every front end can tell
    /// "the phone is sending" apart from "nothing is arriving". Without it a tray window that
    /// has received no packets looks exactly like one that has not started yet.
    /// </remarks>
    VbanTransportStats? TransportStats { get; }

    /// <summary>This hub's stable identity, which a phone recognises it by across reconnects.</summary>
    HubIdentity Identity { get; }

    /// <summary>
    /// The key a phone types once to send encrypted audio, or null when encryption is off.
    /// </summary>
    string? PairingKey { get; }

    /// <summary>How many arriving packets were unwrapped, or null when encryption is off.</summary>
    long? PacketsDecrypted { get; }

    /// <summary>What this hub demands of senders.</summary>
    SecurityMode SecurityMode { get; }

    /// <summary>
    /// Changes <see cref="SecurityMode"/> while the graph is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A setter rather than a constructor option because the setting is a switch in the window, and
    /// "changing it needs a restart" is not something a user should have to know. The earlier
    /// version only read it at startup, so the window showed the new value while the running hub
    /// kept enforcing the old one — the worst kind of disagreement, because both halves look right
    /// on their own.
    /// </para>
    /// <para>
    /// Implementations must update <i>all</i> consumers together: what discovery advertises, what
    /// the transport accepts, and whether a pairing key is still shown. Updating only some of them
    /// is how a phone ends up sealing packets at a hub that will not open them.
    /// </para>
    /// </remarks>
    void SetSecurityMode(SecurityMode mode);

    /// <summary>
    /// Discovery diagnostics, or null when discovery is off.
    /// </summary>
    /// <remarks>
    /// On the interface because whether anybody can find this hub is invisible otherwise, and a
    /// discovery service that silently failed looks exactly like a phone app that is broken.
    /// </remarks>
    IDiscoveryTransport? Discovery { get; }

    /// <summary>Starts capture, mixing and rendering.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops everything and releases devices.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves output to another endpoint. Reopens the render device, leaving capture and
    /// the mixer untouched so the switch is as quick as the device allows.
    /// </summary>
    Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);

    /// <summary>Refreshes <see cref="OutputDevices"/>, e.g. after a headset is switched on.</summary>
    void RefreshOutputDevices();
}
