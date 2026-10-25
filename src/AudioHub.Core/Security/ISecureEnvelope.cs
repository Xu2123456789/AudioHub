namespace AudioHub.Core.Security;

/// <summary>
/// Wraps and unwraps packets as they cross the network, so how they are protected can be chosen
/// without touching the code around it.
/// </summary>
/// <remarks>
/// <para>
/// The mixer, jitter buffer, transport diagnostics and every front end sit either side of this
/// and do not know which implementation is in use. That is the point: encryption here is a layer
/// that can be swapped, not a property of the audio path.
/// </para>
/// <para>
/// Only one promise is made. <see cref="TryOpen"/> returns false rather than partial data when a
/// packet fails authentication — never a decrypted-looking buffer that happens to be wrong.
/// </para>
/// </remarks>
public interface ISecureEnvelope
{
    /// <summary>False for the pass-through case, which reports honestly rather than pretending.</summary>
    bool IsEnabled { get; }

    /// <summary>How many bytes <see cref="Seal"/> adds to a packet, and what the maximum payload is.</summary>
    int OverheadBytes { get; }

    /// <summary>
    /// Whether a packet's leading bytes are recognisably this envelope's own. False for the
    /// pass-through implementation, which has no marker to recognise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so a receiver can tell "some other program's traffic" apart from "one of ours
    /// that would not open". They need different explanations, and the second is by far the more
    /// likely of the two: a packet carrying our magic and failing to authenticate means the keys
    /// do not match. Falling back to the audio parser reports it as missing VBAN's own magic,
    /// which blames the network for a configuration mistake — in a feature whose single most
    /// likely failure is a mistyped or stale key, that is the one diagnosis worth getting right.
    /// </para>
    /// <para>
    /// It inspects cleartext bytes only and says nothing about authenticity. Never treat a true
    /// return as permission to use the packet; that is <see cref="TryOpen"/>'s job alone.
    /// </para>
    /// </remarks>
    bool LooksProtected(ReadOnlySpan<byte> packet);

    /// <summary>Wraps <paramref name="plaintext"/> for sending.</summary>
    byte[] Seal(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Unwraps a received packet. Returns false with a reason when anything about it does not
    /// check out; <paramref name="plaintext"/> is then guaranteed empty rather than half-populated.
    /// </summary>
    bool TryOpen(ReadOnlySpan<byte> packet, out ReadOnlyMemory<byte> plaintext, out string? rejection);
}
