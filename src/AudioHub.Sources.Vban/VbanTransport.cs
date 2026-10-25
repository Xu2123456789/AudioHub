using System.Net;
using System.Net.Sockets;
using AudioHub.Core;
using AudioHub.Core.Security;

namespace AudioHub.Sources.Vban;

/// <summary>
/// Snapshot of what the transport has seen so far.
/// </summary>
/// <remarks>
/// Exposed because "is the phone actually reaching the PC" is otherwise unanswerable
/// without listening yourself. Counts let a silent failure be diagnosed remotely.
/// </remarks>
public sealed record VbanTransportStats(
    long PacketsAccepted,
    long PacketsRejected,
    long FramesLost,
    long FramesOutOfOrder,
    float LastPeakLevel,
    DateTimeOffset? LastPacketUtc,
    bool IsConnected,
    string? LastRejectionReason,
    VbanArrivalStats Arrival)
{
    /// <summary>Nothing received yet.</summary>
    public static VbanTransportStats Empty { get; } =
        new(0, 0, 0, 0, 0f, null, false, null, VbanArrivalStats.Empty);
}

/// <summary>
/// How packets actually arrived, as opposed to how they were sent.
/// </summary>
/// <remarks>
/// <para>
/// A packet count cannot tell "arrives evenly" apart from "arrives in bursts", but the two
/// are completely different in the audio. A Wi-Fi client in power save is told by its access
/// point to wait for a beacon, so a steady 187 packets per second can reach the machine as
/// roughly nineteen packets every hundred milliseconds. The receiver's dejitter buffer then
/// sees a flood followed by a drought, and an 80 ms buffer loses audio at both ends of every
/// cycle. Measuring the gaps is the only way to see that from the receiving side.
/// </para>
/// <para>
/// Gaps are bucketed at 1 ms resolution, which is far finer than anything that matters here.
/// </para>
/// </remarks>
/// <param name="NominalIntervalMs">Interval the sender is aiming for, from the packet header.</param>
/// <param name="MedianMs">Median gap between consecutive packets.</param>
/// <param name="P95Ms">95th percentile gap.</param>
/// <param name="MaxMs">Largest gap seen.</param>
/// <param name="BurstCount">How many runs of back-to-back packets were seen.</param>
/// <param name="MaxBurstPackets">Packets in the largest such run.</param>
public sealed record VbanArrivalStats(
    double NominalIntervalMs,
    double MedianMs,
    double P95Ms,
    double MaxMs,
    long BurstCount,
    int MaxBurstPackets)
{
    /// <summary>Nothing measured yet.</summary>
    public static VbanArrivalStats Empty { get; } = new(0d, 0d, 0d, 0d, 0, 0);

    /// <summary>
    /// Whether arrivals look batched rather than paced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Batching has a shape, not just a size: packets pile up back-to-back and then nothing
    /// arrives for a long time. So all three have to hold — the typical gap is much shorter
    /// than the sender's own pacing, the largest gap is much longer, and at least one run was
    /// big enough to be a release rather than scheduling noise.
    /// </para>
    /// <para>
    /// Sizing alone is not enough, and this is the second version of the test for that reason.
    /// The first counted a "burst" as everything between two long gaps, so on a perfectly even
    /// stream one stray gap slightly over the threshold split a hundred seconds of packets into
    /// a single run of thousands and reported batching where there was none. Sending someone to
    /// add latency on the strength of that is worse than saying nothing, hence the shape test.
    /// </para>
    /// <para>
    /// Do not add a "we have measured something" guard on <see cref="MedianMs"/>. The second
    /// version did — <c>MedianMs &gt; 0</c> — and it silenced the detector on exactly the traffic
    /// it was written for: a real phone's first measurement came back with
    /// <c>p50=0.0 / p95=23.0 / max=191.8 ms / 最大 28 包/批</c>, a textbook batch, and the median
    /// of a genuine burst is zero because the gaps are bucketed to whole milliseconds. The
    /// "anything measured" question is already answered by <see cref="MaxMs"/> being non-zero.
    /// </para>
    /// </remarks>
    public bool LooksBatched =>
        NominalIntervalMs > 0 &&
        MaxBurstPackets >= 6 &&
        MedianMs <= NominalIntervalMs * 0.5 &&
        MaxMs >= NominalIntervalMs * 3;
}

