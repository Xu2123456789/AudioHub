using System.Text;
using AudioHub.Core;
using AudioHub.Engine;

namespace AudioHub.Tests;

/// <summary>
/// 诊断 + 回归：本机那一路（WASAPI 回环）在不丢声音的前提下需要多深的抖动缓冲。
/// </summary>
/// <remarks>
/// <para>
/// 起因：2026-10-07 在真机上量到本机通道 <c>欠载 690 次 · 填静音 662400 采样 ·
/// 溢出丢弃 586368 采样</c>，也就是真实声音只占约 30.8%、约 62% 被丢掉。根因不是网络，
/// 而是 <b>回环一次送来的音频比缓冲还大</b>：实测一次约 65 ms，而本机通道的缓冲只有 20 ms。
/// 装不下就丢掉多余的，空了就填静音，于是本机声音被切成三分之一。
/// </para>
/// <para>
/// 这里用完全确定的模拟复现它：一个稳定的源每 65 ms 交一次货（回环的真实形状——
/// 它不是按包来的，是把上次回调之后设备产生的所有音频一次性交出来），渲染时钟每 10 ms 拉一块。
/// 不碰声卡、不碰网络、不依赖耳朵。
/// </para>
/// <para>
/// 结论写成了断言，所以以后谁把缓冲改浅，这个测试会立刻失败并给出数字。
/// </para>
/// </remarks>
public sealed class LoopbackDeliveryTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;

    /// <summary>渲染块大小，10 ms。WASAPI 共享模式的典型引擎周期。</summary>
    private const int BlockMs = 10;

    /// <summary>
    /// 回环一次交出来的音频长度。由真机计数器反解得到（见 CONTINUE.md 第六节第 15/16 条），
    /// 与 NAudio WasapiLoopbackCapture 默认 100 ms 缓冲、每半个缓冲轮询一次的行为相符。
    /// </summary>
    private const int DeliveryMs = 65;

    private const int Blocks = 3_000; // 30 秒

    /// <summary>前 200 ms 是起播：缓冲要先攒到预充阈值，这段静音是正常的，不计入判据。</summary>
    private const int StartupBlocks = 20;

    [Fact]
    public void 诊断_本机回环的分块交付需要多深的缓冲()
    {
        var report = new StringBuilder();
        report.AppendLine("本机通道（WASAPI 回环）缓冲深度诊断");
        report.AppendLine($"  格式           : {Rate} Hz / {Channels} ch");
        report.AppendLine($"  回环一次交付   : {DeliveryMs} ms");
        report.AppendLine($"  渲染块         : {BlockMs} ms");
        report.AppendLine($"  时长           : {Blocks * BlockMs / 1000.0:F0} 秒（跳过前 {StartupBlocks * BlockMs} ms 起播）");
        report.AppendLine();

        Outcome before = Run(new MixerChannelOptions(JitterBufferMs: 20), "A. 旧的 20 ms 缓冲");
        Write(report, before);

        report.AppendLine();

        Outcome after = Run(MixerChannelOptions.Local, $"B. 现在的 {MixerChannelOptions.Local.JitterBufferMs} ms 缓冲");
        Write(report, after);

        string text = report.ToString();
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "audiohub-loopback-delivery.txt"),
            text,
            new UTF8Encoding(false));

        // 旧值必须确实是坏的，否则这个诊断没有说服力。
        Assert.True(before.SilentRatio > 0.5, $"20 ms 缓冲本该丢掉大半声音，实测静音比例 {before.SilentRatio:P1}");

        // 新值必须确实是好的。
        Assert.Equal(0, after.Underruns);
        Assert.Equal(0, after.DroppedSamples);
        Assert.True(after.SilentRatio < 0.01, $"{MixerChannelOptions.Local.JitterBufferMs} ms 缓冲仍有 {after.SilentRatio:P1} 静音");
    }

    /// <summary>缓冲必须至少装得下一次交付，否则每次回调都要么溢出要么欠载。</summary>
    [Fact]
    public void 本机缓冲深度不小于一次回环交付()
    {
        Assert.True(
            MixerChannelOptions.Local.JitterBufferMs >= DeliveryMs,
            $"本机缓冲 {MixerChannelOptions.Local.JitterBufferMs} ms 装不下一次回环交付 {DeliveryMs} ms。");
    }

    private static Outcome Run(MixerChannelOptions options, string name)
    {
        var engine = new MixerEngine(new AudioFormat(Rate, Channels));
        engine.AddChannel("local", new AudioFormat(Rate, Channels), options);
        engine.Start();

        var output = new float[(BlockMs * Rate / 1000) * Channels];
        double phase = 0d;
        double due = 0d;

        long silentSamples = 0;
        long countedSamples = 0;

        for (int block = 0; block < Blocks; block++)
        {
            // 回环：攒够一次交付的时长就把这一整段交出来。
            due += BlockMs;
            if (due >= DeliveryMs)
            {
                due -= DeliveryMs;
                int frames = DeliveryMs * Rate / 1000;
                float[] chunk = new float[frames * Channels];
                for (int frame = 0; frame < frames; frame++)
                {
                    float value = (float)(Math.Sin(phase) * 0.5d);
                    phase += 2 * Math.PI * 440d / Rate;
                    chunk[frame * Channels] = value;
                    chunk[(frame * Channels) + 1] = value;
                }

                engine.Submit("local", chunk);
            }

            engine.Read(output);

            if (block >= StartupBlocks)
            {
                foreach (float value in output)
                {
                    if (value == 0f)
                    {
                        silentSamples++;
                    }

                    countedSamples++;
                }
            }
        }

        ChannelBufferStats stats = engine.GetBufferStats("local");
        engine.Dispose();

        return new Outcome(
            name,
            options.JitterBufferMs,
            stats.Underruns,
            stats.SilentSamplesInserted,
            stats.SamplesDroppedOnOverflow,
            countedSamples == 0 ? 0 : silentSamples / (double)countedSamples);
    }

    private static void Write(StringBuilder report, Outcome outcome)
    {
        report.AppendLine(outcome.Name);
        report.AppendLine($"  缓冲深度        : {outcome.JitterBufferMs} ms");
        report.AppendLine($"  欠载次数        : {outcome.Underruns}");
        report.AppendLine($"  填静音采样      : {outcome.SilentSamples}");
        report.AppendLine($"  溢出丢弃采样    : {outcome.DroppedSamples}");
        report.AppendLine($"  静音采样比例    : {outcome.SilentRatio:P1}（跳过起播）");
    }

    private sealed record Outcome(
        string Name,
        int JitterBufferMs,
        long Underruns,
        long SilentSamples,
        long DroppedSamples,
        double SilentRatio);
}
