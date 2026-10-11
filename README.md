# MPStats

Server-side stat tracking for the Bannerlord Dedicated Server (v1.4.8). Nothing to install on clients
(`ModuleCategory` Server).

## Groupfight (Battle): round stats

At the end of each Battle round, everyone gets one chat line per player who spawned that round:

```
Round 3:
Player1 | DMG: 65 | TeamDMG: 21 | HP: 22
Player2 | DMG: 40 | TeamDMG: 0 | HP: 0
```

- `DMG`: damage dealt to enemies (players and bots).
- `TeamDMG`: damage dealt to teammates (self-damage doesn't count).
- `HP`: health left when the round ended (0 if dead or disconnected).

Damage is the health actually removed (a 120 hit on a 40 HP enemy counts 40). Hits on horses don't count; a
horse's charge counts for its rider. Attackers are listed first, then defenders, each by damage dealt. The lines
also go to the server log (`[MPStats]`). Only Battle missions get the tracker; other modes are untouched.

## Match records (Skirmish and Battle)

Every Skirmish and Battle match (one mission) is recorded as raw events: spawns, hits (damaging or blocked),
missiles shot, deaths and rounds, one CSV per table in a folder per match (`~/bannerlord/stats/<start>-<map>/`
on the server, `MPS_DATA_DIR`). Only real rounds count: warmup records nothing. No stats are computed on the
server; scripts compute them from the records later, and the records are meant to be imported into a database.
Players see one chat line when recording starts (first round) and one when the match's stats are saved.

Columns, how each way a match can end is recorded, and the safety rules: `docs/design.md`.

## Performance

Stats code runs on the server's main thread for every hit, so it follows mpManager's rules (`docs/roadmap.md`,
"Performance"): per event only lookups, additions and appending a struct to a list (no text, no disk, no
allocation); the match records are formatted and written by a background thread once a second; only agents the
game hands over are read (on the Linux server a null or freed access is a native crash). An error stops
recording for that match (logged) and never reaches the game.

## Build and deploy

```sh
dotnet build src/MPStats -c Release   # into dist/Modules/MPStats
scripts/deploy.sh                     # build and install on the server (aesten@7900x)
scripts/deploy.sh --restart           # and restart it
```

`run-server.sh` (in the sibling mpServerConfig repo) loads MPStats by default (`--no-stats`, `--manager-only` or `--vanilla` leave it out).
