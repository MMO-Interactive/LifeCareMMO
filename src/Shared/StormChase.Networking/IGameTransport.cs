using StormChase.Protocol;

namespace StormChase.Networking;

/// <summary>What a game authority needs from its player transport: the direct encrypted UDP host, or the Gateway link.</summary>
public interface IGameTransport : IDisposable
{
    bool Send<T>(uint connectionId, MessageKind kind, T payload, bool reliable = true);
    IReadOnlyList<TransportEvent> Poll(int maxEvents = 256);
    void Close(uint connectionId, string reason = "Disconnected");
    string GetConnectionStatus(uint connectionId);
}
