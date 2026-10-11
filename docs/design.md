# MPStats: match event records (design)

## Principles

- **The server records events, nothing else.** No stats are computed on the server. Scripts turn the raw events
  into stats later. Players only see two chat lines: "[MPStats] Recording this match's stats." when the first
  round starts, and "[MPStats] Match stats recorded (5 rounds)." when the mission ends.
- **Raw events are the source of truth.** Any stat thought of later can be computed from matches already
  recorded, so the event fields matter more than any list of stats.
- **Skirmish and Battle (groupfight) only.** No TDM.
- **Only real rounds count.** Recording starts when a round starts (preparation included, since that's when
  players spawn) and stops when it ends. Warmup never runs rounds (the round controller is idle while the lobby
  is in warmup), so warmup hits, kills and spawns are never recorded, and a mission that never leaves warmup
  writes nothing.
- **A match is one mission.** Each round's events go to the match's files as the match runs; when the mission
  ends, the match record is closed and marked complete or aborted.
- **Final destination: a database.** Files are the interchange format: an import script loads finished matches
  into the database, raw rows as they are, and the computed stats are cached in their own tables.

## Cost on the server

Per event, combat code only appends a struct of numbers and references to a list on the main thread: no
strings built, no disk access, no locks. Once a second (and at round end) the lists are handed over to a
background thread, which formats and writes them; the hand-over is the only allocation. This follows mpManager's
`docs/roadmap.md` ("Performance"). Volume for comparison: mpbalancer's per-hit CSV for a 2.5 h, 4-player
session was 37 KB gzipped.

## Files

One folder per match, in `MPS_DATA_DIR` (`~/bannerlord/stats/` on the server; without it,
`Modules/MPStats/data/`):

```
stats/20261011-213000-mp_skirmish_map_007/
  match.csv     players.csv   rounds.csv   spawns.csv   hits.csv   shots.csv   deaths.csv
```

Unlike logs and metrics, stats are never deleted by the server: they are kept until imported.

Each file is a database table (CSV with a header); every row carries the match id implicitly (its folder).
Times are UTC (`2026-10-11T21:30:05.123`).

### match.csv (one row)

| column | |
|---|---|
| `match` | folder name: start time and map |
| `game_type` | `Skirmish` / `Battle` |
| `map`, `culture1`, `culture2` | attackers' and defenders' factions |
| `start`, `end` | first round start; last round end (empty until the match closes) |
| `rounds_played`, `round_total` | |
| `result` | `complete` (the game ended the match: all rounds, a team out of reach, or a team left), `aborted` (ended by an admin or MPManager: !setmap, !resetmap, !mode, !warmup, standby), `error` (the recorder hit an error and stopped; logged); empty if the server stopped mid-match |
| `dropped` | batches (about a second of events each) lost because the writer fell behind; normally 0 |
| `server` | server name |

Written when the first round starts and rewritten when the match closes.

### players.csv

`player` (platform id, e.g. the Steam id: stable across name changes), `name` (as of this match).

### rounds.csv

`round`, `start`, `fight_start` (preparation over), `end`, `winner` (`attacker`/`defender`/`none` = draw),
`end_reason`, `morale` (the morale bar at the end, −1 = defenders full to 1 = attackers full), `attacker_alive`,
`defender_alive`.

`end_reason`, as the game decides it (`MissionMultiplayerFlagDomination`, used by Skirmish and Battle):

| `end_reason` | game's reason | |
|---|---|---|
| `wiped` | `SideDepleted` | the other side has nobody alive |
| `morale` | `GameModeSpecificEnded` | a side's morale bar filled up (flags) |
| `time` | `RoundTimeEnded` | time ran out: the side ahead on morale wins, `none` if even |

A round cut short by an admin (see below) is recorded as `time`.

### spawns.csv: one row per life

| column | |
|---|---|
| `spawn` | number of this life within the match; hits and deaths refer to it |
| `round`, `time` | |
| `player` | empty for bots |
| `side` | `attacker` / `defender` |
| `class` | troop id (e.g. `mp_heavy_infantry_vlandia_troop`); class cost and type come from the game's data |
| `horse` | horse item id, empty on foot |

### hits.csv: one row per hit on an agent, damaging or blocked

