using NetworkMessages.FromServer;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// Server messages in chat. Only from the main thread outside packet handling (a tick or a round event): sending
// from inside the server's packet handler crashes it.
internal static class ServerChat
{
    public static void Send(NetworkCommunicator peer, string text)
    {
        if (peer.IsServerPeer || !peer.IsSynchronized)
        {
            return;
        }
        GameNetwork.BeginModuleEventAsServer(peer);
        GameNetwork.WriteMessage(new ServerMessage(text));
        GameNetwork.EndModuleEventAsServer();
    }

    // To everyone. Not to the server log: the round stats would add a line per player per round, and the match
    // records hold the same data.
    public static void Broadcast(string text)
    {
        if (!GameNetwork.NetworkPeersValid)
        {
            return;
        }
        foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
        {
            Send(peer, text);
        }
    }
}
