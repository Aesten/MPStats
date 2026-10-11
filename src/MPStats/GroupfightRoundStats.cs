using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// Groupfight (Battle): at the end of each round, everyone gets one chat line per player who spawned that round:
//
//   Round 3:
//   Player1: d=65, td=21, HP=22
//
// d = damage dealt to enemy players and bots, td = damage dealt to teammates (not counting yourself), HP = health
// left when the round ended (0 if dead or gone). Damage is the health actually removed (a 120 hit on a 40 HP enemy
// counts 40), from the game's own per-hit callback; hits on horses don't count, a horse's charge counts for its
// rider. Attackers are listed first, then defenders, each by damage dealt.
//
// Cost: per hit, two dictionary lookups and an addition; text is only built at round end.
internal sealed class GroupfightRoundStats : MissionLogic
{
    private sealed class PlayerStats
    {
        public string Name;
        public BattleSideEnum Side;
        public float Damage, TeamDamage;
    }

    private readonly Dictionary<MissionPeer, PlayerStats> _players = new Dictionary<MissionPeer, PlayerStats>();
    private MultiplayerRoundController _rounds;
    private bool _reportPending;

    public override void AfterStart()
    {
        _rounds = Mission.GetMissionBehavior<MultiplayerRoundController>();
        if (_rounds == null)
        {
            return;
        }
        _rounds.OnRoundStarted += OnRoundStarted;
        _rounds.OnRoundEnding += OnRoundEnding;
    }

    protected override void OnEndMission()
    {
        if (_rounds != null)
        {
            _rounds.OnRoundStarted -= OnRoundStarted;
            _rounds.OnRoundEnding -= OnRoundEnding;
        }
    }

    private void OnRoundStarted()
    {
        _players.Clear();
        _reportPending = false;
    }

    // Sent from the next tick rather than from inside the round controller's state change. Not when the mission is
    // being ended (!setmap, !resetmap...): MPManager zeroes the mission timer, which the round controller shares,
    // so it ends the round in progress as if its time were up.
    private void OnRoundEnding()
    {
        _reportPending = Mission.GetMissionBehavior<MissionLobbyComponent>()?.CurrentMultiplayerState
            != MissionLobbyComponent.MultiplayerGameState.Ending;
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        MissionPeer peer = agent.MissionPeer;
        if (peer != null && agent.IsHuman && !_players.ContainsKey(peer))
        {
            _players[peer] = new PlayerStats { Name = peer.GetNetworkPeer()?.UserName ?? peer.Name, Side = agent.Team?.Side ?? BattleSideEnum.None };
        }
    }

    // damagedHp is the health the hit removed (at most what the victim had left); 0 for blocked hits.
    public override void OnScoreHit(Agent affectedAgent, Agent affectorAgent, WeaponComponentData attackerWeapon, bool isBlocked,
        bool isSiegeEngineHit, in Blow blow, in AttackCollisionData collisionData, float damagedHp, float hitDistance, float shotDifficulty)
    {
        if (damagedHp <= 0 || affectedAgent == null || affectorAgent == null || !affectedAgent.IsHuman)
        {
            return;
        }
        Agent attacker = affectorAgent.IsMount ? affectorAgent.RiderAgent : affectorAgent;
        if (attacker == null || attacker == affectedAgent || attacker.MissionPeer == null
            || !_players.TryGetValue(attacker.MissionPeer, out PlayerStats stats))
        {
            return;
        }
        if (attacker.Team != null && attacker.Team == affectedAgent.Team)
        {
            stats.TeamDamage += damagedHp;
        }
        else
        {
            stats.Damage += damagedHp;
        }
    }

    public override void OnMissionTick(float dt)
    {
        if (!_reportPending)
        {
            return;
        }
        _reportPending = false;
        if (_players.Count == 0)
        {
            return;
        }

        // Health left: only agents still in the mission's active list (removed agents' data may be freed).
        Dictionary<MissionPeer, float> health = new Dictionary<MissionPeer, float>();
        foreach (Agent agent in Mission.Agents)
        {
            if (agent.IsHuman && agent.MissionPeer != null && agent.IsActive())
            {
                health[agent.MissionPeer] = agent.Health;
            }
        }

        List<string> lines = new List<string> { "Round " + _rounds.RoundCount + ":" };
        foreach (KeyValuePair<MissionPeer, PlayerStats> entry in _players
            .OrderBy(e => e.Value.Side == BattleSideEnum.Attacker ? 0 : 1)
            .ThenByDescending(e => e.Value.Damage))
        {
            PlayerStats s = entry.Value;
            health.TryGetValue(entry.Key, out float hp);
            lines.Add(s.Name + ": d=" + Round(s.Damage) + ", td=" + Round(s.TeamDamage) + ", HP=" + Round(hp));
        }
        foreach (string line in lines)
        {
            ServerChat.Broadcast(line);
        }
    }

    private static int Round(float value) => (int)System.Math.Round(value);
}
