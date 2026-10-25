using AudioHub.Core;
using Xunit;

namespace AudioHub.Tests;

/// <summary>
/// Tests the seam that turns any transport into an audio source.
/// </summary>
/// <remarks>
/// Using a hand-rolled transport rather than the VBAN one keeps these tests about the
/// adapter itself. Whatever replaces VBAN later inherits the same behaviour for free.
/// </remarks>
public sealed class TransportAudioSourceTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public async Task StartingTheSourceStartsTheUnderlyingTransportOnce()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        await source.StartAsync();

        Assert.Equal(1, transport.StartCount);
        Assert.Equal(SourceState.Connecting, source.State);
    }

    [Fact]
    public async Task StartingTwiceDoesNotRestartTheTransport()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        await source.StartAsync();
        await source.StartAsync();

        Assert.Equal(1, transport.StartCount);
    }

    [Fact]
    public async Task PacketsAreForwardedUnchanged()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        List<float[]> captured = [];
        source.FrameReceived += (_, frame) => captured.Add(frame.Samples.ToArray());
        await source.StartAsync();

        float[] payload = [0.1f, -0.2f, 0.3f, -0.4f];
        using (AudioFrame frame = AudioFrame.Rent(Stereo48k, payload.Length))
        {
            payload.CopyTo(frame.WritableSamples);
            transport.RaisePacket(frame);
        }

        Assert.Single(captured);
        Assert.Equal(payload, captured[0]);
    }

    [Fact]
    public async Task ConnectionChangesAreMirroredOntoSourceState()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        List<SourceState> transitions = [];
        source.StateChanged += (_, e) => transitions.Add(e.Current);

        await source.StartAsync();
        transport.RaiseState(connected: true);
        transport.RaiseState(connected: false);

        Assert.Contains(SourceState.Streaming, transitions);
        Assert.Contains(SourceState.Connecting, transitions);
        Assert.Equal(SourceState.Connecting, source.State);
    }

    [Fact]
    public async Task StoppingUnsubscribesSoLatePacketsAreIgnored()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        int callbacks = 0;
        source.FrameReceived += (_, _) => callbacks++;

        await source.StartAsync();
        transport.RaisePacket(Frame());
        await source.StopAsync();
        transport.RaisePacket(Frame());

        Assert.Equal(1, callbacks);
        Assert.Equal(1, transport.StopCount);
        Assert.Equal(SourceState.Stopped, source.State);
    }

    [Fact]
    public async Task IdentifiersFallBackToTheTransportId()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(transport, Stereo48k);

        Assert.Equal(transport.Id, source.Id);
        Assert.Equal(transport.Id, source.DisplayName);
        Assert.Equal(SourceKind.Network, source.Kind);
        Assert.Equal(Stereo48k, source.Format);
    }

    [Fact]
    public async Task IdentifiersAndKindCanBeOverridden()
    {
        FakeTransport transport = new();
        await using TransportAudioSource source = new(
            transport,
            Stereo48k,
            id: "android",
            displayName: "Android phone",
            kind: SourceKind.Bluetooth);

        Assert.Equal("android", source.Id);
        Assert.Equal("Android phone", source.DisplayName);
        Assert.Equal(SourceKind.Bluetooth, source.Kind);
    }

    private static AudioFrame Frame() => AudioFrame.Rent(Stereo48k, 4);

    private sealed class FakeTransport : ITransport
    {
        public string Id => "fake";

        public bool IsConnected { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public event EventHandler<AudioFrame>? PacketReceived;

        public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

        public void RaisePacket(AudioFrame frame) => PacketReceived?.Invoke(this, frame);

        public void RaiseState(bool connected, string? message = null)
        {
            IsConnected = connected;
            StateChanged?.Invoke(this, new TransportStateChangedEventArgs(Id, connected, message));
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
