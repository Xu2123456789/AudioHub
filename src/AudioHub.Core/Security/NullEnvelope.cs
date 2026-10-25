namespace AudioHub.Core.Security;

/// <summary>
/// Protects nothing and says so. Used for plain VBAN and for diagnosing problems that encryption
/// would otherwise hide behind a wall of ciphertext.
/// </summary>
/// <remarks>
/// Exists as a real implementation rather than a null check scattered through the transport: the
/// receiving path stays branchless, and the choice between protected and unprotected is made once
/// at startup instead of being asked about on every packet.
/// </remarks>
public sealed class NullEnvelope : ISecureEnvelope
{
    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public int OverheadBytes => 0;

    /// <inheritdoc />
    public bool LooksProtected(ReadOnlySpan<byte> packet) => false;

    /// <inheritdoc />
    public byte[] Seal(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    /// <inheritdoc />
    public bool TryOpen(ReadOnlySpan<byte> packet, out ReadOnlyMemory<byte> plaintext, out string? rejection)
    {
        plaintext = packet.ToArray();
        rejection = null;
        return true;
    }
}
