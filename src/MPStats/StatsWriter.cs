using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPStats;

// Writes match folders from the batches MatchRecorder hands over, on a background thread, so the game never waits
// on the disk. One folder per match in MPS_DATA_DIR (run-server.sh: ~/bannerlord/stats; without it,
// Modules/MPStats/data), one CSV per table; see docs/design.md.
//
// If the writer falls behind (QueueCapacity batches waiting, i.e. minutes of a stuck disk), new batches are dropped
// and counted in match.csv's "dropped" column rather than piling up in memory.
internal static class StatsWriter
{
    private const int QueueCapacity = 600;

    private static readonly BlockingCollection<Batch> Queue = new BlockingCollection<Batch>(QueueCapacity);
    private static readonly Dictionary<string, MatchFiles> Open = new Dictionary<string, MatchFiles>();
    private static int _started;
    private static string _dir;

    private sealed class MatchFiles
    {
        public string Dir;
        public StreamWriter Players, Rounds, Spawns, Hits, Shots, Deaths;

        public IEnumerable<StreamWriter> All => new[] { Players, Rounds, Spawns, Hits, Shots, Deaths };
    }

    public static string Directory
    {
        get
        {
            if (_dir == null)
            {
                string dir = Environment.GetEnvironmentVariable("MPS_DATA_DIR");
                _dir = string.IsNullOrEmpty(dir) ? Path.Combine(BasePath.Name, "Modules", "MPStats", "data") : dir;
            }
            return _dir;
        }
    }

