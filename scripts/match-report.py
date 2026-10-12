#!/usr/bin/env python3
"""Player performance per match (one mission, all its rounds) from MPStats match records: one Markdown document
with, for every match given, a short summary and a compact table of its players.

Usage:
  python scripts/match-report.py <match folder or folder of matches>... [-o out.md]
  python scripts/match-report.py session/stats/20261012-181653-echerion session/stats/20261012-183451-echerion
  python scripts/match-report.py session/ -o reports/2026-10-12-matches.md     # every match under session/(stats/)
  python scripts/render-report.py reports/2026-10-12-matches.md                 # shareable PDF

One table per team (attackers first), players by score or damage. In the PDF (render-report.py) each match starts
on a new page.

Columns:
  Rounds     rounds the player played (spawned in)
  Inf/Cav/Arc  lives (characters spawned) per class type: infantry (skirmishers included), cavalry, archers
  K / A / D  kills, assists (the game's rule: per kill, the other player with the most damage on that life, if 35+;
             -1 for a teammate), deaths (disconnects not counted); KDA = (K + A) / D
  Dmg        health removed from enemy players (overkill not counted), in total and per round; Taken: from enemies
  Horse      damage to horses; Team: damage to teammates
  Score      the game's scoreboard score (matches recorded with scores.csv only)
  FK / FD    rounds in which the player made the first kill / died first
  Surv.      rounds the player was still alive at the end of
"""
import csv
import glob
import os
import sys
from collections import Counter, defaultdict
from datetime import datetime

FACTIONS = ("aserai", "battania", "empire", "khuzait", "sturgia", "vlandia")


def rd(path):
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(f))


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return 0.0


def class_type(c):
    """Infantry, cavalry or archers ("ranged" classes); skirmishers count as infantry."""
    return "cav" if "cavalry" in c else "arc" if "ranged" in c else "inf"


def seconds(a, b):
    try:
        return (datetime.fromisoformat(b) - datetime.fromisoformat(a)).total_seconds()
    except ValueError:
        return 0.0


def match_folders(paths):
    found = []
    for p in paths:
        if os.path.exists(os.path.join(p, "match.csv")):
            found.append(p)
        else:
            found += [os.path.dirname(m) for m in glob.glob(os.path.join(p, "*", "match.csv"))
                      + glob.glob(os.path.join(p, "stats", "*", "match.csv"))]
    unique = {os.path.abspath(f): f for f in found}
    return sorted(unique.values(), key=lambda f: os.path.basename(os.path.normpath(f)))


def table(headers, rows, align):
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join("---:" if a == "r" else "---" for a in align) + "|"]
    out += ["| " + " | ".join(str(x) for x in r) + " |" for r in rows]
    return "\n".join(out)


