using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// The rows MatchRecorder collects, as plain values: enums and references to strings the game already holds, so
// recording allocates nothing per event. StatsWriter turns them into text off the main thread. Spawn numbers are
// 1-based; 0 means unknown (written as an empty field). See docs/design.md for the columns.

internal enum AttackKind : byte
{
    Other,
    Swing,
    Thrust,
    Couch,
    Missile,
    Kick,
    Bash,
    Charge,
    Fall
}

internal enum BlockKind : byte
{
    None,
    Shield,
    Parry,
    Chamber
}

// One match (mission), from its first round. The recorder sets End, RoundsPlayed and Result before closing it.
internal sealed class MatchInfo
{
    public string Id;
    public string GameType, Map, Scene, Culture1, Culture2, Server;
    public int RoundTotal;
    public DateTime Start;
    public DateTime? End;
    public int RoundsPlayed; // finished rounds (a round cut short by the mission ending isn't counted)
    public string Result = "";
    public int DroppedBatches;
}

internal struct PlayerRow
{
    public string Player, Name;
}

internal struct RoundRow
{
    public int Round;
    public DateTime Start, End;
    public DateTime? FightStart;
    public bool Aborted;
    public BattleSideEnum Winner;
    public RoundEndReason Reason;
    public float? Morale;
    public int AttackerAlive, DefenderAlive;
}

internal struct SpawnRow
{
    public int Spawn, Round;
    public DateTime Time;
    public string Player, Class, Horse;
    public BattleSideEnum Side;
}

internal struct HitRow
{
    public DateTime Time;
    public int Round, Attacker, Victim;
    public bool HorseVictim, Team, AttackerMounted, VictimMounted, Kill;
    public AttackKind Attack;
    public DamageTypes DamageType;
    public string Weapon;
    public WeaponClass WeaponClass;
    public BoneBodyPartType Part;
    public BlockKind Block;
    public float Hp, AttackerSpeed, VictimSpeed, Distance;
    public int Raw;
}

internal struct ShotRow
{
    public DateTime Time;
    public int Round, Shooter;
    public string Weapon;
    public WeaponClass WeaponClass;
    public bool Mounted;
    public float Speed;
}

internal struct DeathRow
{
    public DateTime Time;
    public int Round, Victim, Killer;
    public bool HorseVictim;
    public AgentState State;
    public AttackKind Attack;
    public WeaponClass WeaponClass;
    public BoneBodyPartType Part;
}

// What the main thread hands to the writer at once: rows collected since the last hand-over, and whether the
// match opens or closes with it.
internal sealed class Batch
{
    public MatchInfo Match;
    public bool Open, Close;
    public readonly List<PlayerRow> Players = new List<PlayerRow>();
    public readonly List<RoundRow> Rounds = new List<RoundRow>();
    public readonly List<SpawnRow> Spawns = new List<SpawnRow>();
    public readonly List<HitRow> Hits = new List<HitRow>();
    public readonly List<ShotRow> Shots = new List<ShotRow>();
    public readonly List<DeathRow> Deaths = new List<DeathRow>();

    public bool IsEmpty => !Open && !Close && Players.Count == 0 && Rounds.Count == 0 && Spawns.Count == 0
        && Hits.Count == 0 && Shots.Count == 0 && Deaths.Count == 0;
}
