using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AudioHub.Core;
using AudioHub.Core.Discovery;
using AudioHub.Core.Security;
using AudioHub.Hosting;
using AudioHub.Platform.Windows;
using AudioHub.Sources.Vban;
using AudioHub.Web;
using NAudio.CoreAudioApi;
using NAudio.Wave;

Console.OutputEncoding = Encoding.UTF8;

// Headless entry point. Used for smoke tests and for measuring the pipeline without
// any UI in the way. Run with --help for the options.

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

// Arguments are the one part of this tool anyone types by hand, so a mistake here has to come
// back as a sentence rather than a stack trace. Before this existed there were two failure modes,
// both bad:
//
//   - Out of range values reached Validate() inside the host and threw an unhandled
//     ArgumentOutOfRangeException, dumping English internals at somebody who had merely mistyped
//     a number.
//   - Unparseable values were silently replaced by the default, so typing "--ceiling abc" quietly
//     ran with -6 dB and never said the value had been thrown away. That one is worse than the
//     crash: it leaves a person believing the setting they asked for is in force.
//
// Checked up front so every subcommand benefits, before any device is touched.
if (DescribeArgumentProblem(args) is { } problem)
{
    Console.WriteLine();
    Console.WriteLine(problem);
    Console.WriteLine();
    Console.WriteLine("加 --help 看完整用法。");
    return 2;
}

if (args.Contains("--list-devices"))
{
    IReadOnlyList<AudioEndpointInfo> devices = AudioEndpointEnumerator.RenderDevices();
    Console.WriteLine("Playback endpoints:");
    foreach (AudioEndpointInfo device in devices)
    {
        Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}");
        Console.WriteLine($"      {device.Id}");
    }

    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine($"AudioHub {VersionText()}");
    return 0;
}

if (args.Contains("--selftest"))
{
    return await RunSelfTestAsync();
}

if (args.Contains("--latency"))
{
    return await RunLatencyProbeAsync(args);
}

if (args.Contains("--probe-vban"))
{
    return await RunVbanProbeAsync(args);
}

// Proves the encryption end to end without a phone: builds a real VBAN packet, wraps it, puts it
// on a real UDP socket, lets the actual transport unwrap and parse it, and compares the audio that
// comes out against what went in. Also measures the cost per packet, because the reason to check
// is that nobody has to choose between safety and latency.
static async Task<int> RunSecureProbeAsync(string[] args)
{
    int port = int.TryParse(GetValue(args, "--probe-port"), out int parsedPort) ? parsedPort : 6_981;
    const int framesPerPacket = 256;

    Console.WriteLine("=== AudioHub encryption probe ===");
    Console.WriteLine();

    byte[] psk = HubKeyStore.GeneratePsk();
    Guid hubId = HubIdentity.LoadOrCreate().HubId;
    byte[] key = HubKeyStore.DeriveKey(psk, hubId);

    Console.WriteLine($"1. 密钥派生：PSK {HubKeyStore.ToDisplayString(psk)}");
    Console.WriteLine($"   绑定到本机身份 {HubIdentity.FromGuid(hubId).Fingerprint}，得到 32 字节 AES-256 密钥");

    AesGcmEnvelope sender = new(key);
    AesGcmEnvelope receiver = new(key);

    byte[] audio = BuildVbanPacket(framesPerPacket, 2, frameCounter: 1, seed: 0x5A);
    byte[] sealedPacket = sender.Seal(audio);

    Console.WriteLine($"2. 构造并封装一个真实 VBAN 包：明文 {audio.Length} 字节 → 密文 {sealedPacket.Length} 字节（+{sealedPacket.Length - audio.Length}）");
    Console.WriteLine();

    await using VbanTransport transport = new(port: port, envelope: receiver, requireEncryption: true);
    await transport.StartAsync();
    Console.WriteLine($"3. 接收端已启动：UDP {port}，强制加密（明文包一律丢弃）");

    using UdpClient client = new();

    async Task<int> SendAndCount(byte[] packet)
    {
        long before = transport.Stats.PacketsAccepted;
        client.Send(packet, packet.Length, "127.0.0.1", port);
        await Task.Delay(400);
        return (int)(transport.Stats.PacketsAccepted - before);
    }

    int good = await SendAndCount(sealedPacket);
    Console.WriteLine();
    Console.WriteLine($"4. 发送一个合法加密包 → 接受 {good} 个，解出 {transport.PacketsDecrypted} 个");

    // A plain VBAN packet under required mode answers "is the door actually locked", which a
    // successful decrypt alone cannot: that would also pass if encryption were silently ignored.
    int plain = await SendAndCount(audio);
    Console.WriteLine($"5. 发送同一个包的明文版本 → 接受 {plain} 个（强制模式下必须是 0）");

    byte[] tampered = (byte[])sealedPacket.Clone();
    tampered[^1] ^= 0xFF;
    int broken = await SendAndCount(tampered);
    Console.WriteLine($"6. 发送改了最后一个字节的密文 → 接受 {broken} 个（必须是 0）");

    AesGcmEnvelope stranger = new(HubKeyStore.DeriveKey(HubKeyStore.GeneratePsk(), hubId));
    int wrongKey = await SendAndCount(stranger.Seal(audio));
    Console.WriteLine($"7. 用另一个密钥封装同一个包 → 接受 {wrongKey} 个（必须是 0）");

    Console.WriteLine();

    // Timing: the claim that encryption is free has to be a number, not an assertion.
    const int iterations = 5_000;
    Stopwatch stopwatch = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        _ = sender.Seal(audio);
    }

    stopwatch.Stop();
    double microseconds = stopwatch.Elapsed.TotalMilliseconds * 1_000 / iterations;

    Console.WriteLine($"8. 封装 {iterations} 个包耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"   平均每包 {microseconds:F2} 微秒（48 kHz 下一个包是 5.3 毫秒的音频，占比 {(microseconds / 1000 / 5.33 * 100):F4}%）");
    Console.WriteLine();

    bool pass = good == 1 && plain == 0 && broken == 0 && wrongKey == 0;

    if (!pass)
    {
        Console.WriteLine("FAIL: 上面有哪一项不是预期的值。");
        Console.WriteLine($"     合法={good}(期望1) 明文={plain}(期望0) 篡改={broken}(期望0) 错密钥={wrongKey}(期望0)");
        return 1;
    }

    Console.WriteLine("PASS: 加密包能解开、明文/篡改/错密钥全部被拒。");
    Console.WriteLine();
    Console.WriteLine("注意：这只证明电脑端自己封得开、认得出。手机端还没实现加密，");
    Console.WriteLine("真正「手机发加密包 → 电脑收到」要等手机端做完再验一次。");
    return 0;
}