def report_match(folder, w, new_page):
    info = rd(os.path.join(folder, "match.csv"))[0]
    rounds = rd(os.path.join(folder, "rounds.csv"))
    spawns = {r["spawn"]: r for r in rd(os.path.join(folder, "spawns.csv"))}
    hits = rd(os.path.join(folder, "hits.csv"))
    deaths = rd(os.path.join(folder, "deaths.csv"))
    names = {r["player"]: r["name"] for r in rd(os.path.join(folder, "players.csv"))}
    score_file = os.path.join(folder, "scores.csv")
    scores = rd(score_file) if os.path.exists(score_file) else None
    side_name = {"attacker": info["culture1"].capitalize(), "defender": info["culture2"].capitalize()}
    pl = lambda spawn: spawns[spawn]["player"] if spawn in spawns else ""
    sd = lambda spawn: spawns[spawn]["side"] if spawn in spawns else ""

    P = defaultdict(Counter)
    classes = defaultdict(Counter)
    side_of = {}
    for s in spawns.values():
        P[s["player"]]["lives"] += 1
        classes[s["player"]][class_type(s["class"])] += 1
        side_of[s["player"]] = s["side"]
    played = defaultdict(set)
    for s in spawns.values():
        played[s["player"]].add(s["round"])

    # Damage, per round (assists need each life's damage).
    life_damage = defaultdict(Counter)
    for h in hits:
        if not h["attacker"] or h["attacker"] == h["victim"]:
            continue
        a, hp = pl(h["attacker"]), num(h["hp"])
        if h["victim_kind"] == "horse":
            P[a]["horse"] += hp
            continue
        if hp > 0:
            life_damage[h["victim"]][a] += hp
        if h["team"] == "1":
            P[a]["team"] += hp
            continue
        P[a]["dmg"] += hp
        P[pl(h["victim"])]["taken"] += hp

    dead = set()
    first = {}
    for d in deaths:
        if d["victim_kind"] != "human" or d["state"] == "left":
            continue
        v = d["victim"]
        dead.add(v)
        P[pl(v)]["deaths"] += 1
        k = d["killer"]
        if k and sd(k) != sd(v):
            P[pl(k)]["kills"] += 1
        killer_player = pl(k) if k else ""
        hitters = [(dmg, p) for p, dmg in life_damage[v].items() if p != killer_player]
        if hitters:
            dmg, p = max(hitters)
            if dmg >= 35:
                P[p]["assists"] += -1 if side_of.get(p) == sd(v) else 1
        if d["round"] not in first or d["time"] < first[d["round"]]["time"]:
            first[d["round"]] = d
    for d in first.values():
        P[pl(d["victim"])]["fd"] += 1
        if d["killer"]:
            P[pl(d["killer"])]["fk"] += 1
    for rnd in rounds:
        alive = {s["player"] for s in spawns.values() if s["round"] == rnd["round"] and s["spawn"] not in dead}
        for p in alive:
            P[p]["survived"] += 1

    # The game's score: each player's last row (cumulative over the match).
    if scores is not None:
        last = {}
        for r in scores:
            if r["player"] not in last or int(r["round"]) >= int(last[r["player"]]["round"]):
                last[r["player"]] = r
        for p, r in last.items():
            P[p]["score"] = int(r["score"])

    # Summary.
    wins = Counter(r["winner"] for r in rounds if r["winner"] in side_name)
    reason = {"wiped": "wiped", "morale": "morale", "time": "time", "aborted": "cut short"}
    sequence = ", ".join("%s %s" % (side_name.get(r["winner"], "draw" if r["end_reason"] != "aborted" else "–"),
                                    reason.get(r["end_reason"], r["end_reason"])) for r in rounds)
    fighting = sum(seconds(r["fight_start"] or r["start"], r["end"]) for r in rounds)
    if new_page:
        w('<div class="page-break"></div>')
        w("")
    w("## %s · %s · %s %d – %d %s" % (info["match"][:15], info["map"], side_name["attacker"], wins["attacker"],
                                       wins["defender"], side_name["defender"]))
    w("")
    w("%s, %s (attackers) vs %s (defenders), %s round%s, %d:%02d of fighting%s. Rounds: %s." % (
        info["game_type"], side_name["attacker"], side_name["defender"], info["rounds_played"],
        "" if info["rounds_played"] == "1" else "s", fighting // 60, fighting % 60,
        "" if info["result"] == "complete" else " (" + (info["result"] or "unfinished") + ")", sequence))
    w("")

    for side in ("attacker", "defender"):
        team = [p for p in side_of if side_of[p] == side]
        if not team:
            continue
        won, lost = wins[side], wins["defender" if side == "attacker" else "attacker"]
        w("### %s (%ss): %s %d–%d" % (side_name[side], side, "won" if won > lost else "lost" if won < lost else "drew",
                                       won, lost))
        w("")
        rows = []
        total = Counter()
        for p in sorted(team, key=lambda p: (-P[p]["score"], -P[p]["dmg"])):
            c = P[p]
            nr = len(played[p])
            total.update(c)
            rows.append((names.get(p, p), nr, classes[p]["inf"] or "", classes[p]["cav"] or "", classes[p]["arc"] or "",
                         c["kills"], c["assists"], c["deaths"], "%.2f" % ((c["kills"] + c["assists"]) / max(1, c["deaths"])),
                         "%.0f" % c["dmg"], "%.0f" % (c["dmg"] / max(1, nr)), "%.0f" % c["taken"], "%.0f" % c["horse"],
                         "%.0f" % c["team"] if c["team"] else "", c["score"] if scores is not None else "",
                         c["fk"] or "", c["fd"] or "", c["survived"] or ""))
        lives = Counter()
        for p in team:
            lives.update(classes[p])
        rows.append(("**Team**", "", lives["inf"] or "", lives["cav"] or "", lives["arc"] or "", total["kills"],
                     total["assists"], total["deaths"], "", "%.0f" % total["dmg"], "", "%.0f" % total["taken"],
                     "%.0f" % total["horse"], "%.0f" % total["team"] if total["team"] else "",
                     total["score"] if scores is not None else "", total["fk"] or "", total["fd"] or "",
                     total["survived"] or ""))
        w(table(["Player", "Rounds", "Inf", "Cav", "Arc", "K", "A", "D", "KDA", "Dmg", "Dmg/round", "Taken", "Horse",
                 "Team", "Score", "FK", "FD", "Surv."], rows, ["l"] + ["r"] * 17))
        w("")


def main():
    args = sys.argv[1:]
    out_path = None
    if "-o" in args:
        i = args.index("-o")
        out_path = args[i + 1]
        del args[i:i + 2]
    if not args:
        sys.exit(__doc__)
    folders = match_folders(args)
    if not folders:
        sys.exit("No match records (folders with match.csv) in: " + " ".join(args))
    lines = []
    w = lines.append
    w("# Matches, %s" % rd(os.path.join(folders[0], "match.csv"))[0]["start"][:10])
    w("")
    w("%d match%s. K/A/D: kills, assists (the game's rule), deaths. Dmg/Taken: health removed from/by enemy "
      "players. Horse: damage to horses. Team: damage to teammates. Score: the game's scoreboard (when recorded). "
      "FK/FD: rounds with the first kill / first death. Surv.: rounds survived. Inf/Cav/Arc: lives spawned as "
      "infantry, cavalry, archers." % (
          len(folders), "" if len(folders) == 1 else "es"))
    w("")
    for i, f in enumerate(folders):
        report_match(f, w, new_page=i > 0)
    text = "\n".join(lines) + "\n"
    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(text)
        print("Wrote " + out_path)
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
