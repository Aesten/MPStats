# MPStats

Server-side stat tracking for the Bannerlord Dedicated Server (v1.4.8). Nothing to install on clients
(`ModuleCategory` Server).

## Groupfight (Battle): round stats

At the end of each Battle round, everyone gets one chat line per player who spawned that round:

```
Round 3:
Player1: d=65, td=21, HP=22
Player2: d=40, td=0, HP=0
```

- `d`: damage dealt to enemies (players and bots).
- `td`: damage dealt to teammates (self-damage doesn't count).
- `HP`: health left when the round ended (0 if dead or disconnected).

Damage is the health actually removed (a 120 hit on a 40 HP enemy counts 40). Hits on horses don't count; a
horse's charge counts for its rider. Attackers are listed first, then defenders, each by damage dealt. The lines
also go to the server log (`[MPStats]`). Only Battle missions get the tracker; other modes are untouched.

## Skirmish: match records (planned)

Stats over a whole Skirmish game (one mission), saved as records. Not started; two existing stat trackers will
show what data is needed.

## Performance

Stats code runs on the server's main thread for every hit, so it follows mpManager's rules (`docs/roadmap.md`,
"Performance"): per hit only lookups and additions, text built and sent at round end, and only agents still
in the mission's active list are read (on the Linux server a null or freed access is a native crash).

## Build and deploy

```sh
dotnet build src/MPStats -c Release   # into dist/Modules/MPStats
scripts/deploy.sh                     # build and install on the server (aesten@7900x)
scripts/deploy.sh --restart           # and restart it
```

mpManager's `run-server.sh` loads MPStats when it's installed (`--no-stats` to leave it out).