// A genuine VBAN packet, header and all, so the probe exercises the real parser rather than a
// stand-in. 256 frames of stereo 16-bit PCM is what the Android sender actually puts on the wire.
static byte[] BuildVbanPacket(int frames, int channels, uint frameCounter, byte seed)
{
    const int bytesPerSample = 2;
    int payloadBytes = frames * channels * bytesPerSample;
    byte[] packet = new byte[VbanPacketHeader.Size + payloadBytes];

    packet[0] = (byte)'V';
    packet[1] = (byte)'B';
    packet[2] = (byte)'A';
    packet[3] = (byte)'N';
    packet[4] = 0x03; // sample-rate index 3 = 48 000 Hz, sub-protocol 0 = native PCM audio
    packet[5] = (byte)(frames - 1);
    packet[6] = (byte)(channels - 1);
    packet[7] = 0x01; // data type 1 = 16-bit signed, codec 0 = uncompressed PCM

    byte[] name = System.Text.Encoding.ASCII.GetBytes("AudioHub");
    name.CopyTo(packet, 8);

    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), frameCounter);

    for (int i = 0; i < payloadBytes; i++)
    {
        packet[VbanPacketHeader.Size + i] = (byte)(seed + i);
    }

    return packet;
}

// Impersonates a phone asking "which hub is out there?", so discovery can be proven without a
// phone in hand. Discovery either works or nobody can use this app without typing an address,
// and there is no third state that is visible from the outside.
if (args.Contains("--probe-discovery"))
{
    return await RunDiscoveryProbeAsync(args);
}

if (args.Contains("--probe-secure"))
{
    return await RunSecureProbeAsync(args);
}

string? deviceId = GetValue(args, "--device");
int bufferMs = int.TryParse(GetValue(args, "--buffer"), out int parsedBuffer) ? parsedBuffer : 50;
int seconds = int.TryParse(GetValue(args, "--seconds"), out int parsedSeconds) ? parsedSeconds : 10;
float gainDb = float.TryParse(GetValue(args, "--gain"), out float parsedGain) ? parsedGain : 0f;

TransportKind transportKind = args.Contains("--vban") ? TransportKind.Vban : TransportKind.None;
int vbanPort = int.TryParse(GetValue(args, "--vban-port"), out int parsedVbanPort) ? parsedVbanPort : 6_980;
int jitterMs = int.TryParse(GetValue(args, "--jitter"), out int parsedJitter)
    ? parsedJitter
    : AudioHubOptions.Default.NetworkJitterBufferMs;
int localJitterMs = int.TryParse(GetValue(args, "--local-jitter"), out int parsedLocalJitter)
    ? parsedLocalJitter
    : AudioHubOptions.Default.LocalJitterBufferMs;
float ceilingDb = float.TryParse(GetValue(args, "--ceiling"), out float parsedCeiling)
    ? parsedCeiling
    : AudioHubOptions.Default.OutputCeilingDb;

// Optional rather than required by default, because a phone that has not been given the key yet
// would otherwise be locked out with no way to notice why. "auto" accepts both encrypted and
// plain packets; "on" insists on encrypted; "off" is the old behaviour.
SecurityMode securityMode = ParseSecureMode(GetValue(args, "--secure"), args.Contains("--insecure"));

// Off unless asked for. Capturing the PC's own playback while rendering to the same endpoint
// feeds the mixer back into itself; see AudioHubOptions.CaptureLocalSound for the measurement.
bool captureLocal = args.Contains("--local");

AudioHubOptions options = new(
    OutputDeviceId: deviceId,
    BufferMilliseconds: bufferMs,
    CaptureLocalSound: captureLocal,
    LoopbackDeviceId: GetValue(args, "--capture-device"),
    LocalJitterBufferMs: localJitterMs,
    LocalGainDb: gainDb,
    Transport: transportKind,
    VbanPort: vbanPort,
    VbanStreamName: GetValue(args, "--stream"),
    NetworkJitterBufferMs: jitterMs,
    OutputCeilingDb: ceilingDb,
    EnableDiscovery: !args.Contains("--no-discovery"),
    AdvertisedSecurity: securityMode);
await using WindowsAudioHubHost host = new(options);

if (args.Contains("--show-key"))
{
    Console.WriteLine($"AudioHub     : {VersionText()}");
    Console.WriteLine();
    Console.WriteLine($"本机身份     : {host.Identity.Fingerprint}");
    Console.WriteLine($"配对密钥     : {host.PairingKey ?? "(加密已关闭，没有密钥)"}");
    Console.WriteLine();
    Console.WriteLine("把这个密钥在手机上输一次就行，之后它会记住。");
    return 0;
}

WebConsole? web = null;
if (args.Contains("--web"))
{
    int port = int.TryParse(GetValue(args, "--port"), out int parsedPort) ? parsedPort : 5055;
    web = new WebConsole(host);
    await web.StartAsync(port);
    Console.WriteLine($"Web console  : http://localhost:{port}");

    if (web.LanReservationCommand is { } command)
    {
        Console.WriteLine($"Phone access : run once as administrator, then restart:");
        Console.WriteLine($"               {command}");
    }
}

