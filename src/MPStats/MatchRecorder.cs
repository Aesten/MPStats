using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// Records a Skirmish or Battle match (one mission) as raw events: spawns, hits, shots, deaths and rounds (see
// docs/design.md). One recorder per mission.
//
// Only real rounds are recorded: from the round controller's round start (players spawn during preparation) to its
// round end. Warmup runs no rounds, so it records nothing, and a mission that never leaves warmup has no match.
// The match opens at the first round and closes when the mission ends: "complete" when the game ended it (the
// round controller's match end), "aborted" when an admin or MPManager did (!setmap, !resetmap, !mode, !warmup,
// standby). A round cut short that way still gets its row: MPManager zeroes the mission timer, which the round
// controller shares, so the controller ends the round as timed out (earlier in the same tick: its
// OnPreDisplayMissionTick runs before our OnMissionTick).
//
// On the main thread, events only append structs to lists; once a second the lists go to StatsWriter. Agents are
// only touched while the game hands them to us (active), and are remembered by reference only. An error turns
// recording off for the rest of the mission (logged once) and closes the match as "error"; the game goes on.
internal sealed class MatchRecorder : MissionLogic
{
    private const double FlushIntervalSeconds = 1;

    private static readonly FieldInfo MoraleField = typeof(MissionMultiplayerFlagDomination)
        .GetField("_morale", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dictionary<Agent, int> _spawns = new Dictionary<Agent, int>();
    // Horses by the spawn of the rider they came with, so hits on and deaths of riderless horses still have an owner.
    private readonly Dictionary<Agent, int> _horseOwners = new Dictionary<Agent, int>();
    private readonly Dictionary<MissionPeer, string> _playerIds = new Dictionary<MissionPeer, string>();

    private MultiplayerRoundController _rounds;
    private MissionLobbyComponent _lobby;
    private MatchInfo _match;
    private Batch _batch;
    private bool _recording, _closed, _failed;
    private int _round, _nextSpawn;
    private DateTime _roundStart, _nextFlush;
    private DateTime? _fightStart;
    // The weapon of the hit being reported: OnAgentHit (which has the item) runs just before OnScoreHit (which has
    // the damage dealt and whether it was blocked).
    private string _hitWeapon;

    public override void AfterStart()
    {
        _rounds = Mission.GetMissionBehavior<MultiplayerRoundController>();
        _lobby = Mission.GetMissionBehavior<MissionLobbyComponent>();
        if (_rounds == null || _lobby == null)
        {
            _failed = true;
            return;
        }
        _rounds.OnRoundStarted += OnRoundStarted;
        _rounds.OnPreparationEnded += OnPreparationEnded;
        _rounds.OnRoundEnding += OnRoundEnding;
    }

    protected override void OnEndMission()
    {
        if (_rounds != null)
        {
            _rounds.OnRoundStarted -= OnRoundStarted;
            _rounds.OnPreparationEnded -= OnPreparationEnded;
            _rounds.OnRoundEnding -= OnRoundEnding;
        }
        // Normally closed when the mission started ending; this catches a mission closed some other way.
        if (_match != null && !_closed)
        {
            try
            {
                Close(_failed ? "error" : "aborted", announce: false);
            }
            catch (Exception e)
            {
                MPStatsSubModule.Log("Closing match " + _match.Id + " failed: " + e.Message);
            }
        }
    }

    private void OnRoundStarted()
    {
        if (_failed)
        {
            return;
        }
        try
        {
            if (_match == null)
            {
                Open();
            }
            _round = _rounds.RoundCount;
            _recording = true;
            _roundStart = DateTime.UtcNow;
            _fightStart = null;
            _spawns.Clear();
            _horseOwners.Clear();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void OnPreparationEnded()
    {
        if (_recording)
        {
            _fightStart = DateTime.UtcNow;
        }
    }

    private void OnRoundEnding()
    {
        try
        {
            if (!_recording)
            {
                return;
            }
            _recording = false;
            float? morale = null;
            MissionMultiplayerFlagDomination mode = Mission.GetMissionBehavior<MissionMultiplayerFlagDomination>();
            if (mode != null && MoraleField != null)
            {
                morale = (float)MoraleField.GetValue(mode);
            }
            _batch.Rounds.Add(new RoundRow
            {
                Round = _round,
                Start = _roundStart,
                FightStart = _fightStart,
                End = DateTime.UtcNow,
                Winner = _rounds.RoundWinner,
                Reason = _rounds.RoundEndReason,
                Morale = morale,
                AttackerAlive = AliveHumans(Mission.AttackerTeam),
                DefenderAlive = AliveHumans(Mission.DefenderTeam),
            });
            _match.RoundsPlayed++;
            Flush();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public override void OnMissionTick(float dt)
    {
        if (_match == null || _closed || _failed)
        {
            return;
        }
        try
        {
            if (_lobby.CurrentMultiplayerState == MissionLobbyComponent.MultiplayerGameState.Ending)
            {
                Close(_rounds.IsMatchEnding ? "complete" : "aborted", announce: true);
            }
            else if (DateTime.UtcNow >= _nextFlush)
            {
                Flush();
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        if (!_recording || !agent.IsHuman)
        {
            return;
        }
        try
        {
            int spawn = ++_nextSpawn;
            _spawns[agent] = spawn;
            // The mount is built before its rider, who already sits on it here.
            if (agent.MountAgent != null)
            {
                _horseOwners[agent.MountAgent] = spawn;
            }
            _batch.Spawns.Add(new SpawnRow
            {
                Spawn = spawn,
                Round = _round,
                Time = DateTime.UtcNow,
                Player = PlayerId(agent.MissionPeer),
                Side = agent.Team?.Side ?? BattleSideEnum.None,
                Class = agent.Character?.StringId ?? "",
                Horse = agent.SpawnEquipment?[EquipmentIndex.Horse].Item?.StringId ?? "",
            });
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon, in Blow blow, in AttackCollisionData attackCollisionData)
    {
        _hitWeapon = _recording ? affectorWeapon.Item?.StringId ?? "" : null;
    }

    public override void OnScoreHit(Agent affectedAgent, Agent affectorAgent, WeaponComponentData attackerWeapon, bool isBlocked,
        bool isSiegeEngineHit, in Blow blow, in AttackCollisionData collisionData, float damagedHp, float hitDistance, float shotDifficulty)
    {
        if (!_recording || affectedAgent == null)
        {
            return;
        }
        try
        {
            Agent attacker = affectorAgent != null && affectorAgent.IsMount ? affectorAgent.RiderAgent : affectorAgent;
            bool horseVictim = affectedAgent.IsMount;
            Team victimTeam = horseVictim ? affectedAgent.RiderAgent?.Team ?? affectedAgent.Team : affectedAgent.Team;
            _batch.Hits.Add(new HitRow
            {
                Time = DateTime.UtcNow,
                Round = _round,
                Attacker = SpawnOf(attacker),
                Victim = horseVictim ? HorseOwner(affectedAgent) : SpawnOf(affectedAgent),
                HorseVictim = horseVictim,
                Team = attacker != null && attacker != affectedAgent && attacker.Team != null && attacker.Team == victimTeam,
                Attack = Kind(affectorAgent, in blow, in collisionData),
                DamageType = blow.DamageType,
                Weapon = _hitWeapon ?? "",
                WeaponClass = blow.WeaponRecord.WeaponClass,
                Part = blow.VictimBodyPart,
                Block = !isBlocked ? BlockKind.None
                    : collisionData.AttackBlockedWithShield ? BlockKind.Shield
                    : collisionData.CollisionResult == CombatCollisionResult.ChamberBlocked ? BlockKind.Chamber
                    : BlockKind.Parry,
                Hp = damagedHp,
                Raw = blow.InflictedDamage,
                AttackerMounted = attacker != null && attacker.MountAgent != null,
                VictimMounted = !horseVictim && affectedAgent.MountAgent != null,
                AttackerSpeed = Speed(attacker),
                VictimSpeed = Speed(horseVictim ? affectedAgent : affectedAgent.MountAgent ?? affectedAgent),
                Distance = blow.IsMissile ? (blow.GlobalPosition - blow.WeaponRecord.StartingPosition).Length : 0f,
                Kill = damagedHp > 0f && affectedAgent.Health < 1f,
            });
        }
        catch (Exception e)
        {
            Fail(e);
        }
        _hitWeapon = null;
    }

    public override void OnAgentShootMissile(Agent shooterAgent, EquipmentIndex weaponIndex, Vec3 position, Vec3 velocity, Mat3 orientation, bool hasRigidBody, int forcedMissileIndex)
    {
        if (!_recording || shooterAgent == null)
        {
            return;
        }
        try
        {
            MissionWeapon weapon = weaponIndex == EquipmentIndex.None ? MissionWeapon.Invalid : shooterAgent.Equipment[weaponIndex];
            _batch.Shots.Add(new ShotRow
            {
                Time = DateTime.UtcNow,
                Round = _round,
                Shooter = SpawnOf(shooterAgent),
                Weapon = weapon.Item?.StringId ?? "",
                WeaponClass = weapon.CurrentUsageItem?.WeaponClass ?? WeaponClass.Undefined,
                Mounted = shooterAgent.MountAgent != null,
                Speed = Speed(shooterAgent),
            });
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
    {
        if (!_recording || affectedAgent == null)
        {
            return;
        }
        try
        {
            Agent killer = affectorAgent != null && affectorAgent.IsMount ? affectorAgent.RiderAgent : affectorAgent;
            bool horseVictim = affectedAgent.IsMount;
            _batch.Deaths.Add(new DeathRow
            {
                Time = DateTime.UtcNow,
                Round = _round,
                Victim = horseVictim ? HorseOwner(affectedAgent) : SpawnOf(affectedAgent),
                HorseVictim = horseVictim,
                Killer = killer == affectedAgent ? 0 : SpawnOf(killer),
                State = agentState,
                Attack = !blow.IsValid ? AttackKind.Other
                    : blow.IsMissile ? AttackKind.Missile
                    : blow.AttackType == AgentAttackType.Kick ? AttackKind.Kick
                    : blow.AttackType == AgentAttackType.Bash ? AttackKind.Bash
                    : blow.OverrideKillInfo == Agent.KillInfo.Gravity ? AttackKind.Fall
                    : AttackKind.Other,
                WeaponClass = blow.IsValid ? (WeaponClass)blow.WeaponClass : WeaponClass.Undefined,
                Part = blow.IsValid ? blow.VictimBodyPart : BoneBodyPartType.None,
            });
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Open()
    {
        _match = new MatchInfo
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + MultiplayerOptions.OptionType.Map.GetStrValue(),
            GameType = MultiplayerOptions.OptionType.GameType.GetStrValue(),
            Map = MultiplayerOptions.OptionType.Map.GetStrValue(),
            Culture1 = MultiplayerOptions.OptionType.CultureTeam1.GetStrValue(),
            Culture2 = MultiplayerOptions.OptionType.CultureTeam2.GetStrValue(),
            Server = MultiplayerOptions.OptionType.ServerName.GetStrValue(),
            RoundTotal = MultiplayerOptions.OptionType.RoundTotal.GetIntValue(),
            Start = DateTime.UtcNow,
        };
        _batch = new Batch { Match = _match, Open = true };
        Flush();
        ServerChat.Broadcast("[MPStats] Recording this match's stats.");
    }

    private void Close(string result, bool announce)
    {
        // The round controller normally ends a cut-short round itself first (see the top); if not, end it here.
        if (_recording)
        {
            OnRoundEnding();
        }
        _closed = true;
        _recording = false;
        _match.End = DateTime.UtcNow;
        _match.Result = result;
        _batch.Close = true;
        Flush();
        if (announce)
        {
            ServerChat.Broadcast("[MPStats] Match stats recorded (" + _match.RoundsPlayed + " round" + (_match.RoundsPlayed == 1 ? "" : "s") + ").");
        }
    }

    private void Flush()
    {
        _nextFlush = DateTime.UtcNow.AddSeconds(FlushIntervalSeconds);
        if (_batch.IsEmpty)
        {
            return;
        }
        if (!StatsWriter.Enqueue(_batch))
        {
            _match.DroppedBatches++;
        }
        _batch = new Batch { Match = _match };
    }

    // An error stops recording for this mission instead of reaching the game (each event handler catches its own,
    // without a lambda, so recording allocates nothing per event).
    private void Fail(Exception e)
    {
        if (_failed)
        {
            return;
        }
        _failed = true;
        _recording = false;
        MPStatsSubModule.Log("Stopped recording this match after an error: " + e);
        if (_match != null && !_closed)
        {
            try
            {
                Close("error", announce: false);
            }
            catch (Exception)
            {
                // Already logged; nothing more to save.
            }
        }
    }

    private int SpawnOf(Agent agent) => agent != null && _spawns.TryGetValue(agent, out int spawn) ? spawn : 0;

    private int HorseOwner(Agent horse)
    {
        if (_horseOwners.TryGetValue(horse, out int owner))
        {
            return owner;
        }
        return SpawnOf(horse.RiderAgent);
    }

    // Platform id ("2.0.0.<steam id>"), stable across name changes; the name goes to players.csv once per match.
    private string PlayerId(MissionPeer peer)
    {
        if (peer == null)
        {
            return "";
        }
        if (!_playerIds.TryGetValue(peer, out string id))
        {
            NetworkCommunicator network = peer.GetNetworkPeer();
            id = network?.VirtualPlayer?.Id.ToString() ?? "";
            _playerIds[peer] = id;
            _batch.Players.Add(new PlayerRow { Player = id, Name = network?.UserName ?? peer.Name });
        }
        return id;
    }

    private static AttackKind Kind(Agent affector, in Blow blow, in AttackCollisionData collision)
    {
        if (blow.IsMissile)
        {
            return AttackKind.Missile;
        }
        if (collision.IsHorseCharge)
        {
            return AttackKind.Charge;
        }
        if (collision.IsFallDamage || blow.IsFallDamage)
        {
            return AttackKind.Fall;
        }
        if (blow.AttackType == AgentAttackType.Kick)
        {
            return AttackKind.Kick;
        }
        if (blow.AttackType == AgentAttackType.Bash)
        {
            return AttackKind.Bash;
        }
        if (affector != null && affector.IsHuman && affector.IsDoingPassiveAttack)
        {
            return AttackKind.Couch;
        }
        return blow.StrikeType == StrikeType.Thrust ? AttackKind.Thrust : AttackKind.Swing;
    }

    // Ground speed in m/s; a rider moves at its horse's speed.
    private static float Speed(Agent agent)
    {
        if (agent == null)
        {
            return 0f;
        }
        return (agent.MountAgent ?? agent).Velocity.Length;
    }

    private static int AliveHumans(Team team)
    {
        if (team == null)
        {
            return 0;
        }
        int count = 0;
        foreach (Agent agent in team.ActiveAgents)
        {
            if (agent.IsHuman)
            {
                count++;
            }
        }
        return count;
    }
}
