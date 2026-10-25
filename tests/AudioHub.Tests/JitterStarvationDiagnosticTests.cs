using System.Text;
using AudioHub.Core;
using AudioHub.Engine;

namespace AudioHub.Tests;

/// <summary>
/// 诊断用测试（不是回归测试）：把"手机声音有噪音、音量一下大一下小"变成可读的数字。
/// </summary>
/// <remarks>
/// <para>
/// 背景：用户真机联调时听到两个问题——① 噪音；② 耳机里音量忽大忽小。
/// 现象发生在 VBAN 通道上，而 VBAN 通道唯一会破坏音频连续性的地方就是
/// <see cref="JitterBuffer"/>。这个文件用完全确定性的模拟把它量化：
/// 不碰声卡、不碰网络、不依赖耳朵，跑一次就出一张表。
/// </para>
/// <para>
/// 两个场景的唯一差别是"包到达的节奏"：
/// <list type="bullet">
/// <item>均匀到达——每 10 ms 渲染块恰好交付对应数量的包；</item>
/// <item>批量到达——包先攒起来，每 100 ms 一次性交付。</item>
/// </list>
/// 后者是 802.11 省电模式的真实行为（AP 把单播帧缓存在信标周期上，DTIM 一到才发），
/// 而本机 Intel AX201 在电池供电下的节能模式正是"中等节能"（见 CONTINUE.md 第七节）。
/// </para>
/// <para>
/// 判据：输入是一个振幅 0.5、440 Hz 的正弦。这种信号在 48 kHz 下相邻采样点的最大差值是
/// 0.5·2π·440/48000 ≈ 0.0288。输出里出现远大于它的台阶 = 爆音；
/// 某 10 ms 块的有效值掉到中位数一半以下 = 音量跌落。两个数字都是客观的。
/// </para>
/// </remarks>
public sealed class JitterStarvationDiagnosticTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;

    /// <summary>手机端一个包携带的帧数，必须与 AudioCaptureService.FRAMES_PER_READ 一致。</summary>
    private const int PacketFrames = 256;

    /// <summary>渲染块大小，10 ms。NAudio 实际用的值，这里取整便于叙述。</summary>
    private const int BlockFrames = 480;

    private const double ToneHz = 440d;
    private const double ToneAmplitude = 0.5d;

    /// <summary>理想正弦在相邻采样点之间的最大台阶，用来给"爆音"定一个客观阈值。</summary>
    private static readonly double IdealMaxStep = ToneAmplitude * 2 * Math.PI * ToneHz / Rate;

    [Fact]
    public void 诊断_网络通道在两种到达节奏下的输出质量()
    {
        const int blocks = 3_000; // 30 秒
        var report = new StringBuilder();

        report.AppendLine("网络通道输出质量诊断");
        report.AppendLine($"  信号           : {ToneHz} Hz 正弦, 振幅 {ToneAmplitude}");
        report.AppendLine($"  格式           : {Rate} Hz / {Channels} ch");
        report.AppendLine($"  抖动缓冲       : {MixerChannelOptions.Network.JitterBufferMs} ms " +
                          $"({MixerChannelOptions.Network.JitterBufferMs * Rate / 1000} 帧)");
        report.AppendLine($"  包大小         : {PacketFrames} 帧 ({PacketFrames * 1000.0 / Rate:F2} ms)");
        report.AppendLine($"  渲染块         : {BlockFrames} 帧 (10 ms)");
        report.AppendLine($"  时长           : {blocks * BlockFrames / (double)Rate:F0} 秒");
        report.AppendLine($"  理想最大台阶   : {IdealMaxStep:F5}");
        report.AppendLine();

        Measure(report, "A. 均匀到达（每 10 ms 交付一次）", burstEveryBlocks: 1, blocks);
        report.AppendLine();
        Measure(report, "B. 批量到达（每 100 ms 交付一次，802.11 省电模式）", burstEveryBlocks: 10, blocks);

        string text = report.ToString();
        File.WriteAllText(ReportPath("arrival-quality"), text, new UTF8Encoding(false));

        Assert.Contains("理想最大台阶", text);
    }

    /// <summary>
    /// 单独验证抖动缓冲的溢出策略：容量 80 ms 的缓冲收到 100 ms 数据时，
    /// 被丢掉的是最旧的 20 ms，还是最新的 20 ms？这决定用户听到的是"跳音"还是"延迟增大"。
    /// </summary>
    [Fact]
    public void 诊断_抖动缓冲溢出时丢掉的是最旧的一段音频()
    {
        const int capacityFrames = 80 * Rate / 1000;   // 3840 帧
        const int prefillFrames = capacityFrames / 4;  // 960 帧，构造方式同 MixerEngine
        const int incomingFrames = 100 * Rate / 1000;  // 4800 帧，一个 100 ms 突发

        var buffer = new JitterBuffer(capacityFrames, prefillFrames);

        // 单声道递增序列：样本值就是它的序号，读出来的第一个值直接告诉我们丢了多长。
        float[] ramp = new float[incomingFrames];
        for (int i = 0; i < incomingFrames; i++)
        {
            ramp[i] = i;
        }

        buffer.Write(ramp);

        float[] got = new float[capacityFrames];
        int read = buffer.Read(got);

        var report = new StringBuilder();
        report.AppendLine("抖动缓冲溢出行为诊断");
        report.AppendLine($"  容量        : {capacityFrames} 帧 ({capacityFrames * 1000.0 / Rate:F0} ms)");
        report.AppendLine($"  预充阈值    : {prefillFrames} 帧 ({prefillFrames * 1000.0 / Rate:F0} ms)");
        report.AppendLine($"  一次写入    : {incomingFrames} 帧 ({incomingFrames * 1000.0 / Rate:F0} ms)");
        report.AppendLine($"  读回        : {read} 帧");
        report.AppendLine($"  第一个样本   : {got[0]:F0}  -> 丢掉了最旧的 {got[0]:F0} 帧 ({got[0] * 1000.0 / Rate:F1} ms)");
        report.AppendLine($"  最后一个样本 : {got[read - 1]:F0}");
        report.AppendLine($"  缓冲内剩余   : {buffer.Count} 帧");

        string text = report.ToString();
        File.WriteAllText(ReportPath("buffer-overflow"), text, new UTF8Encoding(false));

        Assert.Equal(capacityFrames, read);
    }

    private static void Measure(StringBuilder report, string name, int burstEveryBlocks, int blocks)
    {
        var engine = new MixerEngine(new AudioFormat(Rate, Channels));
        engine.AddChannel("net", new AudioFormat(Rate, Channels), MixerChannelOptions.Network);
        engine.Start();

        var pending = new List<float[]>();
        var output = new float[BlockFrames * Channels];
        var blockRms = new List<double>(blocks);

        double phase = 0d;
        double producedFrames = 0d;
        double packetizedFrames = 0d;
        double deliveredFrames = 0d;
        double maxStep = 0d;
        double maxStepAfterStartup = 0d;
        double previous = double.NaN;
        long stepsOverIdeal = 0;
        long samplesSeen = 0;
        long silentSamples = 0;
        long totalSamples = 0;

        // The first 100 ms are excluded from the "after startup" figure on purpose: the ramp
        // in JitterBuffer is deliberately not applied to the very first audio a stream produces,
        // so that one seam is expected and would otherwise mask whether the recurring ones are
        // fixed.
        const int StartupSamples = 4_800 * Channels;

        for (int block = 0; block < blocks; block++)
        {
            // 1. 发送端按自己的时钟产生包（这里不模拟漂移，只看到达节奏的影响）。
            producedFrames += BlockFrames;
            while (producedFrames - packetizedFrames >= PacketFrames)
            {
                packetizedFrames += PacketFrames;
                float[] packet = new float[PacketFrames * Channels];
                for (int frame = 0; frame < PacketFrames; frame++)
                {
                    float value = (float)(Math.Sin(phase) * ToneAmplitude);
                    phase += 2 * Math.PI * ToneHz / Rate;
                    packet[frame * Channels] = value;
                    packet[(frame * Channels) + 1] = value;
                }

                pending.Add(packet);
            }

            // 2. 网络交付：均匀模式每块交付；批量模式攒够 burstEveryBlocks 块再一次性交付。
            if ((block + 1) % burstEveryBlocks == 0)
            {
                foreach (float[] packet in pending)
                {
                    engine.Submit("net", packet);
                    deliveredFrames += PacketFrames;
                }

                pending.Clear();
            }

            // 3. 渲染时钟拉取一块。
            engine.Read(output);

            double sum = 0d;
            int silentInBlock = 0;
            for (int i = 0; i < output.Length; i++)
            {
                float value = output[i];
                if (value == 0f)
                {
                    silentInBlock++;
                }

                sum += (double)value * value;

                if (!double.IsNaN(previous))
                {
                    double step = Math.Abs(value - previous);
                    if (step > maxStep)
                    {
                        maxStep = step;
                    }

                    if (step > IdealMaxStep * 2)
                    {
                        stepsOverIdeal++;
                    }

                    if (samplesSeen >= StartupSamples && step > maxStepAfterStartup)
                    {
                        maxStepAfterStartup = step;
                    }
                }

                previous = value;
                samplesSeen++;
            }

            blockRms.Add(Math.Sqrt(sum / output.Length));
            silentSamples += silentInBlock;
            totalSamples += output.Length;
        }

        engine.Dispose();

        double[] sorted = blockRms.Where(r => r > 0).OrderBy(r => r).ToArray();
        double median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        int dipBlocks = blockRms.Count(r => r < median * 0.5);
        int deadBlocks = blockRms.Count(r => r == 0);

        report.AppendLine(name);
        report.AppendLine($"  交付帧数        : {deliveredFrames:F0} 帧（应为 {blocks * BlockFrames} 帧）");
        report.AppendLine($"  最大台阶        : {maxStep:F5}   （理想 {IdealMaxStep:F5}，" +
                          $"倍数 {maxStep / IdealMaxStep:F1}x）");
        report.AppendLine($"  最大台阶（跳过前 100 ms）: {maxStepAfterStartup:F5}   " +
                          $"（倍数 {maxStepAfterStartup / IdealMaxStep:F1}x）");
        report.AppendLine($"  超过 2 倍理想值的台阶数   : {stepsOverIdeal}");
        report.AppendLine($"  块有效值中位数   : {median:F4}");
        report.AppendLine($"  音量跌落块数     : {dipBlocks} / {blocks} 个 10 ms 块 " +
                          $"（{dipBlocks * 100.0 / blocks:F1}%）");
        report.AppendLine($"  完全静音块数     : {deadBlocks} / {blocks} 个 10 ms 块 " +
                          $"（{deadBlocks * 100.0 / blocks:F1}%）");
        report.AppendLine($"  静音采样比例     : {silentSamples * 100.0 / totalSamples:F1}%");
    }

    /// <summary>诊断报告落盘位置。放在系统临时目录，方便直接读取，不污染仓库。</summary>
    private static string ReportPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"audiohub-jitter-{name}.txt");
}