/// <summary>
/// Receives native PCM VBAN audio over UDP and publishes it as frames.
/// </summary>
/// <remarks>
/// <para>
/// VBAN is deliberately dumb, and that is why it is worth using here: one datagram carries
/// one small block of PCM and a frame counter. There is no clock recovery, no session, and
/// no handshake, so there is nothing to negotiate and nothing to get stuck waiting on. The
/// specification states plainly that every sender is master and the receiver follows, which
/// suits a pull-free arrival stream feeding a dejitter buffer.
/// </para>
/// <para>
/// Packets arrive on their own thread. Two rules are inherited from the interface contract:
/// handlers must not keep the frame beyond the callback, and this class owns the frame it
/// publishes, so it disposes once every subscriber has returned.
/// </para>
/// </remarks>
public sealed class VbanTransport : ITransport
{
    /// <summary>The port Voicemeeter and most VBAN senders use by default.</summary>
    public const int DefaultPort = 6980;

    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _port;
    private readonly string? _streamName;
    private readonly AudioFormat? _expectedFormat;
    private readonly object _gate = new();

    private Socket? _socket;
    private Task? _listener;
    private Task? _watchdog;
    private CancellationTokenSource? _stopping;

    private long _packetsAccepted;
    private long _packetsRejected;
    private long _packetsDecrypted;
    private readonly ISecureEnvelope? _envelope;
    private readonly bool _requireEncryption;

    /// <summary>
    /// Largest datagram this transport will accept: a full VBAN packet plus whatever the envelope
    /// adds on top of it.
    /// </summary>
    /// <remarks>
    /// <see cref="VbanPacketHeader.MaxPacketBytes"/> is the ceiling for a <i>plain</i> VBAN packet,
    /// and using it as the receive size was wrong the moment encryption arrived: sealing one adds
    /// bytes, so a packet at or near the ceiling arrives longer than the buffer waiting for it.
    /// A datagram that does not fit is not clipped — on Windows the surplus is discarded and the
    /// receive fails outright (WSAEMSGSIZE, native 10040) — so the whole packet is lost, not just
    /// its tail. It is expressed as a sum rather than a constant so the two numbers cannot drift
    /// apart again when the envelope changes.
    /// </remarks>
    private readonly int _maxDatagramBytes;
    private long _framesLost;
    private long _framesOutOfOrder;
    private float _lastPeakLevel;
    private long _lastPacketTicks;
    private uint _lastFrameCounter;
    private bool _seenAnyFrame;
    private string? _lastRejectionReason;
    private volatile bool _isConnected;
    private bool _disposed;

    // Arrival-gap measurement. Written only on the listener thread; read from the UI thread
    // for diagnostics, where a torn value at worst misreports one percentile by a millisecond.
    private readonly int[] _gapBuckets = new int[GapBucketCount];
    private long _lastArrivalTicks;
    private long _gapSamples;
    private double _nominalIntervalMs;
    private double _gapMaxMs;
    private double _gapMedianMs;
    private double _gapP95Ms;
    private long _burstCount;
    private int _burstSize;
    private int _maxBurstPackets;

    /// <summary>1 ms buckets from 0 ms to <see cref="GapBucketCount"/> − 1 ms, plus a clamp bucket.</summary>
    private const int GapBucketCount = 1_025;

