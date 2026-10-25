using System.Net;
using System.Net.Sockets;
using System.Text;
using AudioHub.Core;
using AudioHub.Core.Security;
using AudioHub.Sources.Vban;
using Xunit;

namespace AudioHub.Tests;

/// <summary>
/// End-to-end tests for the VBAN receive path that need no Android device.
/// </summary>
/// <remarks>
/// Each test runs a sender and the transport on the loopback interface, so this really does
/// exercise UDP reception, header validation, conversion and frame publication. What it
/// cannot prove is that a particular phone app emits well-formed packets; that stays a
/// manual check, and the version of it for this setup is the
/// <c>--probe-vban</c> command, which prints the same counters against a real sender.
/// </remarks>
public sealed class VbanTransportTests
{
    private const int SampleCount = 128;

    [Fact]
    public async Task AStereoSixteenBitPacketArrivesAsFloatFrames()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        var received = new List<float[]>();
        transport.PacketReceived += (_, frame) => received.Add(frame.Samples.ToArray());

        await transport.StartAsync();
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16);

        await WaitUntilAsync(() => received.Count == 1);

        // 128 frames of stereo, interleaved.
        Assert.Equal(SampleCount * 2, received[0].Length);
        Assert.Equal(0.5f, received[0][0], 4);
        Assert.Equal(0.5f, received[0][1], 4);
        Assert.True(transport.IsConnected);
        Assert.Equal(1, transport.Stats.PacketsAccepted);
        Assert.Equal(0, transport.Stats.PacketsRejected);
    }

    [Fact]
    public async Task AMonoFloatPacketKeepsItsChannelCount()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        var received = new List<(int SampleRate, int Channels, int SampleCount)>();
        transport.PacketReceived += (_, frame) => received.Add((frame.Format.SampleRate, frame.Format.Channels, frame.SampleCount));

        await transport.StartAsync();
        await SendAsync(port, frameCounter: 0, channels: 1, dataType: VbanDataType.Float32, srIndex: 16);

        await WaitUntilAsync(() => received.Count == 1);

        Assert.Equal((44_100, 1, SampleCount), received[0]);
    }

    [Fact]
    public async Task SkippedFrameCountersAreCountedAsLost()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();

        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16);
        await SendAsync(port, frameCounter: 5, channels: 2, dataType: VbanDataType.Int16);

        await WaitUntilAsync(() => callbacks == 2);

        // Counters 1..4 never arrived.
        Assert.Equal(4, transport.Stats.FramesLost);
        Assert.Equal(2, transport.Stats.PacketsAccepted);
    }

    [Fact]
    public async Task PacketsThatArriveLateAreCountedSeparatelyFromLoss()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();

        await SendAsync(port, frameCounter: 10, channels: 2, dataType: VbanDataType.Int16);
        await SendAsync(port, frameCounter: 9, channels: 2, dataType: VbanDataType.Int16);

        await WaitUntilAsync(() => callbacks == 2);

        Assert.Equal(1, transport.Stats.FramesOutOfOrder);
    }

    [Fact]
    public async Task NonVbanTrafficOnTheSamePortIsRejectedAndExplained()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        await transport.StartAsync();

        using var sender = new UdpClient();
        byte[] noise = Encoding.ASCII.GetBytes("definitely not audio");
        await sender.SendAsync(noise, noise.Length, new IPEndPoint(IPAddress.Loopback, port));

        await WaitUntilAsync(() => transport.Stats.PacketsRejected > 0);

        Assert.Equal(0, transport.Stats.PacketsAccepted);
        Assert.False(string.IsNullOrEmpty(transport.Stats.LastRejectionReason));
    }

    [Fact]
    public async Task PayloadsDescribedWronglyAreRejectedRatherThanMisread()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        await transport.StartAsync();

        // Advertise 256 samples but send half that many bytes.
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16, sampleCount: 256, payloadBytesOverride: 512);

        await WaitUntilAsync(() => transport.Stats.PacketsRejected > 0);

        Assert.Equal(0, transport.Stats.PacketsAccepted);
    }

    [Fact]
    public async Task AStreamNameFilterIgnoresOtherStreams()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port, streamName: "Phone");

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();

        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16, streamName: "SomethingElse");
        await Task.Delay(250);

        Assert.Equal(0, callbacks);
        Assert.Equal(1, transport.Stats.PacketsRejected);

        await SendAsync(port, frameCounter: 1, channels: 2, dataType: VbanDataType.Int16, streamName: "Phone");
        await WaitUntilAsync(() => callbacks == 1);

        Assert.Equal(1, transport.Stats.PacketsAccepted);
    }

    [Fact]
    public async Task StartAndStopCanBeRepeated()
    {
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        await transport.StartAsync();
        await transport.StartAsync();
        await transport.StopAsync();
        await transport.StopAsync();

        await transport.StartAsync();

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16);

        await WaitUntilAsync(() => callbacks == 1);
    }

    [Fact]
    public async Task PacketsAtAnUnexpectedSampleRateAreRefusedLoudly()
    {
        int port = FreePort();

        // The mixer channel is created with one fixed format, so letting a stream through at
        // a different rate would not resample it, it would play at the wrong speed. Refusing
        // surfaces the mismatch in LastRejectionReason instead of hiding it behind bad audio.
        await using var transport = new VbanTransport(port, expectedFormat: new AudioFormat(48_000, 2));

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16, srIndex: 16);

        await WaitUntilAsync(() => transport.Stats.PacketsRejected > 0);

        Assert.Equal(0, callbacks);
        Assert.Contains("expects", transport.Stats.LastRejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncryptedPacketLongerThanTheVbanCeilingStillArrives()
    {
        // The receive buffer used to be exactly VbanPacketHeader.MaxPacketBytes, the ceiling for a
        // *plain* VBAN packet. Sealing one adds the envelope on top, so a packet at the top of that
        // allowance arrives longer than the buffer that was waiting for it — and on a datagram
        // socket an oversized arrival is not clipped, it is refused outright, taking the whole
        // packet with it. Nothing here is hypothetical or malformed: 179 frames of stereo float32
        // is 1460 bytes, inside the 1464 the specification permits, and 1496 once sealed.
        int port = FreePort();
        var key = new byte[32];
        var envelope = new AesGcmEnvelope(key, salt: 0x01020304u);
        await using var transport = new VbanTransport(port, envelope: envelope, requireEncryption: true);

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();

        await SendAsync(
            port, frameCounter: 0, channels: 2, dataType: VbanDataType.Float32,
            sampleCount: 179, sealWith: envelope);

        // Wait for any verdict at all, so a failure reports the reason rather than timing out.
        await WaitUntilAsync(() =>
            transport.Stats.PacketsAccepted + transport.Stats.PacketsRejected > 0);

        Assert.True(
            transport.Stats.PacketsAccepted == 1,
            $"accepted={transport.Stats.PacketsAccepted}, rejected={transport.Stats.PacketsRejected}, " +
            $"reason={transport.Stats.LastRejectionReason ?? "(none)"}, connected={transport.IsConnected}");

        Assert.Equal(1, callbacks);
        Assert.Equal(1, transport.PacketsDecrypted);
    }

    [Fact]
    public async Task AnOversizedDatagramCostsOnePacketAndNotTheSession()
    {
        // Windows refuses to hand over a datagram bigger than the buffer at all (WSAEMSGSIZE),
        // rather than truncating it. That is a per-packet failure, but the receive loop used to
        // treat every SocketException as fatal, so a single oversized arrival stopped reception
        // for the rest of the process lifetime — with the socket still bound and nothing reading
        // it, which looks exactly like the sender having gone quiet.
        int port = FreePort();
        await using var transport = new VbanTransport(port);

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();

        using (var sender = new UdpClient())
        {
            byte[] huge = new byte[9_000];
            huge[0] = (byte)'V';
            huge[1] = (byte)'B';
            huge[2] = (byte)'A';
            huge[3] = (byte)'N';
            await sender.SendAsync(huge, huge.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        await WaitUntilAsync(() => transport.Stats.PacketsRejected > 0);

        // The whole point: an ordinary packet afterwards still gets through.
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16);
        await WaitUntilAsync(() => callbacks == 1);

        Assert.Equal(1, transport.Stats.PacketsAccepted);
        Assert.Contains("接收缓冲", transport.Stats.LastRejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APacketSealedWithTheWrongKeyIsBlamedOnTheKey()
    {
        // The most likely misconfiguration in this whole feature is a phone holding a key this PC
        // no longer has. In the default permissive mode such a packet used to fall through to the
        // VBAN parser and come back as "missing the VBAN magic", which points at the network.
        int port = FreePort();
        var hubKey = new byte[32];
        var phoneKey = new byte[32];
        phoneKey[0] = 1;

        await using var transport = new VbanTransport(
            port,
            envelope: new AesGcmEnvelope(hubKey, salt: 1),
            requireEncryption: false);

        await transport.StartAsync();

        await SendAsync(
            port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16,
            sealWith: new AesGcmEnvelope(phoneKey, salt: 2));

        await WaitUntilAsync(() => transport.Stats.PacketsRejected > 0);

        Assert.Equal(0, transport.Stats.PacketsAccepted);
        Assert.Contains("认证失败", transport.Stats.LastRejectionReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("VBAN", transport.Stats.LastRejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissiveModeStillAcceptsPlainPackets()
    {
        // The transitional setting, and the default: a phone that has not been given the key yet
        // has to keep working. The change above must not have turned this into a refusal.
        int port = FreePort();
        await using var transport = new VbanTransport(
            port,
            envelope: new AesGcmEnvelope(new byte[32], salt: 1),
            requireEncryption: false);

        int callbacks = 0;
        transport.PacketReceived += (_, _) => Interlocked.Increment(ref callbacks);

        await transport.StartAsync();
        await SendAsync(port, frameCounter: 0, channels: 2, dataType: VbanDataType.Int16);

        await WaitUntilAsync(() => callbacks == 1);

        Assert.Equal(1, transport.Stats.PacketsAccepted);
        Assert.Equal(0, transport.PacketsDecrypted);
    }

    private static async Task SendAsync(
        int port,
        uint frameCounter,
        int channels,
        VbanDataType dataType,
        int srIndex = 3,
        int sampleCount = SampleCount,
        string streamName = "AudioHub",
        int? payloadBytesOverride = null,
        ISecureEnvelope? sealWith = null)
    {
        int bytesPerSample = dataType == VbanDataType.Float32 ? 4 : 2;
        int payloadBytes = payloadBytesOverride ?? (bytesPerSample * channels * sampleCount);

        byte[] packet = new byte[VbanPacketHeader.Size + payloadBytes];
        packet[0] = (byte)'V';
        packet[1] = (byte)'B';
        packet[2] = (byte)'A';
        packet[3] = (byte)'N';
        packet[4] = (byte)srIndex;
        packet[5] = (byte)(sampleCount - 1);
        packet[6] = (byte)(channels - 1);
        packet[7] = (byte)dataType;
        Encoding.ASCII.GetBytes(streamName).CopyTo(packet, 8);

        for (int i = 0; i < 4; i++)
        {
            packet[24 + i] = (byte)(frameCounter >> (8 * i) & 0xFF);
        }

        FillPayload(packet.AsSpan(VbanPacketHeader.Size), dataType);

        if (sealWith is not null)
        {
            packet = sealWith.Seal(packet);
        }

        using var sender = new UdpClient();
        await sender.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static void FillPayload(Span<byte> payload, VbanDataType dataType)
    {
        // Constant 0.5 for both supported representations, so the assertion reads simply.
        if (dataType == VbanDataType.Int16)
        {
            for (int i = 0; i + 1 < payload.Length; i += 2)
            {
                payload[i] = 0x00;
                payload[i + 1] = 0x40;
            }
        }
        else if (dataType == VbanDataType.Float32)
        {
            for (int i = 0; i + 3 < payload.Length; i += 4)
            {
                BitConverter.GetBytes(0.5f).CopyTo(payload.Slice(i, 4));
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        Assert.Fail("Condition was not met within five seconds.");
    }
}