host.RefreshOutputDevices();

Console.WriteLine($"AudioHub     : {VersionText()}");
Console.WriteLine($"Mixer output : {host.Mixer.Format.SampleRate} Hz, {host.Mixer.Format.Channels} ch");
Console.WriteLine($"WASAPI buffer: {bufferMs} ms");
Console.WriteLine($"Net jitter   : {jitterMs} ms  (raise with --jitter to survive bursty arrival)");
Console.WriteLine($"Channels     : {string.Join(", ", host.Mixer.ChannelIds)}");
Console.WriteLine($"Output device: {DescribeDevice(host, deviceId)}");
Console.WriteLine($"本机采集     : {DescribeLocalCapture(host, captureLocal)}");
Console.WriteLine($"输出上限     : {DescribeOutputCeiling(host.Mixer.OutputCeilingDb)}");
Console.WriteLine($"本机身份     : {host.Identity.Fingerprint}");
Console.WriteLine($"广播发现     : {DescribeDiscovery(host.Discovery)}");
Console.WriteLine($"加密         : {DescribeSecurity(host, securityMode)}");

await host.StartAsync();
Console.WriteLine("Running. Press Ctrl+C to stop.");

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    TimeSpan nextHintAt = TimeSpan.FromSeconds(5);
    while (!cancellation.IsCancellationRequested && stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        await Task.Delay(500, cancellation.Token);

        foreach (string channel in host.Mixer.ChannelIds)
        {
            float level = host.Mixer.GetLevel(channel);
            string bar = new('#', (int)Math.Clamp(level * 40, 0, 40));
            Console.WriteLine($"  {channel,-8} {level,7:F4} |{bar}");
            Console.WriteLine($"  {"",-8} {DescribeBuffer(host.Mixer.GetBufferStats(channel))}");
        }

        if (host.TransportStats is { } transportStats)
        {
            Console.WriteLine(
                $"  {"vban",-8} accepted={transportStats.PacketsAccepted,-6} " +
                $"rejected={transportStats.PacketsRejected,-4} lost={transportStats.FramesLost,-4} " +
                $"peak={transportStats.LastPeakLevel,7:F4} connected={transportStats.IsConnected}");
            Console.WriteLine($"  {"",-8} {DescribeArrival(transportStats.Arrival)}");

            // Say so when nothing has arrived. A window that has received no packets looks
            // exactly like one that has not started yet, and the two need opposite actions:
            // wait, versus go find out why the network is dropping everything.
            //
            // Repeated every 30 s rather than printed once, because the window is usually
            // started and then looked at some time later.
            if (transportStats.PacketsAccepted == 0 && stopwatch.Elapsed >= nextHintAt)
            {
                Console.WriteLine();
                Console.WriteLine(DescribeNothingArrivedYet(
                    (int)stopwatch.Elapsed.TotalSeconds, LocalIPv4Addresses()));
                Console.WriteLine();
                nextHintAt = stopwatch.Elapsed + TimeSpan.FromSeconds(30);
            }
        }
    }
}
catch (OperationCanceledException)
{
    // Expected on Ctrl+C.
}

await host.StopAsync();

if (web is not null)
{
    await web.DisposeAsync();
}

Console.WriteLine("Stopped.");
return 0;

// Plays a known tone and measures what the loopback source captures, so "does the pipeline
// actually move audio" is answered with a number instead of asking someone to listen. It also
// checks that the level falls back afterwards, which is how a feedback loop would show up.
//
// The tone is played into the endpoint being tapped, and that endpoint is deliberately NOT the
// one being rendered to. Both used to be the default endpoint, which made this test a feedback
// loop by construction: it could only ever report the loop's own ringing. That version passed
// for months, because a shallow jitter buffer threw away most of every trip around the loop and
// the level still decayed. Deepening the buffer raised the loop gain past one and the same test
// failed — which is how the loop was finally caught. Keep the tone and the render on different
// endpoints and the number means "audio got through", which is what it was always for.
static async Task<int> RunSelfTestAsync()
{
    Console.WriteLine("=== AudioHub self test ===");

    IReadOnlyList<AudioEndpointInfo> endpoints = AudioEndpointEnumerator.RenderDevices();
    Console.WriteLine($"1. Playback endpoints found: {endpoints.Count}");
    foreach (AudioEndpointInfo endpoint in endpoints)
    {
        Console.WriteLine($"   {(endpoint.IsDefault ? "*" : " ")} {endpoint.Name}");
    }

    if (endpoints.Count == 0)
    {
        Console.WriteLine("FAIL: no playback endpoint on this machine.");
        return 1;
    }

    // Render to the default endpoint (normally the headset) but tap a different one, exactly
    // as the host would choose on its own. With only one endpoint there is no way to avoid
    // the loop, so say so instead of quietly measuring it.
    AudioEndpointInfo? renderTo = endpoints.FirstOrDefault(e => e.IsDefault) ?? endpoints[0];
    AudioEndpointInfo? tap = endpoints.FirstOrDefault(e => e.Id != renderTo.Id);

    if (tap is null)
    {
        Console.WriteLine();
        Console.WriteLine("FAIL: only one playback endpoint, so the loopback tap would have to");
        Console.WriteLine("      read the endpoint being rendered to. That is a feedback loop,");
        Console.WriteLine("      not a measurement. Connect a second output device and rerun.");
        return 1;
    }

    await using WindowsAudioHubHost host = new(new AudioHubOptions(
        OutputDeviceId: renderTo.Id,
        BufferMilliseconds: 50,
        CaptureLocalSound: true,
        LoopbackDeviceId: tap.Id,
        LocalJitterBufferMs: AudioHubOptions.Default.LocalJitterBufferMs));
    await host.StartAsync();
    Console.WriteLine("2. Audio graph started: ok");
    Console.WriteLine($"   渲染到 : {renderTo.Name}");
    Console.WriteLine($"   抓取   : {tap.Name}（刻意不是输出设备）");

    float peak = 0f;
    using (IWavePlayer player = CreateTonePlayer(frequencyHz: 440, amplitude: 0.2f, tap))
    {
        player.Play();
        Console.WriteLine($"3. Playing 440 Hz tone into '{tap.Name}' for 2 s...");

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            peak = Math.Max(peak, host.Mixer.GetLevel("local"));
        }

        player.Stop();
    }

    Console.WriteLine($"4. Peak level captured while tone played: {peak:F4}");
    Console.WriteLine($"   缓冲计数器: {DescribeBuffer(host.Mixer.GetBufferStats("local"))}");
    Console.WriteLine("   （本机通道的缓冲是 100 ms：回环一次会送来约 65 ms，缓冲必须装得下整整一次。"
                      + "若这里仍在报大量欠载/溢出，说明分块比预想的大。）");

    await Task.Delay(1_500);
    float after = host.Mixer.GetLevel("local");
    Console.WriteLine($"5. Level 1.5 s after the tone stopped:     {after:F4}");

    await host.StopAsync();

    if (peak <= 0.01f)
    {
        Console.WriteLine();
        Console.WriteLine("FAIL: nothing was captured. Check that AudioHub is not muted and");
        Console.WriteLine("      that the output device is the one actually in use.");
        return 1;
    }

    if (after > peak * 0.2f)
    {
        // Note what this can and cannot see. This run taps the endpoint it renders to on
        // purpose, so a loop is always present here; the check only catches a loop strong
        // enough to keep ringing. A loop that loses more than it recirculates — which is
        // exactly what a too-shallow jitter buffer produces — decays to silence and sails
        // through as a PASS. Do not read a PASS here as "there is no feedback loop".
        Console.WriteLine();
        Console.WriteLine("FAIL: level did not fall after the tone stopped. Capture and output");
        Console.WriteLine("      are feeding back into each other strongly. Try a different output device.");
        Console.WriteLine("      (A weaker loop still passes here — see the note above.)");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("PASS: local playback is captured into the mixer and decays normally.");
    return 0;
}

