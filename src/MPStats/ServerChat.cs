using NetworkMessages.FromServer;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// Server messages in chat. Only from the main thread outside packet handling (a tick or a round event): sending
// from inside the server's packet handler crashes it.
internal static class ServerChat
{
    // To everyone, and to the log.
    public static void Broadcast(string text)
    {
        MPStatsSubModule.Log(text);
        if (!GameNetwork.NetworkPeersValid)
        {
            return;
        }
        foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
        {
            if (peer.IsServerPeer || !peer.IsSynchronized)
            {
                continue;
            }
            GameNetwork.BeginModuleEventAsServer(peer);
            GameNetwork.WriteMessage(new ServerMessage(text));
            GameNetwork.EndModuleEventAsServer();
        }
    }
}
