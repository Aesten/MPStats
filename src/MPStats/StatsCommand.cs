using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using MPManagerLinking;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// "!stats" (short: "!st"), open to everyone: what MPStats is doing right now, e.g.
//
//   [MPStats] Recording 20261011-213000-lighthouse: round 3 in progress, 2 done | 6 players, 41 spawns,
//             812 hits, 230 shots, 37 deaths
//   [MPStats] Not recording yet: starts at the first round (warmup isn't recorded). Last match: ...
//
// With MPManager on the server, the command is routed through it (MPManagerLink): hidden from chat, in !help,
// and usable from the server console (mpm cmd stats). Without MPManager, this mod hooks chat itself; the command
// then still appears in chat (the hook can observe messages but not swallow them). The hook runs inside the
// server's packet handler, where sending messages crashes the server, so it only queues commands; they run from
// the application tick.
internal static class StatsCommand
{
    private const string Tag = "[MPStats] ";

    private static readonly string[] Names = { "stats", "st" };

    private static readonly Queue<NetworkCommunicator> Pending = new Queue<NetworkCommunicator>();
    private static ChatBox _chatBox;

    // Hands the command to MPManager if it's loaded; false without it. Called at startup (every mod's DLL is
    // loaded before any OnSubModuleLoad, so MPManager can be found), and again by Attach.
    public static bool RouteThroughMPManager()
    {
        bool wasRouted = MPManagerLink.Routed;
        bool routed = MPManagerLink.TryRegister(Names, "", "What MPStats is recording right now.", adminOnly: false, Run);
        if (routed && !wasRouted)
        {
            MPStatsSubModule.Log("Chat command routed through MPManager.");
        }
        return routed;
    }

    // Per mission: hooks chat when MPManager isn't there (once per ChatBox instance).
    public static void Attach()
    {
        if (RouteThroughMPManager())
        {
            if (_chatBox != null)
            {
                _chatBox.OnMessageReceivedAtDedicatedServer -= OnMessage;
                _chatBox = null;
            }
            return;
        }
        ChatBox chatBox = Game.Current?.GetGameHandler<ChatBox>();
        if (chatBox == null || chatBox == _chatBox)
        {
            return;
        }
        if (_chatBox != null)
        {
            _chatBox.OnMessageReceivedAtDedicatedServer -= OnMessage;
        }
        _chatBox = chatBox;
        _chatBox.OnMessageReceivedAtDedicatedServer += OnMessage;
        MPStatsSubModule.Log("Chat command attached.");
    }

    private static void OnMessage(NetworkCommunicator fromPeer, string message)
    {
        if (message == null || fromPeer == null)
        {
            return;
        }
        string[] words = message.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0].Length < 2 || words[0][0] != '!' || Array.IndexOf(Names, words[0].Substring(1).ToLowerInvariant()) < 0)
        {
            return;
        }
        lock (Pending)
        {
            Pending.Enqueue(fromPeer);
        }
    }

    // Called from the application tick: main thread, outside packet handling.
    public static void ProcessPending()
    {
        while (true)
        {
            NetworkCommunicator peer;
            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    return;
                }
                peer = Pending.Dequeue();
            }
            try
            {
                if (peer.IsConnectionActive)
                {
                    ServerChat.Send(peer, Tag + Status());
                }
            }
            catch (Exception e)
            {
                MPStatsSubModule.Log("!stats failed: " + e);
            }
        }
    }

    // Entry point when MPManager routes the command; peer is null for the server console.
    private static void Run(NetworkCommunicator peer, string[] args, Action<string> reply)
    {
        reply(Tag + Status());
    }

    private static string Status()
    {
        MatchRecorder recorder = MatchRecorder.Current;
        string last = MatchRecorder.LastMatch;
        string lastText = last != null ? " Last match: " + last + "." : "";
        if (Mission.Current == null)
        {
            return "Not recording (between missions)." + lastText;
        }
        if (recorder == null)
        {
            return "Not recording: " + MultiplayerOptions.OptionType.GameType.GetStrValue() + " isn't recorded (Skirmish and Battle only)." + lastText;
        }
        return recorder.Status() + (recorder.HasMatch ? "" : lastText);
    }
}
