using System.Net;
using System.Net.Sockets;
using AudioHub.Core.Discovery;

namespace AudioHub.Hosting;

/// <summary>
/// Listens for discovery probes on UDP <see cref="DiscoveryProtocol.Port"/> and answers each one
/// with a unicast reply carrying this hub's identity.
/// </summary>
/// <remarks>
/// <para>
/// Two behaviours here are deliberate and easy to undo by accident.
/// </para>
/// <para>
/// First, the reply goes back to whoever sent the probe, unicast, rather than being broadcast.
/// A broadcast would reach every phone on the subnet, which is louder than it needs to be and
/// happens to advertise the hub to devices that never asked.
/// </para>
/// <para>
/// Second, a port that is already taken is reported rather than thrown. Two AudioHub instances
/// running at once is a mistake, but blowing up startup over it would take down audio playback
/// for somebody who just double-clicked twice. <see cref="UnavailableReason"/> carries the bad
/// news and nothing else changes.
/// </para>
/// <para>
/// Every malformed packet is dropped silently apart from being counted and described in
/// <see cref="LastRejection"/>. Anything arriving on this port is from the network, so parsing it
/// has to assume it is hostile.
/// </para>
/// </remarks>
public sealed class DiscoveryListener : IDiscoveryTransport, IDisposable
{
    private readonly HubIdentity _identity;
    private readonly int _vbanPort;
    private readonly SecurityMode _securityMode;
    private readonly string _name;
    private readonly object _gate = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cancel;
    private Task? _loop;
    private bool _disposed;

    private int _probesReceived;
    private int _announcementsSent;
    private int _rejectedPackets;
    private string? _lastRejection;

    /// <summary>Creates a listener announcing <paramref name="identity"/>.</summary>
    /// <param name="identity">This hub's stable identity.</param>
    /// <param name="vbanPort">Where senders should direct VBAN audio.</param>
    /// <param name="securityMode">What this hub expects of senders.</param>
    /// <param name="name">Human-readable name. Defaults to the machine name.</param>
    public DiscoveryListener(
        HubIdentity identity,
        int vbanPort,
        SecurityMode securityMode = SecurityMode.None,
        string? name = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _vbanPort = vbanPort;
        _securityMode = securityMode;
        _name = name ?? Environment.MachineName;
    }

    /// <inheritdoc />
    public bool IsListening => _client is not null;

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; }

    /// <summary>How many probes have been answered.</summary>
    public int ProbesReceived => Volatile.Read(ref _probesReceived);

    /// <summary>How many replies have been sent.</summary>
    public int AnnouncementsSent => Volatile.Read(ref _announcementsSent);

    /// <summary>How many packets were dropped as malformed.</summary>
    public int RejectedPackets => Volatile.Read(ref _rejectedPackets);

    /// <summary>Why the most recent packet was dropped, if any. Used by diagnostics.</summary>
    public string? LastRejection
    {
        get { lock (_gate) { return _lastRejection; } }
    }

    /// <summary>Identity being announced.</summary>
    public HubIdentity Identity => _identity;

    /// <inheritdoc />
    public void Start()
    {
        if (_client is not null)
        {
            return;
        }

        try
        {
            // Bound to any address so probes arriving on whichever interface the phone is on are
            // seen, including the subnet broadcast address.
            UdpClient client = new(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port))
            {
                EnableBroadcast = true,
            };

            _client = client;
            _cancel = new CancellationTokenSource();
            _loop = Task.Run(() => ListenAsync(_cancel.Token));
            UnavailableReason = null;
        }
        catch (SocketException ex)
        {
            // Almost always another instance already holding the port. Say so instead of crashing.
            UnavailableReason = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"UDP {DiscoveryProtocol.Port} 已被占用（多半已经有一个 AudioHub 在跑）"
                : $"无法监听 UDP {DiscoveryProtocol.Port}：{ex.SocketErrorCode}";
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        _cancel?.Cancel();

        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop only aggregates cancellation, which is expected here.
        }

        _loop = null;
        _cancel?.Dispose();
        _cancel = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        UdpClient? client = _client;
        if (client is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult received = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                Handle(received);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A bad packet from the network is not our problem and must not end the loop.
            }
        }
    }

    private void Handle(UdpReceiveResult received)
    {
        if (!DiscoveryProtocol.TryParse(received.Buffer, out DiscoveryMessage message, out string? rejection))
        {
            Interlocked.Increment(ref _rejectedPackets);
            lock (_gate)
            {
                _lastRejection = rejection;
            }

            return;
        }

        if (message.Type != DiscoveryMessageType.Probe)
        {
            // A hub announcing itself is not something we asked for. Ignore rather than count it
            // as malformed: it is well-formed, just not addressed to us.
            return;
        }

        Interlocked.Increment(ref _probesReceived);

        byte[] reply = DiscoveryProtocol.BuildAnnounce(
            message.Token,
            _identity.HubId,
            _vbanPort,
            _securityMode,
            _name);

        UdpClient? client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            client.Send(reply, reply.Length, received.RemoteEndPoint);
            Interlocked.Increment(ref _announcementsSent);
        }
        catch (SocketException)
        {
            // The requester went away between asking and receiving. Nothing to do.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down mid-reply.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