// Answers "how long is this waiting inside the endpoint" with numbers rather than asking
// someone to listen. Everything it reports stops at WASAPI: whatever the headset does over
// the air afterwards is invisible from software and cannot be folded into these figures.
static async Task<int> RunLatencyProbeAsync(string[] args)
{
    string? deviceId = GetValue(args, "--device");
    int bufferMs = int.TryParse(GetValue(args, "--buffer"), out int parsedBuffer) ? parsedBuffer : 50;
    int seconds = int.TryParse(GetValue(args, "--seconds"), out int parsedSeconds) ? parsedSeconds : 5;

    Console.WriteLine($"Measuring endpoint latency for {seconds} s with a requested {bufferMs} ms buffer...");
    Console.WriteLine();

    try
    {
        WasapiLatencyProbe probe = new(deviceId);
        LatencyReport report = await probe.MeasureAsync(TimeSpan.FromSeconds(seconds), bufferMs);

        Console.WriteLine(LatencyReport.ToJson(report));
        Console.WriteLine();
        Console.WriteLine("Notes:");
        foreach (string note in report.Notes)
        {
            Console.WriteLine($"  - {note}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {ex.Message}");
        return 1;
    }
}

// Acts the part of the phone: broadcast a probe, collect whatever answers, and report what came
// back. Everything that can go wrong here goes wrong silently for a real user — no error, just a
// phone app with an empty list — so the whole point of this command is to turn silence into a
// sentence naming which of the three usual causes it is.
static async Task<int> RunDiscoveryProbeAsync(string[] args)
{
    int port = int.TryParse(GetValue(args, "--discovery-port"), out int parsedPort)
        ? parsedPort
        : DiscoveryProtocol.Port;
    int seconds = int.TryParse(GetValue(args, "--seconds"), out int parsedSeconds) ? parsedSeconds : 6;

    Console.WriteLine("=== AudioHub discovery probe ===");
    Console.WriteLine();
    Console.WriteLine("扮演手机：向本网段广播一次「有 AudioHub 吗」，然后等回答。");
    Console.WriteLine("请在另一台机器上运行 start-vban.bat，或者本机已经有实例在跑。");
    Console.WriteLine();

    List<(DiscoveryMessage Message, IPEndPoint From)> answers = [];
    List<string> rejections = [];

    using UdpClient client = new();
    client.EnableBroadcast = true;
    client.Client.ReceiveTimeout = Math.Max(200, seconds * 1_000);

    try
    {
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
    }
    catch (SocketException ex)
    {
        Console.WriteLine($"FAIL: 无法打开 UDP 接收端口：{ex.SocketErrorCode}");
        return 1;
    }

    uint token = (uint)Random.Shared.Next();
    byte[] probe = DiscoveryProtocol.BuildProbe(token);

    int sent = client.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, port));
    Console.WriteLine($"1. 已向 {IPAddress.Broadcast}:{port} 广播询问（{sent} 字节，标识 0x{token:X8}）");

    // Every reachable subnet gets a probe. A machine normally sits on several, and the hub is
    // only ever on one of them, so asking just one address would be asking the wrong one often
    // enough to make this look broken.
    foreach (IPEndPoint directed in SubnetBroadcastAddresses())
    {
        client.Send(probe, probe.Length, directed);
        Console.WriteLine($"   另外发往 {directed}");
    }

    Console.WriteLine();
    Console.WriteLine($"2. 等 {seconds} 秒……");
    Console.WriteLine();

    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
    while (DateTimeOffset.UtcNow < deadline)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            break;
        }

        try
        {
            Task<UdpReceiveResult> receive = client.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(remaining)) != receive)
            {
                break;
            }

            UdpReceiveResult received = await receive;
            if (DiscoveryProtocol.TryParse(received.Buffer, out DiscoveryMessage message, out string? rejection))
            {
                if (message.Type == DiscoveryMessageType.Announce)
                {
                    answers.Add((message, received.RemoteEndPoint));
                }
            }
            else
            {
                rejections.Add(rejection ?? "未知原因");
            }
        }
        catch (SocketException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }
    }

    Console.WriteLine($"3. 收到 {answers.Count} 个回答，丢弃 {rejections.Count} 个无法解析的包。");
    Console.WriteLine();

    foreach ((DiscoveryMessage message, IPEndPoint from) in answers)
    {
        bool tokenMatches = message.Token == token;
        Console.WriteLine($"   [{(tokenMatches ? "匹配" : "标识不符")}] {from.Address}:{message.VbanPort}");
        Console.WriteLine($"        名字     : {message.Name}");
        Console.WriteLine($"        身份指纹 : {HubIdentity.FromGuid(message.HubId).Fingerprint}");
        Console.WriteLine($"        完整 UUID: {message.HubId}");
        Console.WriteLine($"        安全模式 : {DescribeSecurityMode(message.SecurityMode)}");
    }

    foreach (string rejection in rejections.Take(3))
    {
        Console.WriteLine($"   丢弃一个包：{rejection}");
    }

    Console.WriteLine();

    if (answers.Count == 0)
    {
        Console.WriteLine("FAIL: 没有任何 AudioHub 回答。通常是这三件事之一：");
        Console.WriteLine();
        Console.WriteLine("  1  这台机器上没跑 AudioHub（另开一个窗口跑 start-vban.bat）");
        Console.WriteLine("  2  UDP 6979 被防火墙挡住了 —— 跑一次 dist\\allow-firewall.bat");
        Console.WriteLine("  3  这个网络不允许广播（校园网 / 公共 WiFi 常见）");
        Console.WriteLine();
        Console.WriteLine("注意：广播到不了 ≠ 单播到不了。手机的 VBAN 音频走的是单播 6980，");
        Console.WriteLine("两者受阻原因不同，所以这个 FAIL 不代表音频一定也不通。");
        return 1;
    }

    Console.WriteLine($"PASS: {answers.Count} 个 AudioHub 回答了询问。");
    Console.WriteLine();
    Console.WriteLine("这说明：UDP 6979 双向通了、广播能到对方、回来的身份也没被改错；");
    Console.WriteLine("手机那边的「找电脑」界面能列出它 —— 不用再手填 IP。");
    return 0;
}

