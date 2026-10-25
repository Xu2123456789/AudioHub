using AudioHub.Sources.Vban;

namespace AudioHub.Tests;

/// <summary>
/// Pins the "is this stream arriving in batches?" verdict down with the numbers real runs
/// actually produce.
/// </summary>
/// <remarks>
/// <para>
/// This verdict has now been wrong in both directions, which is why it has tests instead of
/// just a comment. The first version called a perfectly even stream batched; the second called
/// a genuinely batched stream even. Both mistakes cost a real measurement, because the verdict
/// is what tells someone whether to spend latency on a deeper buffer.
/// </para>
/// <para>
/// The two cases below are verbatim readings: one from the simulator on this PC's loopback
/// (paced, and known to be paced), one from a real Redmi K80 over Wi-Fi.
/// </para>
/// </remarks>
public sealed class VbanArrivalStatsTests
{
    // 256 frames at 48 kHz -> the sender aims to put out one packet every 5.33 ms.
    private const double Nominal = 5.33;

    [Fact]
    public void 均匀到达_不判为批量()
    {
        // Simulator, UDP loopback: p50=5.0 p95=7.0 max=10.7, biggest run 2 packets.
        VbanArrivalStats paced = new(Nominal, MedianMs: 5.0, P95Ms: 7.0, MaxMs: 10.7, BurstCount: 204, MaxBurstPackets: 2);

        Assert.False(paced.LooksBatched);
    }

    [Fact]
    public void 真机批量到达_判为批量()
    {
        // Redmi K80 over Wi-Fi, 2026-10-08: p50=0.0 p95=23.0 max=191.8, biggest run 28 packets.
        // A median of zero is the signature, not a missing measurement: the packets land
        // back-to-back, so their gaps round down to 0 ms in a 1 ms histogram.
        VbanArrivalStats batched = new(Nominal, MedianMs: 0.0, P95Ms: 23.0, MaxMs: 191.8, BurstCount: 5_245, MaxBurstPackets: 28);

        Assert.True(batched.LooksBatched);
    }

    /// <summary>
    /// The mistake this guards against: a "have we measured anything" check written as
    /// <c>MedianMs &gt; 0</c> rejects the very streams that are batched, because a real burst
    /// has a median gap of zero.
    /// </summary>
    [Fact]
    public void 中位数为零不代表没测到()
    {
        VbanArrivalStats batched = new(Nominal, MedianMs: 0.0, P95Ms: 23.0, MaxMs: 191.8, BurstCount: 5_245, MaxBurstPackets: 28);

        Assert.Equal(0d, batched.MedianMs);
        Assert.True(batched.LooksBatched, "a zero median must not be read as 'nothing measured'");
    }

    [Fact]
    public void 什么都没测到_不判为批量()
    {
        Assert.False(VbanArrivalStats.Empty.LooksBatched);
    }
}