    /// <summary>Creates a listening transport.</summary>
    /// <param name="port">UDP port to bind. The VBAN default is 6980.</param>
    /// <param name="streamName">
    /// Only accept packets advertising this stream name. Null accepts any stream, which is
    /// convenient when you have not decided how the sender will be configured yet.
    /// </param>
    /// <param name="id">Stable transport id. Defaults to a fixed value.</param>
    /// <param name="expectedFormat">
    /// When set, packets at any other sample rate or channel count are refused. The mixer
    /// channel is created with one fixed format, so letting a differently formatted stream
    /// through would not be resampled — it would play at the wrong speed. Refusing loudly
    /// beats silent pitch and tempo errors. Null accepts whatever arrives.
    /// </param>
    /// <param name="envelope">
    /// Optional packet protection, sitting between the socket and the VBAN parser. Null leaves
    /// packets alone. See <see cref="ISecureEnvelope"/>: the point of taking it here is that
    /// nothing else in the pipeline has to know about it.
    /// </param>
    /// <param name="requireEncryption">
    /// When true, a packet that does not authenticate is refused instead of being tried as plain
    /// VBAN. False is the transitional setting, which accepts both so a phone that has not been
    /// paired yet keeps working.
    /// </param>
    public VbanTransport(
        int port = DefaultPort,
        string? streamName = null,
        string? id = null,
        AudioFormat? expectedFormat = null,
        ISecureEnvelope? envelope = null,
        bool requireEncryption = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        _port = port;
        _streamName = streamName;
        _expectedFormat = expectedFormat;
        _envelope = envelope;
        _requireEncryption = requireEncryption;
        _maxDatagramBytes = VbanPacketHeader.MaxPacketBytes + (envelope?.OverheadBytes ?? 0);
        Id = string.IsNullOrWhiteSpace(id) ? "vban" : id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <summary>
    /// How many packets were unwrapped successfully. Zero while everything arriving is plain,
    /// which is exactly the number to watch when wondering whether encryption is actually engaged.
    /// </summary>
    public long PacketsDecrypted => Interlocked.Read(ref _packetsDecrypted);

    /// <summary>Counters and diagnostics as of now.</summary>
    public VbanTransportStats Stats => new(
        Interlocked.Read(ref _packetsAccepted),
        Interlocked.Read(ref _packetsRejected),
        Interlocked.Read(ref _framesLost),
        Interlocked.Read(ref _framesOutOfOrder),
        _lastPeakLevel,
        LastPacketUtc,
        _isConnected,
        _lastRejectionReason,
        new VbanArrivalStats(
            _nominalIntervalMs,
            _gapMedianMs,
            _gapP95Ms,
            _gapMaxMs,
            Interlocked.Read(ref _burstCount),
            _maxBurstPackets));

    /// <summary>When the last accepted packet arrived, or null if none has.</summary>
    private DateTimeOffset? LastPacketUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastPacketTicks);
            return ticks == 0 ? null : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
    }

    /// <inheritdoc />
    public event EventHandler<AudioFrame>? PacketReceived;

    /// <inheritdoc />
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_listener is not null)
            {
                return Task.CompletedTask;
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _socket = socket;

            _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _stopping.Token;
            _listener = Task.Run(() => ListenAsync(token), token);
            _watchdog = Task.Run(() => WatchAsync(token), token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? listener;
        Task? watchdog;

        lock (_gate)
        {
            listener = _listener;
            watchdog = _watchdog;
            _listener = null;
            _watchdog = null;

            _stopping?.Cancel();
            _socket?.Dispose();
            _socket = null;
        }

        if (listener is null && watchdog is null)
        {
            return;
        }

        try
        {
            if (listener is not null)
            {
                await listener.ConfigureAwait(false);
            }

            if (watchdog is not null)
            {
                await watchdog.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling is how the loop is told to stop.
        }
        catch (SocketException)
        {
            // Expected: disposing the socket aborts the pending receive.
        }
        finally
        {
            CleanupStoppingSource();
        }

        SetConnected(false, "Transport stopped.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _stopping?.Dispose();
            _stopping = null;
            _disposed = true;
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        Socket socket = _socket!;
        byte[] buffer = new byte[_maxDatagramBytes];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            int received;

            try
            {
                SocketReceiveFromResult result =
                    await socket.ReceiveFromAsync(new Memory<byte>(buffer), SocketFlags.None, remote, token)
                        .ConfigureAwait(false);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (IsPerDatagramError(ex.SocketErrorCode))
            {
                // A datagram the socket refused to hand over, or an ICMP error Windows surfaces
                // from an earlier exchange. Neither says anything about whether this socket can
                // keep listening, and `break` here would be the worst possible response: the
                // socket stays bound, nothing ever reads from it again, and from the outside it
                // is indistinguishable from the sender having gone quiet. One bad packet would
                // silently end the session until the app was restarted.
                RecordDatagramFailure(ex.SocketErrorCode);
                continue;
            }
            catch (SocketException)
            {
                break;
            }

            if (received > 0)
            {
                HandlePacket(buffer.AsSpan(0, received));
            }
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        // A UDP stream has no disconnect event. Silence is the only signal, so we watch for it.
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long ticks = Interlocked.Read(ref _lastPacketTicks);
            bool alive = ticks != 0 && DateTime.UtcNow.Ticks - ticks <= SilenceTimeout.Ticks;

            if (alive != _isConnected)
            {
                SetConnected(alive, alive ? null : "No VBAN packets for two seconds.");
            }
        }
    }

    private void HandlePacket(ReadOnlySpan<byte> packet)
    {
        // Unwrap before anything looks at the bytes. Doing it here rather than in the socket layer
        // keeps the protection out of every other stage: the VBAN parser below, and everything
        // after it, sees exactly what it saw before, whether or not encryption is on.
        if (_envelope is { IsEnabled: true } envelope)
        {
            if (envelope.TryOpen(packet, out ReadOnlyMemory<byte> plaintext, out string? openRejection))
            {
                Interlocked.Increment(ref _packetsDecrypted);
                packet = plaintext.Span;
            }
            else if (_requireEncryption || envelope.LooksProtected(packet))
            {
                // Refused either because encryption is mandatory, or because the packet is
                // unmistakably one of ours that would not open. The second case matters in the
                // permissive mode, which is the default: without it the packet falls through to
                // the VBAN parser and comes back as "missing the VBAN magic", pointing at the
                // network when the real cause is a key that does not match this PC's.
                Reject(openRejection);
                return;
            }

            // Otherwise it is plain VBAN and is left alone. That is the transitional setting only;
            // once phones are paired, requiring encryption is what makes this mean something.
        }

        if (!VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out string? rejection))
        {
            Reject(rejection);
            return;
        }

        if (_streamName is { Length: > 0 } expected &&
            !string.Equals(header.StreamName, expected, StringComparison.Ordinal))
        {
            Reject($"Stream '{header.StreamName}' is not the expected '{expected}'.");
            return;
        }

        if (!VbanPacketHeader.TryGetPayloadSize(header, out int expectedBytes))
        {
            Reject($"Data type {header.DataType} has no documented storage width; refusing rather than guessing.");
            return;
        }

        ReadOnlySpan<byte> payload = packet.Slice(VbanPacketHeader.Size);
        if (payload.Length != expectedBytes)
        {
            Reject($"Payload holds {payload.Length} bytes but the header describes {expectedBytes}.");
            return;
        }

        if (header.Channels is < 1 or > 2)
        {
            Reject($"Stream carries {header.Channels} channels; AudioHub handles mono and stereo only.");
            return;
        }

        if (header.SampleRate is < 8_000 or > 384_000)
        {
            Reject($"Sample rate {header.SampleRate} Hz is outside the range AudioHub accepts.");
            return;
        }

        if (_expectedFormat is { } configured &&
            (header.SampleRate != configured.SampleRate || header.Channels != configured.Channels))
        {
            Reject($"Stream is {header.SampleRate} Hz with {header.Channels} channel(s) but this source expects {configured.SampleRate} Hz with {configured.Channels}.");
            return;
        }

        VbanPacketHeader.TryGetSampleType(header.DataType, out PcmSampleType sampleType);
        TrackArrival(header);
        TrackSequence(header.FrameCounter);

        var format = new AudioFormat(header.SampleRate, header.Channels);
        using AudioFrame frame = AudioFrame.Rent(
            format,
            header.Channels * header.SampleCount,
            DateTime.UtcNow.Ticks);

        PcmConverter.ToFloat(payload, sampleType, frame.WritableSamples);

        Interlocked.Increment(ref _packetsAccepted);
        Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _lastPeakLevel, PeakOf(frame.Samples));

        if (!_isConnected)
        {
            SetConnected(true, null);
        }

        // We rented this frame, so we dispose it. Subscribers copy during the callback only.
        PacketReceived?.Invoke(this, frame);
    }

    private void TrackSequence(uint frameCounter)
    {
        if (_seenAnyFrame)
        {
            // Frame counters are unsigned and intended to wrap, which unchecked arithmetic
            // handles for us: a wrapped forward jump still reads as a small positive delta.
            uint expected = unchecked(_lastFrameCounter + 1);
            if (frameCounter != expected)
            {
                int delta = unchecked((int)(frameCounter - expected));
                if (delta > 0)
                {
                    Interlocked.Add(ref _framesLost, delta);
                }
                else
                {
                    Interlocked.Increment(ref _framesOutOfOrder);
                }
            }
        }

        _lastFrameCounter = frameCounter;
        _seenAnyFrame = true;
    }

    /// <summary>
    /// Measures the gap since the previous packet and groups packets into bursts.
    /// </summary>
    /// <remarks>
    /// The nominal interval comes from the packet itself rather than from configuration, so
    /// this works against any sender without being told what to expect.
    /// <para>
    /// A packet counts as belonging to a burst when it lands close behind the previous one —
    /// half the sender's own pacing or less. A paced stream never does that: its gaps sit at
    /// roughly the nominal interval, so it produces no bursts at all. A batching access point
    /// releases a dozen or more back-to-back and then goes quiet, which shows up as a long run
    /// of short gaps followed by a long one.
    /// </para>
    /// <para>
    /// The threshold is deliberately on the short side rather than the long side. Marking a
    /// burst by a gap longer than nominal makes every packet between two stray slow gaps into
    /// one enormous "burst", which is how the previous version called a perfectly even stream
    /// batched. Marking it by a gap shorter than nominal cannot do that.
    /// </para>
    /// </remarks>
    private void TrackArrival(VbanPacketHeader header)
    {
        if (header.SampleCount > 0 && header.SampleRate > 0)
        {
            _nominalIntervalMs = header.SampleCount * 1000.0 / header.SampleRate;
        }

        long now = DateTime.UtcNow.Ticks;
        long previous = _lastArrivalTicks;
        _lastArrivalTicks = now;

        if (previous == 0)
        {
            _burstSize = 1;
            return;
        }

        double gapMs = (now - previous) / (double)TimeSpan.TicksPerMillisecond;
        _gapSamples++;
        if (gapMs > _gapMaxMs)
        {
            _gapMaxMs = gapMs;
        }

        _gapBuckets[(int)Math.Clamp(gapMs, 0d, GapBucketCount - 1)]++;

        // A burst is a run of packets arriving far closer together than the sender spaces them.
        // The floor keeps sub-millisecond scheduling jitter from looking like a release.
        double burstGapMs = Math.Max(1.0, _nominalIntervalMs * 0.5);

        if (_nominalIntervalMs > 0 && gapMs <= burstGapMs)
        {
            _burstSize++;
        }
        else
        {
            if (_burstSize >= 2)
            {
                Interlocked.Increment(ref _burstCount);
                if (_burstSize > _maxBurstPackets)
                {
                    _maxBurstPackets = _burstSize;
                }
            }

            _burstSize = 1;
        }

        (_gapMedianMs, _gapP95Ms) = SummariseGaps();
    }

    /// <summary>Reads the median and 95th percentile back out of the gap histogram.</summary>
    private (double Median, double P95) SummariseGaps()
    {
        if (_gapSamples == 0)
        {
            return (0d, 0d);
        }

        long medianRank = (_gapSamples + 1) / 2;
        long p95Rank = (long)Math.Ceiling(_gapSamples * 0.95);
        long running = 0;
        double median = 0d;
        double p95 = 0d;
        bool medianFound = false;

        for (int bucket = 0; bucket < _gapBuckets.Length; bucket++)
        {
            int count = _gapBuckets[bucket];
            if (count == 0)
            {
                continue;
            }

            running += count;

            if (!medianFound && running >= medianRank)
            {
                median = bucket;
                medianFound = true;
            }

            if (running >= p95Rank)
            {
                p95 = bucket;
                break;
            }
        }

        return (median, p95);
    }

    /// <summary>
    /// Whether a receive error describes the packet just attempted rather than the socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a datagram socket the two are genuinely different and only one of them is fatal:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="SocketError.MessageSize"/> — the datagram was larger than the buffer. Windows
    /// discards the surplus and reports this instead of handing over a truncated packet.
    /// </description></item>
    /// <item><description>
    /// <see cref="SocketError.ConnectionReset"/> — Windows delivers this on UDP when an earlier
    /// send drew an ICMP port-unreachable. It is a well-known quirk rather than a real reset,
    /// and it is reported once, against whichever receive call happens to be pending.
    /// </description></item>
    /// <item><description>
    /// <see cref="SocketError.NetworkReset"/> and <see cref="SocketError.ConnectionAborted"/>
    /// — the same family: transient conditions attached to one exchange.
    /// </description></item>
    /// </list>
    /// <para>
    /// Everything else — a bound port being taken away, the socket closing underneath us — really
    /// does mean the loop cannot continue.
    /// </para>
    /// </remarks>
    private static bool IsPerDatagramError(SocketError error) =>
        error is SocketError.MessageSize
            or SocketError.ConnectionReset
            or SocketError.NetworkReset
            or SocketError.ConnectionAborted;

    /// <summary>
    /// Counts a datagram the socket would not hand over, and remembers why for the diagnostics.
    /// </summary>
    private void RecordDatagramFailure(SocketError error)
    {
        Interlocked.Increment(ref _packetsRejected);

        lock (_gate)
        {
            _lastRejectionReason = error == SocketError.MessageSize
                ? $"数据报超过接收缓冲（{_maxDatagramBytes} 字节），已整个丢弃"
                : $"底层 UDP 错误 {error}（只影响这一个包，接收继续）";
        }
    }

    private void Reject(string? reason)
    {
        Interlocked.Increment(ref _packetsRejected);

        lock (_gate)
        {
            _lastRejectionReason = reason;
        }
    }

    private void CleanupStoppingSource()
    {
        lock (_gate)
        {
            _stopping?.Dispose();
            _stopping = null;
        }
    }

    private void SetConnected(bool connected, string? message)
    {
        if (_isConnected == connected)
        {
            return;
        }

        _isConnected = connected;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(Id, connected, message));
    }

    private static float PeakOf(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }
}
