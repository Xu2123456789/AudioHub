namespace AudioHub.Core.Discovery;

/// <summary>
/// Answers "which hub is running here?" so a phone can find one without asking anybody to type
/// an address.
/// </summary>
/// <remarks>
/// <para>
/// This interface exists so that how discovery happens can be replaced without touching anything
/// else. The implementation here is the probe-and-reply one described in
/// <see cref="DiscoveryProtocol"/>; mDNS, Bonjour or anything else would slot in behind the same
/// interface, and the rest of AudioHub would not change by a line.
/// </para>
/// <para>
/// What is deliberately <i>not</i> here: any mention of identity or keys. Those belong to layers
/// that are supposed to stay put. Keeping discovery ignorant of them is what lets the discovery
/// mechanism be swapped later without forcing already-paired phones to pair again — the phone
/// recognises the hub by its id, not by how it was found.
/// </para>
/// </remarks>
public interface IDiscoveryTransport : IDisposable
{
    /// <summary>Whether the transport is currently answering.</summary>
    bool IsListening { get; }

    /// <summary>Nothing answered yet, and why: null when running, otherwise the reason.</summary>
    string? UnavailableReason { get; }

    /// <summary>Begins answering. Does not throw when the port is taken; records it instead.</summary>
    void Start();

    /// <summary>Stops answering and releases the port.</summary>
    void Stop();
}