static string DescribeSecurityMode(SecurityMode mode) => mode switch
{
    SecurityMode.None => "明文（目前的行为）",
    SecurityMode.Optional => "加密可选",
    SecurityMode.Required => "只接受加密",
    _ => "未知",
};

// Subnet-directed broadcasts for whichever networks this machine is actually on. Falls back to
// nothing rather than guessing: 255.255.255.255 already covers every interface once.
static IEnumerable<IPEndPoint> SubnetBroadcastAddresses()
{
    foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            continue;
        }

        IPInterfaceProperties properties = adapter.GetIPProperties();
        foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || IsPrivateLanAddress(unicast.Address.ToString()) == false)
            {
                continue;
            }

            // APIPA has no meaningful broadcast scope for our purposes.
            if (unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            {
                continue;
            }

            // Assume a /24, which covers essentially every home and hotspot network. A wrong mask
            // only costs one ignored datagram, since the global broadcast above still goes out.
            byte[] bytes = unicast.Address.GetAddressBytes();
            IPAddress directed = new([bytes[0], bytes[1], bytes[2], 255]);
            yield return new IPEndPoint(directed, DiscoveryProtocol.Port);
        }
    }
}

// Proves packets from the phone reach AudioHub, without anyone having to listen. A single
// line per second plus an exit code makes it usable both interactively and from a script.
static async Task<int> RunVbanProbeAsync(string[] args)
{
    int port = int.TryParse(GetValue(args, "--vban-port"), out int parsedPort) ? parsedPort : 6_980;
    string? streamName = GetValue(args, "--stream");
    int seconds = int.TryParse(GetValue(args, "--seconds"), out int parsedSeconds) ? parsedSeconds : 10;

    // Honours --secure so this probe can answer the question that is otherwise unanswerable
    // without a phone: are the packets the phone sends ones this PC can actually open? With
    // --secure=on, plain packets are dropped and only genuinely encrypted ones count.
    SecurityMode mode = ParseSecureMode(GetValue(args, "--secure"), args.Contains("--insecure"));
    ISecureEnvelope? envelope = mode == SecurityMode.None ? null : new AesGcmEnvelope(HubKeyStore.DeriveKey(HubKeyStore.LoadOrCreatePsk(), HubIdentity.LoadOrCreate().HubId));

    await using VbanTransport transport = new(
        port: port,
        streamName: streamName,
        envelope: envelope,
        requireEncryption: mode == SecurityMode.Required);
    await transport.StartAsync();

    string streamDescription = streamName is null
        ? string.Empty
        : $", stream '{streamName}'";

    Console.WriteLine($"Listening for VBAN audio on UDP {port}{streamDescription}.");
    Console.WriteLine($"Security: {DescribeSecurityMode(mode)}; key: {HubKeyStore.ToDisplayString(HubKeyStore.LoadOrCreatePsk())}");
    Console.WriteLine($"Watching for {seconds} s. Start sending from the phone app now.");
    Console.WriteLine();

    for (int elapsed = 1; elapsed <= seconds; elapsed++)
    {
        await Task.Delay(1_000);
        VbanTransportStats stats = transport.Stats;
        Console.WriteLine(
            $"  t={elapsed,3}s  accepted={stats.PacketsAccepted,-6} rejected={stats.PacketsRejected,-4} " +
            $"lost={stats.FramesLost,-4} reorder={stats.FramesOutOfOrder,-4} " +
            $"peak={stats.LastPeakLevel,7:F4} connected={stats.IsConnected}");
        Console.WriteLine($"         {DescribeArrival(stats.Arrival)}");
    }

    VbanTransportStats final = transport.Stats;

    Console.WriteLine();
    if (envelope is not null)
    {
        Console.WriteLine($"Decrypted {transport.PacketsDecrypted} packet(s).");
    }

    if (final.LastRejectionReason is { } reason)
    {
        Console.WriteLine($"Last rejection: {reason}");
    }

    if (final.PacketsAccepted > 0)
    {
        Console.WriteLine($"PASS: received {final.PacketsAccepted} VBAN packet(s) from the network.");
        Console.WriteLine();
        Console.WriteLine($"Arrival: {DescribeArrival(final.Arrival)}");
        if (final.Arrival.LooksBatched)
        {
            Console.WriteLine();
            Console.WriteLine("Batched arrival detected. Packets are being held back and released in");
            Console.WriteLine("groups, which the jitter buffer cannot smooth away at its default depth.");
            Console.WriteLine("Run start-vban.bat with --jitter raised (e.g. --jitter 250) to test that");
            Console.WriteLine("theory, and watch the buffer line it prints.");
        }

        Console.WriteLine();
        Console.WriteLine("Note: this proves packets arrived and decoded. How it sounds through the");
        Console.WriteLine("      headphones, and the real end-to-end latency, still need your ears.");
        return 0;
    }

    Console.WriteLine("FAIL: no VBAN packets arrived. Check the phone app sends to this PC's IP on");
    Console.WriteLine($"      UDP port {port}, that a firewall is not blocking it, and that the sample");
    Console.WriteLine("      rate matches what this source expects.");
    return 1;
}