| column | |
|---|---|
| `time`, `round` | |
| `attacker`, `victim` | spawn numbers (a hit on a horse: its rider's spawn number, or empty if riderless) |
| `victim_kind` | `human` / `horse` |
| `team` | 1 if attacker and victim are on the same side |
| `attack` | `swing`, `thrust`, `couch`, `missile`, `kick`, `bash`, `charge` (horse bump), `fall` |
| `damage_type` | `cut` / `pierce` / `blunt` |
| `weapon`, `weapon_class` | item id and the game's weapon class (`OneHandedSword`, `Arrow`, `Javelin`...); for missiles, the ammunition (arrow, bolt, javelin), not the bow |
| `part` | body part hit (`head`, `neck`, `chest`, `abdomen`, `shoulder`, `arm`, `legs`) |
| `block` | `none`, `shield`, `parry`, `chamber` |
| `hp` | health removed (at most what the victim had left); 0 when blocked |
| `raw` | damage the blow would have dealt (unclamped), i.e. overkill = raw − hp |
| `attacker_mounted`, `victim_mounted` | 0/1 |
| `attacker_speed`, `victim_speed` | m/s; for riders, the horse's speed |
| `distance` | missiles: distance from where it was shot, in m |
| `kill` | 1 if this hit killed |

Melee "misses" (swings that hit nothing) have no game event and aren't recorded. Missiles that hit nothing come
from `shots.csv` minus hits.

### shots.csv: one row per missile shot or thrown

`time`, `round`, `shooter` (spawn), `weapon`, `weapon_class`, `mounted`, `speed` (shooter's speed).
The weapon is the one wielded: the bow or crossbow (`Bow`, `Crossbow`), or the thrown weapon itself. Accuracy =
the shooter's missile hits on agents / shots in the same round.

### deaths.csv: one row per human or horse death

`time`, `round`, `victim` (spawn; for a horse, its rider's spawn or empty), `victim_kind`, `killer` (spawn,
empty for falls and suicides), `state` (`killed` / `unconscious`), `attack` (`missile`, `kick`, `bash`, `fall`,
otherwise `other`: the killing hit's row in `hits.csv` has the details), `weapon_class`, `part`.
Disconnects during a round are rows with `state` = `left`.

## Match and round boundaries

How each way a round or match ends is recorded. In round-based modes (Skirmish, Battle) only two things end a
mission: the round controller once the match is over, or `MissionLobbyComponent.SetStateEndingAsServer` (what
MPManager's `MissionFlow.Restart` calls). The map time limit doesn't apply when there is a round controller.

| What happens | Recorded as |
|---|---|
| Warmup, `!endwarmup`, `!ready`, a cancelled countdown | nothing: no round has started |
| Round ends normally | round row with winner and reason |
| Last round, a team out of reach, or a team left | last round row, then `result` = `complete` |
| `!setmap` / `!resetmap` / `!mode` / `!warmup` / standby during a round | that round ends as `time`, `result` = `aborted` |
| The same between rounds (end screen of a round, next preparation) | `result` = `aborted`; rounds already recorded stay |
| Player leaves mid-round | death row with `state` = `left`; their earlier rows stay |
| Player rejoins | same `player`, a new spawn |
| Server crash or `mpm stop` | rows up to the last second are on disk; `match.csv` has no end or result |

**Nothing left dangling.** `MissionFlow.Restart` sets the mission timer to 0 to skip the end screen, and the round
controller shares that timer: a round in progress sees its time as over and ends through the normal round-end
event (`time`, winner by morale), so every started round gets its row. When the mission closes, the recorder
writes `match.csv` with its result, flushes and closes the files, whatever state the round was in; a mission
closing without a round end event (e.g. between rounds) only closes the match. The agents removed when the next
round resets the mission are not deaths: nothing is recorded between a round's end and the next round's start.

**No harm to the server:**

- Only reads game state, never changes it; subscribes to the round controller's events and unsubscribes when the
  mission ends (a new recorder per mission, so nothing carries over).
- Null checks before every agent access (falls and environment damage have no attacker; riderless horses no
  rider): on the Linux server a null access in native code is a crash, not an exception. Agents are only
  identified by reference through a spawn table filled at spawn, never read after removal.
- Managed errors are caught: an error in the recorder turns recording off for the mission and is logged once;
  the game goes on.
- The writer thread never blocks the game: if it falls behind or the disk fails, batches are dropped (and the
  match marked as incomplete) instead of piling up in memory.

## What this covers (examples)

- Kills, deaths and assists; assists by any rule wanted (e.g. damage to the victim in that life by others).
- Damage dealt/taken per player, per class, per weapon; damage per life; lifespan; overkill.
- First death of a round, trades (killer dies within N s), clutches (last alive and the round won).
- Team damage dealt and received.
- Archers and throwers: shots, hits, shield hits, accuracy, body-part map, distance distribution, hits on
  moving or mounted targets.
- Cavalry: attacker speed at each couch, thrust and swing (the speed distribution and damage vs speed), horses
  lost, bump damage.
- Melee: hits landed vs blocked by the target (shield, parry, chamber), kicks and bashes.
- Matchups (nemesis, favourite victims), class picks per faction and map, round outcomes.
- Balance analysis: match times line up with mpbalancer's `settings-<start>.csv` (which balance configuration
  was live during each match).

## Open points

- **Import and database:** SQLite to start (one file, no server, enough for years of matches), with an
  `import.py` that loads finished match folders and a `stats` script that fills the cached-stat tables. Moving
  to Postgres later doesn't change the files.
- **mpbalancer's hit CSV** overlaps with `hits.csv`; keep both for now (different purposes, each mod works on its
  own). It could later shrink to the balance-specific columns.
- **Groupfight's chat lines** (`GroupfightRoundStats`) stay as they are: live display, separate from records.
