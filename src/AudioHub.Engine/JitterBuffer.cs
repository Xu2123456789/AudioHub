namespace AudioHub.Engine;

/// <summary>
/// A circular buffer that absorbs arrival jitter on a channel.
/// </summary>
/// <remarks>
/// <para>
/// Capture threads write; the render thread reads. A monitor guards the indices,
/// which is deliberately simple: the critical sections are pure memcpy-length work
/// and are held for microseconds. A lock-free single-producer/single-consumer ring
/// is the obvious optimisation if profiling ever shows contention.
/// </para>
/// <para>
/// The buffer primes before it hands out audio, and re-primes after an underrun,
/// so a channel that stutters does not immediately start stuttering again.
/// </para>
/// <para>
/// <b>Why the seams have to be ramped.</b> Two events here destroy continuity, and both
/// used to be a hard cut: running dry mid-read (the rest of the block was zeroed), and
/// dropping the oldest audio because a burst did not fit. A hard cut is a step
/// discontinuity, and a step is a click — measured on a 440 Hz sine at 17x the largest
/// step that signal should ever produce. Ramping the last couple of milliseconds down,
/// and the first couple back up, turns the step into a slope short enough to be
/// inaudible. The ramp does not bring back audio that is genuinely missing; it only stops
/// the seam from being heard as a click. Getting the audio back is a matter of the buffer
/// being large enough, which is what <c>--jitter</c> exists to tune.
/// </para>
/// <para>
/// A ramp is only ever raised while playback is already under way — that is, after the buffer
/// has handed out real audio at least once. The very first audio a stream produces is passed
/// through untouched, because that transition is a one-off that happens while the listener is
/// still waiting for sound to start, and because fading it would mean the buffer no longer
/// passes its first block through unchanged, which is a property everything downstream is
/// entitled to assume.
/// </para>
/// </remarks>
public sealed class JitterBuffer
{
    private readonly float[] _buffer;
    private readonly int _prefillSamples;
    private readonly int _fadeSamples;
    private readonly object _gate = new();
    private int _head;
    private int _tail;
    private int _count;
    private bool _primed;
    private bool _everPrimed;
    private bool _needsFadeIn;
    private bool _hasServedAudio;
    private long _underruns;
    private long _silentSamplesInserted;
    private long _samplesDroppedOnOverflow;

    /// <summary>Creates a buffer able to hold <paramref name="capacitySamples"/> floats.</summary>
    /// <param name="capacitySamples">Total capacity, in interleaved floats.</param>
    /// <param name="prefillSamples">How much must accumulate before reads return audio.</param>
    /// <param name="fadeSamples">
    /// Length of the ramp applied where audio starts or stops, in interleaved floats.
    /// Zero means no ramping, which is what the ring-buffer unit tests want; the mixer
    /// passes a couple of milliseconds.
    /// </param>
    public JitterBuffer(int capacitySamples, int prefillSamples, int fadeSamples = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);