// Plays into a specific endpoint rather than the system default. The self test needs the tone
// to land on the endpoint being tapped, which is not the one being rendered to.
static IWavePlayer CreateTonePlayer(int frequencyHz, float amplitude, AudioEndpointInfo endpoint)
{
    MMDevice? device = AudioEndpointEnumerator.FindRenderDevice(endpoint.Id);
    WasapiOut output = device is null
        ? new WasapiOut(AudioClientShareMode.Shared, true, 50)
        : new WasapiOut(device, AudioClientShareMode.Shared, true, 50);

    output.Init(new SineProvider(frequencyHz, amplitude));
    return output;
}

static void PrintUsage()
{
    Console.WriteLine($"AudioHub.Cli {VersionText()} — headless audio hub runner");
    Console.WriteLine();
    Console.WriteLine("  --version             Print the version and exit");
    Console.WriteLine("  --list-devices        List playback endpoints and exit");
    Console.WriteLine("  --device <id>         Render to this endpoint id (default: system default)");
    Console.WriteLine("  --buffer <ms>         WASAPI buffer size, 10-500 (default: 50)");
    Console.WriteLine("  --seconds <n>         Run for n seconds (default: 10)");
    Console.WriteLine("  --gain <db>           Local channel gain in dB (default: 0)");
    Console.WriteLine("  --local               Also capture this PC's own playback (default: off).");
    Console.WriteLine("                        Windows already mixes it into the output device, so this");
    Console.WriteLine("                        is only needed for per-channel gain and ducking.");
    Console.WriteLine("  --capture-device <id> Tap this endpoint for --local. Default: any endpoint");
    Console.WriteLine("                        that is not the one being rendered to, because tapping");
    Console.WriteLine("                        the output endpoint feeds the mixer back into itself.");
    Console.WriteLine("  --local-jitter <ms>   Local channel dejitter depth, 5-2000 (default: 100).");
    Console.WriteLine("                        Must be at least one loopback delivery (~65 ms here).");
    Console.WriteLine("  --selftest            Play a tone, measure capture, report PASS/FAIL");
    Console.WriteLine("  --latency             Measure output endpoint latency and print JSON");
    Console.WriteLine("  --probe-discovery    扮演手机：广播询问本机有没有 AudioHub，等回答");
    Console.WriteLine("                       并打印身份指纹。不用真手机就能验证发现层的进度。");
    Console.WriteLine("  --probe-vban          Listen for VBAN packets and report counters");
    Console.WriteLine("  --vban                Run with the VBAN (Android) source enabled");
    Console.WriteLine("  --vban-port <n>       UDP port for VBAN (default: 6980)");
    Console.WriteLine("  --stream <name>       VBAN stream name to accept (default: any)");
    Console.WriteLine("  --jitter <ms>         Network channel dejitter depth, 5-2000 (default: 80).");
    Console.WriteLine("                        Raise it to survive bursty arrival; costs that much latency.");
    Console.WriteLine("  --ceiling <db>        Loudest output in dBFS, -60 to 0 (default: -6). This caps");
    Console.WriteLine("                        how loud the headphones can get no matter what arrives.");
    Console.WriteLine("                        Real traffic here peaks near -11 dB, so ordinary listening");
    Console.WriteLine("                        never touches it. 0 turns the protection off -- use only");
    Console.WriteLine("                        when the limiter itself is what you are investigating.");
    Console.WriteLine("  --no-discovery        不回答手机的广播询问（手机需手填 IP，回到旧行为）。");
    Console.WriteLine("  --secure auto|on|off  加密模式（默认 auto）。auto = 加密的收、明文的也收，");
    Console.WriteLine("                        方便手机还没配对时先能用；on = 只收加密的；off = 明文。");
    Console.WriteLine("  --show-key            打印配对密钥（手机上输一次就会记住）后退出。");
    Console.WriteLine("  --insecure            等价于 --secure off。");
    Console.WriteLine("  --web                 Also serve the control API and console page");
    Console.WriteLine("  --port <n>            Port for --web (default: 5055)");
    Console.WriteLine("  --help                Show this text");
}