    // False when the batch was dropped.
    public static bool Enqueue(Batch batch)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            new Thread(WriteLoop) { IsBackground = true, Name = "MPStats writer" }.Start();
        }
        return Queue.TryAdd(batch);
    }

    private static void WriteLoop()
    {
        foreach (Batch batch in Queue.GetConsumingEnumerable())
        {
            try
            {
                Write(batch);
            }
            catch (Exception e)
            {
                MPStatsSubModule.Log("Writing match " + batch.Match.Id + " failed: " + e.Message);
            }
        }
    }

    private static void Write(Batch batch)
    {
        MatchInfo match = batch.Match;
        if (batch.Open)
        {
            string dir = Path.Combine(Directory, match.Id);
            System.IO.Directory.CreateDirectory(dir);
            Open[match.Id] = new MatchFiles
            {
                Dir = dir,
                Players = Create(dir, "players.csv", "player,name"),
                Rounds = Create(dir, "rounds.csv", "round,start,fight_start,end,winner,end_reason,morale,attacker_alive,defender_alive"),
                Spawns = Create(dir, "spawns.csv", "spawn,round,time,player,side,class,horse"),
                Hits = Create(dir, "hits.csv", "time,round,attacker,victim,victim_kind,team,attack,damage_type,weapon,weapon_class,part,block,hp,raw,attacker_mounted,victim_mounted,attacker_speed,victim_speed,distance,kill"),
                Shots = Create(dir, "shots.csv", "time,round,shooter,weapon,weapon_class,mounted,speed"),
                Deaths = Create(dir, "deaths.csv", "time,round,victim,victim_kind,killer,state,attack,weapon_class,part"),
            };
            WriteMatch(dir, match);
            MPStatsSubModule.Log("Recording match " + match.Id + " to " + dir + ".");
        }
        if (!Open.TryGetValue(match.Id, out MatchFiles files))
        {
            return;
        }

        StringBuilder line = new StringBuilder(256);
        foreach (PlayerRow r in batch.Players)
        {
            files.Players.WriteLine(Quote(r.Player) + "," + Quote(r.Name));
        }
        foreach (RoundRow r in batch.Rounds)
        {
            line.Clear().Append(r.Round).Append(',').Append(Time(r.Start)).Append(',').Append(r.FightStart.HasValue ? Time(r.FightStart.Value) : "")
                .Append(',').Append(Time(r.End)).Append(',').Append(Side(r.Winner)).Append(',').Append(Reason(r.Reason))
                .Append(',').Append(r.Morale.HasValue ? r.Morale.Value.ToString("0.###", CultureInfo.InvariantCulture) : "")
                .Append(',').Append(r.AttackerAlive).Append(',').Append(r.DefenderAlive);
            files.Rounds.WriteLine(line.ToString());
        }
        foreach (SpawnRow r in batch.Spawns)
        {
            line.Clear().Append(r.Spawn).Append(',').Append(r.Round).Append(',').Append(Time(r.Time)).Append(',').Append(Quote(r.Player))
                .Append(',').Append(Side(r.Side)).Append(',').Append(r.Class).Append(',').Append(r.Horse);
            files.Spawns.WriteLine(line.ToString());
        }
        foreach (HitRow r in batch.Hits)
        {
            line.Clear().Append(Time(r.Time)).Append(',').Append(r.Round).Append(',').Append(Spawn(r.Attacker)).Append(',').Append(Spawn(r.Victim))
                .Append(',').Append(r.HorseVictim ? "horse" : "human").Append(',').Append(r.Team ? 1 : 0)
                .Append(',').Append(Attack(r.Attack)).Append(',').Append(DamageType(r.DamageType))
                .Append(',').Append(r.Weapon).Append(',').Append(WeaponClassName(r.WeaponClass)).Append(',').Append(Part(r.Part))
                .Append(',').Append(Block(r.Block)).Append(',').Append(F(r.Hp)).Append(',').Append(r.Raw)
                .Append(',').Append(r.AttackerMounted ? 1 : 0).Append(',').Append(r.VictimMounted ? 1 : 0)
                .Append(',').Append(F(r.AttackerSpeed)).Append(',').Append(F(r.VictimSpeed))
                .Append(',').Append(r.Distance > 0 ? F(r.Distance) : "").Append(',').Append(r.Kill ? 1 : 0);
            files.Hits.WriteLine(line.ToString());
        }
        foreach (ShotRow r in batch.Shots)
        {
            line.Clear().Append(Time(r.Time)).Append(',').Append(r.Round).Append(',').Append(Spawn(r.Shooter))
                .Append(',').Append(r.Weapon).Append(',').Append(WeaponClassName(r.WeaponClass))
                .Append(',').Append(r.Mounted ? 1 : 0).Append(',').Append(F(r.Speed));
            files.Shots.WriteLine(line.ToString());
        }
        foreach (DeathRow r in batch.Deaths)
        {
            line.Clear().Append(Time(r.Time)).Append(',').Append(r.Round).Append(',').Append(Spawn(r.Victim))
                .Append(',').Append(r.HorseVictim ? "horse" : "human").Append(',').Append(Spawn(r.Killer))
                .Append(',').Append(State(r.State)).Append(',').Append(Attack(r.Attack))
                .Append(',').Append(WeaponClassName(r.WeaponClass)).Append(',').Append(Part(r.Part));
            files.Deaths.WriteLine(line.ToString());
        }
        foreach (StreamWriter writer in files.All)
        {
            writer.Flush();
        }

        if (batch.Close)
        {
            foreach (StreamWriter writer in files.All)
            {
                writer.Dispose();
            }
            Open.Remove(match.Id);
            WriteMatch(files.Dir, match);
            MPStatsSubModule.Log("Match " + match.Id + " recorded: " + match.RoundsPlayed + " rounds, " + match.Result + ".");
        }
    }

    // Rewritten at the end with the end time and result.
    private static void WriteMatch(string dir, MatchInfo m)
    {
        string text = "match,game_type,map,culture1,culture2,start,end,rounds_played,round_total,result,dropped,server\n"
            + m.Id + "," + m.GameType + "," + m.Map + "," + m.Culture1 + "," + m.Culture2 + "," + Time(m.Start) + ","
            + (m.End.HasValue ? Time(m.End.Value) : "") + "," + m.RoundsPlayed + "," + m.RoundTotal + "," + m.Result + ","
            + m.DroppedBatches + "," + Quote(m.Server) + "\n";
        File.WriteAllText(Path.Combine(dir, "match.csv"), text, new UTF8Encoding(false));
    }

    private static StreamWriter Create(string dir, string name, string header)
    {
        StreamWriter writer = new StreamWriter(Path.Combine(dir, name), append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(header);
        return writer;
    }

    private static string Time(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string F(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Spawn(int spawn) => spawn > 0 ? spawn.ToString(CultureInfo.InvariantCulture) : "";

    private static string Quote(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";

    private static string Side(BattleSideEnum side) =>
        side == BattleSideEnum.Attacker ? "attacker" : side == BattleSideEnum.Defender ? "defender" : "none";

    private static string Reason(RoundEndReason reason) => reason switch
    {
        RoundEndReason.SideDepleted => "wiped",
        RoundEndReason.GameModeSpecificEnded => "morale",
        RoundEndReason.RoundTimeEnded => "time",
        _ => "",
    };

    private static string Attack(AttackKind kind) => kind.ToString().ToLowerInvariant();

    private static string Block(BlockKind kind) => kind.ToString().ToLowerInvariant();

    private static string DamageType(DamageTypes type) => type switch
    {
        DamageTypes.Cut => "cut",
        DamageTypes.Pierce => "pierce",
        DamageTypes.Blunt => "blunt",
        _ => "",
    };

    private static string WeaponClassName(WeaponClass weaponClass) => weaponClass == WeaponClass.Undefined ? "" : weaponClass.ToString();

    private static string State(AgentState state) => state switch
    {
        AgentState.Killed => "killed",
        AgentState.Unconscious => "unconscious",
        AgentState.Deleted => "left",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Part(BoneBodyPartType part) => part switch
    {
        BoneBodyPartType.Head => "head",
        BoneBodyPartType.Neck => "neck",
        BoneBodyPartType.Chest => "chest",
        BoneBodyPartType.Abdomen => "abdomen",
        BoneBodyPartType.ShoulderLeft or BoneBodyPartType.ShoulderRight => "shoulder",
        BoneBodyPartType.ArmLeft or BoneBodyPartType.ArmRight => "arm",
        BoneBodyPartType.Legs => "legs",
        _ => "",
    };
}