        _buffer = new float[capacitySamples];
        _prefillSamples = Math.Clamp(prefillSamples, 0, capacitySamples);
        _fadeSamples = Math.Clamp(fadeSamples, 0, capacitySamples / 2);
    }

    /// <summary>Total capacity in interleaved floats.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Floats currently buffered.</summary>
    public int Count
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>Whether the buffer has enough audio to serve reads.</summary>
    public bool IsPrimed
    {
        get { lock (_gate) { return _primed; } }
    }

    /// <summary>
    /// How many reads ran dry after the stream had started. The startup prefill is not
    /// counted: that is the buffer doing its job, not the network misbehaving.
    /// </summary>
    public long Underruns
    {
        get { lock (_gate) { return _underruns; } }
    }

    /// <summary>Total samples invented as silence because the buffer ran dry.</summary>
    public long SilentSamplesInserted
    {
        get { lock (_gate) { return _silentSamplesInserted; } }
    }

    /// <summary>Total samples discarded because incoming audio did not fit.</summary>
    public long SamplesDroppedOnOverflow
    {
        get { lock (_gate) { return _samplesDroppedOnOverflow; } }
    }

    /// <summary>Discards everything buffered and requires priming again.</summary>
    /// <remarks>
    /// Statistics are deliberately left alone: they describe the session, not the buffer's
    /// contents, so a stop/start cycle does not erase the evidence.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _tail = 0;
            _count = 0;
            _primed = false;
            _needsFadeIn = false;
            _hasServedAudio = false;
        }
    }

    /// <summary>
    /// Appends samples. When they do not fit, the oldest audio is dropped so the
    /// channel stays as close to live as possible rather than accumulating lag.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            bool dropped = false;

            if (samples.Length >= _buffer.Length)
            {
                // Everything already buffered goes as well, so the loss is the old contents
                // plus whatever of this block does not fit. Counting only the latter would
                // under-report exactly the case a burst produces.
                int droppedNow = _count + samples.Length - _buffer.Length;
                _samplesDroppedOnOverflow += droppedNow;
                dropped = droppedNow > 0;
                samples[^_buffer.Length..].CopyTo(_buffer);
                _head = 0;
                _tail = 0;
                _count = _buffer.Length;
            }
            else
            {
                int free = _buffer.Length - _count;
                if (samples.Length > free)
                {
                    int overflow = samples.Length - free;
                    _head = (_head + overflow) % _buffer.Length;
                    _count -= overflow;
                    _samplesDroppedOnOverflow += overflow;
                    dropped = true;
                }

                int firstRun = Math.Min(samples.Length, _buffer.Length - _tail);
                samples[..firstRun].CopyTo(_buffer.AsSpan(_tail));

                if (firstRun < samples.Length)
                {
                    samples[firstRun..].CopyTo(_buffer.AsSpan(0));
                }

                _tail = (_tail + samples.Length) % _buffer.Length;
                _count += samples.Length;
            }

            // The splice from dropped audio into the new audio is only audible on the next
            // read, so ask for a fade-in there instead of trying to crossfade two spans here.
            // Two things must both hold for that to be right:
            //   - this write did the dropping, not an earlier one. Testing the cumulative
            //     counter would ramp every subsequent write too, heard as constant pumping.
            //   - playback is already under way. A stream that overflows its buffer before a
            //     single sample has been read has no seam to smooth; ramping there would just
            //     attenuate the first block, which nothing downstream expects.
            if (dropped && _hasServedAudio)
            {
                _needsFadeIn = true;
            }

            if (_count >= _prefillSamples)
            {
                _primed = true;
                _everPrimed = true;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/>, zero-padding on underrun, and returns
    /// how many floats came from real buffered audio.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            if (!_primed || _count == 0)
            {
                destination.Clear();
                NoteStarvation(destination.Length);
                _primed = false;

                // Only a dryness that interrupts playback is a seam. The silence spent filling
                // the buffer the first time round is the buffer working, not a glitch.
                if (_hasServedAudio)
                {
                    _needsFadeIn = true;
                }

                return 0;
            }

            int take = Math.Min(destination.Length, _count);
            int firstRun = Math.Min(take, _buffer.Length - _head);
            _buffer.AsSpan(_head, firstRun).CopyTo(destination[..firstRun]);

            if (firstRun < take)
            {
                _buffer.AsSpan(0, take - firstRun).CopyTo(destination[firstRun..take]);
            }

            _head = (_head + take) % _buffer.Length;
            _count -= take;

            if (take > 0)
            {
                _hasServedAudio = true;
            }

            if (_needsFadeIn)
            {
                ApplyRamp(destination[..Math.Min(_fadeSamples, take)], rising: true);
                _needsFadeIn = false;
            }

            // Audio is ending either when this block could not be filled, or when the block was
            // filled exactly and nothing is left behind it. Both need the delivered tail ramped
            // down, and missing the second case is what left one full-scale step per burst:
            // the block was served in full and the very next one was silence.
            bool endsAudio = take < destination.Length || _count == 0;
            if (endsAudio)
            {
                ApplyRamp(destination[(take - Math.Min(_fadeSamples, take))..take], rising: false);
                _primed = false;
                _needsFadeIn = true;
            }

            if (take < destination.Length)
            {
                destination[take..].Clear();
                NoteStarvation(destination.Length - take);
            }

            return take;
        }
    }

    private void NoteStarvation(int silentSamples)
    {
        if (silentSamples <= 0 || !_everPrimed)
        {
            return;
        }

        _underruns++;
        _silentSamplesInserted += silentSamples;
    }

    private static void ApplyRamp(Span<float> samples, bool rising)
    {
        int count = samples.Length;
        if (count == 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            float position = (i + 1f) / count;
            samples[i] *= rising ? position : 1f - position;
        }
    }
}