// Reads the version compiled into the assembly rather than hard-coding a string here, so
// the number that gets printed cannot drift away from the one the build actually stamped.
static string VersionText()
{
    Assembly assembly = Assembly.GetEntryAssembly()!;
    string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informational))
    {
        // Strip the "+<commit hash>" suffix Source Link appends; it is build detail, not version.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }

    return assembly.GetName().Version?.ToString() ?? "unknown";
}

// Every numeric switch validated here, so adding one means adding one line rather than
// remembering that rigour is expected. Ranges mirror what AudioHubOptions enforces; they are
// duplicated rather than caught because by the time Validate() runs we already want to be
// holding real devices open, and failing before that is much friendlier than failing during it.
static string? DescribeArgumentProblem(string[] args)
{
    return WholeNumber(args, "--buffer", min: 10, max: 500, fallback: 50)
        ?? WholeNumber(args, "--seconds", min: 1, max: 86_400, fallback: 10)
        ?? WholeNumber(args, "--vban-port", min: 1, max: 65_535, fallback: 6_980)
        ?? WholeNumber(args, "--port", min: 1, max: 65_535, fallback: 5_055)
        ?? WholeNumber(args, "--jitter", min: 5, max: 2_000, fallback: AudioHubOptions.Default.NetworkJitterBufferMs)
        ?? WholeNumber(args, "--local-jitter", min: 5, max: 2_000, fallback: AudioHubOptions.Default.LocalJitterBufferMs)
        ?? Decimal(args, "--gain", min: -60f, max: 20f, fallback: 0f)
        ?? Decimal(args, "--ceiling", min: OutputLimiter.MinCeilingDb, max: 0f, fallback: AudioHubOptions.Default.OutputCeilingDb);
}

static string? WholeNumber(string[] args, string name, int min, int max, int fallback)
{
    if (MissingValue(args, name, fallback) is { } missing)
    {
        return missing;
    }

    string? raw = GetValue(args, name);
    if (raw is null)
    {
        return null;
    }

    if (!int.TryParse(raw, out int value))
    {
        return $"{name} 要一个整数，收到的是 \"{raw}\"。不写就用默认值 {fallback}。";
    }

    return value < min || value > max
        ? $"{name} 的取值范围是 {min} 到 {max}，收到的是 {value}。"
        : null;
}

static string? Decimal(string[] args, string name, float min, float max, float fallback)
{
    if (MissingValue(args, name, fallback) is { } missing)
    {
        return missing;
    }

    string? raw = GetValue(args, name);
    if (raw is null)
    {
        return null;
    }

    if (!float.TryParse(raw, out float value))
    {
        return $"{name} 要一个数字，收到的是 \"{raw}\"。不写就用默认值 {fallback}。";
    }

    return value < min || value > max
        ? $"{name} 的取值范围是 {min} 到 {max}，收到的是 {value}。"
        : null;
}

// A switch sitting at the end of the line with nothing after it used to be ignored outright,
// which reads exactly like "accepted with its default". It is a mistake and should say so.
static string? MissingValue(string[] args, string name, object fallback)
{
    return args.Contains(name) && GetValue(args, name) is null
        ? $"{name} 后面少了一个值。写成 {name} {fallback} 这样的形式。"
        : null;
}

static string? GetValue(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string DescribeArrival(VbanArrivalStats arrival)
{
    if (arrival.NominalIntervalMs <= 0 && arrival.BurstCount == 0)
    {
        return "到达间隔：还没测到足够的数据";
    }

    string verdict = arrival.LooksBatched
        ? "=> 疑似批量下发"
        : "=> 看起来是均匀到达";

    return $"到达间隔：标称 {arrival.NominalIntervalMs:F2} ms  " +
           $"实测 p50={arrival.MedianMs:F1} p95={arrival.P95Ms:F1} max={arrival.MaxMs:F1} ms  " +
           $"突发 {arrival.BurstCount} 批 · 最大 {arrival.MaxBurstPackets} 包/批  {verdict}";
}

static string DescribeBuffer(ChannelBufferStats buffer)
{
    string state = buffer.IsPrimed ? "已预充" : "等待预充";
    return $"缓冲 {buffer.BufferedMilliseconds,6:F1} / {buffer.CapacityMilliseconds:F0} ms {state}  " +
           $"欠载 {buffer.Underruns} 次 · 填静音 {buffer.SilentSamplesInserted} 采样 · " +
           $"溢出丢弃 {buffer.SamplesDroppedOnOverflow} 采样";
}

static string DescribeDevice(IAudioHubHost host, string? requestedId)
{
    if (requestedId is null)
    {
        AudioEndpointInfo? fallback = host.OutputDevices.FirstOrDefault(d => d.IsDefault);
        return fallback is null ? "system default" : $"system default ({fallback.Name})";
    }

    AudioEndpointInfo? match = host.OutputDevices.FirstOrDefault(d => d.Id == requestedId);
    return match is null ? $"{requestedId} (not currently present)" : match.Name;
}

// States the local tap plainly, because "silence on the local channel" and "the local channel
// does not exist" look identical in the level meter and mean completely different things.
static string DescribeLocalCapture(IAudioHubHost host, bool requested)
{
    if (!requested)
    {
        return "关闭（默认）。电脑的声音由 Windows 直接混进输出设备，不经混音器。"
               + "加 --local 才会采集本机声音。";
    }

    if (host.LocalCaptureSkippedReason is { } reason)
    {
        return $"已跳过：{reason}。换个输出设备或加 --capture-device <id> 再试。";
    }

    AudioEndpointInfo? match = host.OutputDevices.FirstOrDefault(d => d.Id == host.LocalCaptureDeviceId);
    string name = match?.Name ?? host.LocalCaptureDeviceId ?? "?";

    bool sameAsOutput = host.LocalCaptureDeviceId is { } id && id == host.OutputDeviceId;
    return sameAsOutput
        ? $"{name} —— 注意：抓的就是输出设备本身，会自激（声音会叠加回授）"
        : name;
}

// Says which mode is in force and, in the modes that have one, where the key is. Encryption is
// easy to get wrong by omission: running with it silently off looks exactly like running with it
// on and nobody paired.
static string DescribeSecurity(IAudioHubHost host, SecurityMode mode) => mode switch
{
    SecurityMode.None => "关闭（明文）。任何人只要能发到 6980 端口就能被混音进来。",
    SecurityMode.Optional => $"自动（加密的收，明文的也收）。密钥用 --show-key 看。手机还没配对时也能用。",
    SecurityMode.Required => $"强制加密（明文包一律丢弃）。密钥用 --show-key 看。",
    _ => "未知",
};

static SecurityMode ParseSecureMode(string? value, bool insecureFlag)
{
    if (insecureFlag)
    {
        return SecurityMode.None;
    }

    return value?.Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "0" => SecurityMode.None,
        "on" or "required" or "1" => SecurityMode.Required,
        _ => SecurityMode.Optional,
    };
}

// Discovery failing silently is indistinguishable from a phone app that is broken, so it says
// what state it is in rather than trusting that "nothing to report" means "working".
static string DescribeDiscovery(IDiscoveryTransport? discovery)
{
    if (discovery is null)
    {
        return "已关闭（--no-discovery）。手机需要手动填 IP。";
    }

    // Asked before listening has begun, so this reports what was decided, not what happened yet.
    return discovery.UnavailableReason is { } reason
        ? $"启动不了 —— {reason}（手机仍需手动填 IP）"
        : $"已在 UDP {DiscoveryProtocol.Port} 等待手机询问（手机无需再填 IP）";
}

// Printed at startup rather than left implicit, because a limit nobody can see is a limit
// nobody knows they have. 0 dB is called out as unsafe since that is the one value where
// asking for no protection is a legitimate diagnostic move and also exactly how ears get hurt.
static string DescribeOutputCeiling(float ceilingDb)
{
    return ceilingDb >= 0f
        ? "0 dB（无保护）—— 任何能发到 6980 端口的设备都能把耳机推到满刻度。"
        : $"{ceilingDb:0.#} dBFS。任何输入都超不过这个值，用 --ceiling 可调（0 = 关闭保护）。";
}

// Lists every IPv4 address this machine currently holds, private LAN ranges first.
//
// Listing all of them rather than picking one is deliberate: a PC routinely has several
// (Wi-Fi, wired, plus virtual adapters from Hyper-V / WSL / VPN clients), and only the one on
// the same network as the phone will ever work. Guessing which one that is would silently
// send people to the wrong address.
static IReadOnlyList<string> LocalIPv4Addresses()
{
    List<string> lan = new();
    List<string> other = new();

    foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (adapter.OperationalStatus != OperationalStatus.Up ||
            adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            continue;
        }

        foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            string text = address.Address.ToString();

            // APIPA: Windows assigned this because nothing answered DHCP. It cannot be routed
            // and is never an address a phone would be sending to on purpose.
            if (text.StartsWith("169.254.", StringComparison.Ordinal))
            {
                continue;
            }

            (IsPrivateLanAddress(text) ? lan : other).Add(text);
        }
    }

    lan.Sort(StringComparer.Ordinal);
    other.Sort(StringComparer.Ordinal);
    return lan.Concat(other).ToList();
}

static bool IsPrivateLanAddress(string address)
{
    // 10/8, 192.168/16 and 172.16/12 — the ranges a home router or a phone hotspot hands out.
    if (address.StartsWith("10.", StringComparison.Ordinal) ||
        address.StartsWith("192.168.", StringComparison.Ordinal))
    {
        return true;
    }

    if (!address.StartsWith("172.", StringComparison.Ordinal))
    {
        return false;
    }

    string[] parts = address.Split('.');
    return parts.Length > 1 && int.TryParse(parts[1], out int second) && second is >= 16 and <= 31;
}

// What to print when the transport is listening but nothing has arrived.
//
// The three causes are ordered by how often they are the answer, and the advice ends with the
// two setups that work regardless of environment, because those are the ones the user can act
// on without being able to inspect the network.
static string DescribeNothingArrivedYet(int waitedSeconds, IReadOnlyList<string> addresses)
{
    StringBuilder text = new();
    text.AppendLine($"  还没收到任何包（已等 {waitedSeconds} 秒）。通常是这三件事之一：");
    text.AppendLine("    1  这个网络不允许设备互相访问（校园网 / 公共 WiFi 常见，叫 AP 隔离）");
    text.AppendLine("    2  手机和电脑不在同一个网段（比如一个连无线、一个插有线）");
    text.AppendLine($"    3  电脑防火墙没放行 UDP {AudioHubOptions.Default.VbanPort} —— 跑一次 dist\\allow-firewall.bat");

    text.AppendLine(addresses.Count > 0
        ? $"  手机上要填的地址（挑和你手机同网段的那个）：{string.Join("  ", addresses)}"
        : "  没查到 IPv4 地址 —— 电脑现在可能没连上任何网络。");

    text.AppendLine("  拿不准就改用手机热点或 USB 网络共享：这是你自己的私有链路，不受环境影响。");
    return text.ToString().TrimEnd();
}

internal sealed class SineProvider : IWaveProvider
{
    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private readonly double _phaseStep;
    private readonly float _amplitude;
    private double _phase;

    public SineProvider(int frequencyHz, float amplitude)
    {
        _amplitude = amplitude;
        _phaseStep = 2 * Math.PI * frequencyHz / _format.SampleRate;
    }

    public WaveFormat WaveFormat => _format;

    public int Read(byte[] buffer, int offset, int count)
    {
        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - (count % 4)));
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(Math.Sin(_phase) * _amplitude);
            _phase += _phaseStep;
        }

        return samples.Length * 4;
    }
}

