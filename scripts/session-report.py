#!/usr/bin/env python3
"""Writes a Markdown report of a play session: server performance, each player's connection, damage compared with
vanilla, and player performance. It only reads files; nothing is computed on the server.

Inputs (copied from the server first):
  stats/<match>/            MPStats match records (spawns, hits, shots, deaths, rounds, players, match)
  metrics/server-*.csv      MPManager server metrics          (optional)
  metrics/players-*.csv     MPManager per-player network      (optional)
  metrics/hits-*.csv        MPBalancer hit log with vanilla damage (optional)

Usage:
  mkdir session && cd session
  ssh bannerlord@7900x 'cd ~/bannerlord/instances/0 && tar -cf - stats/20261012-* metrics/*-20261012-*.csv' | tar -xf -
  python path/to/session-report.py . > report.md

Options: --title "..." (default: the session's date).
"""
import csv
import glob
import os
import statistics as st
import sys
from collections import Counter, defaultdict

# ---------------------------------------------------------------------------------------------------- loading


def rd(path):
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(f))


def num(x, default=0.0):
    try:
        return float(x)
    except (TypeError, ValueError):
        return default


def pct(values, q):
    v = sorted(values)
    return v[min(len(v) - 1, int(len(v) * q))] if v else 0.0


FACTIONS = ("aserai", "battania", "empire", "khuzait", "sturgia", "vlandia")


def short_class(c):
    """"mp_light_cavalry_vlandia_hero" -> "light_cavalry" (the class, whatever the faction)."""
    c = c.replace("mp_", "").replace("_hero", "")
    for f in FACTIONS:
        c = c.replace("_" + f, "")
    return c


def role(c):
    """Class category: cavalry, ranged, skirmisher, infantry."""
    for k in ("cavalry", "ranged", "skirmisher", "infantry"):
        if k in c:
            return k
    return "other"


def table(headers, rows, align=None):
    align = align or ["l"] + ["r"] * (len(headers) - 1)
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join("---:" if a == "r" else "---" for a in align) + "|"]
    for r in rows:
        out.append("| " + " | ".join(str(x) for x in r) + " |")
    return "\n".join(out)


def f0(x):
    return "%.0f" % x


def f1(x):
    return "%.1f" % x


class Match:
    def __init__(self, folder):
        self.id = os.path.basename(folder.rstrip("/\\"))
        self.info = rd(os.path.join(folder, "match.csv"))[0]
        self.rounds = rd(os.path.join(folder, "rounds.csv"))
        self.spawns = {r["spawn"]: r for r in rd(os.path.join(folder, "spawns.csv"))}
        self.hits = rd(os.path.join(folder, "hits.csv"))
        self.shots = rd(os.path.join(folder, "shots.csv"))
        self.deaths = rd(os.path.join(folder, "deaths.csv"))
        self.names = {r["player"]: r["name"] for r in rd(os.path.join(folder, "players.csv"))}
        scores = os.path.join(folder, "scores.csv")
        self.scores = rd(scores) if os.path.exists(scores) else None

    def player(self, spawn):
        s = self.spawns.get(spawn)
        return s["player"] if s else ""

    def side(self, spawn):
        s = self.spawns.get(spawn)
        return s["side"] if s else ""


# ---------------------------------------------------------------------------------------------------- report


def main():
    args = sys.argv[1:]
    title = None
    if "--title" in args:
        i = args.index("--title")
        title = args[i + 1]
        del args[i:i + 2]
    root = args[0] if args else "."
    matches = [Match(d) for d in sorted(glob.glob(os.path.join(root, "stats", "*", "")))
               if os.path.exists(os.path.join(d, "match.csv"))]
    server = [r for f in sorted(glob.glob(os.path.join(root, "metrics", "server-*.csv"))) for r in rd(f)]
    network = [r for f in sorted(glob.glob(os.path.join(root, "metrics", "players-*.csv"))) for r in rd(f)]
    balance = [r for f in sorted(glob.glob(os.path.join(root, "metrics", "hits-*.csv"))) for r in rd(f)]
    if not matches:
        sys.exit("No match records in %s/stats/." % root)

    names = {}
    for m in matches:
        names.update(m.names)
    for r in network:
        if r.get("player_id"):
            names.setdefault(r["player_id"], r["player"])
    name = lambda pid: names.get(pid, pid or "?")

    # Round windows (UTC strings compare as times), for keeping warmup out of the server and balance data.
    windows = [(r["start"], r["end"]) for m in matches for r in m.rounds]
    in_round = lambda t: any(a <= t <= b for a, b in windows)
    session_start = min(m.info["start"] for m in matches)
    session_end = max(m.info["end"] or m.info["start"] for m in matches)

    out = []
    w = out.append
    w("# %s" % (title or "Session report, " + session_start[:10]))
    w("")
    w("%d matches, %d rounds, %s–%s UTC." % (len(matches), sum(len(m.rounds) for m in matches),
                                              session_start[11:16], session_end[11:16]))
    w("")
    w(table(["Match", "Map", "Factions (att. vs def.)", "Result", "Rounds: winner / how"],
            [(m.id[:15], m.info["map"], "%s vs %s" % (m.info["culture1"], m.info["culture2"]), m.info["result"],
              " · ".join("%s %s/%s" % (r["round"], r["winner"][:3], r["end_reason"]) for r in m.rounds))
             for m in matches], ["l", "l", "l", "l", "l"]))
    w("")

    # ------------------------------------------------------------------------------------------ server
    w("## Server performance")
    w("")
    if server:
        play = [r for r in server if r["state"] == "playing" and session_start <= r["time"] <= session_end]
        g = lambda rows, k: [num(r[k]) for r in rows]
        target = num(server[-1]["target_hz"])
        w("Tick rate target %.0f Hz (%.2f ms per tick). Rows are 10 s windows while a mission was playing." % (
            target, 1000 / target if target else 0))
        w("")
        rows = []
        for m in matches:
            mr = [r for r in play if m.info["start"] <= r["time"] <= (m.info["end"] or m.info["start"])]
            if mr:
                rows.append((m.id[:15], f0(max(g(mr, "players"))), f0(max(g(mr, "agents"))), f1(min(g(mr, "hz"))),
                             "%.2f" % st.median(g(mr, "tick_p99_ms")), "%.2f" % max(g(mr, "tick_p99_ms")),
                             f1(max(g(mr, "tick_max_ms"))), "%.2f" % max(g(mr, "late_pct")),
                             f0(st.median(g(mr, "cpu_pct"))), f0(max(g(mr, "mem_mb")))))
        w(table(["Match", "Players", "Agents", "Min Hz", "Tick p99 (median)", "Tick p99 (worst)", "Worst tick ms",
                 "Late % (worst)", "CPU % (median)", "Memory MB"], rows))
        w("")
        spikes = [r for r in server if num(r["tick_max_ms"]) > 2.5 * (1000 / target if target else 7)
                  and session_start <= r["time"] <= session_end]
        if spikes:
            w("Ticks over 2.5× the target interval:")
            w("")
            w(table(["Time", "State", "Players", "Worst tick ms", "Late %"],
                    [(r["time"][11:19], r["state"], r["players"], r["tick_max_ms"], r["late_pct"]) for r in spikes],
                    ["l", "l", "r", "r", "r"]))
            w("")
            w("`none`/`waitingfirstplayers` = between missions or loading a map; `playing` = during a match.")
            w("")
    else:
        w("No server metrics given.")
        w("")

    # ------------------------------------------------------------------------------------------ network
    w("## Player connections")
    w("")
    if network:
        played = {s["player"] for m in matches for s in m.spawns.values()}
        spike_windows = {r["time"] for r in server if num(r["tick_max_ms"]) > 2.5 * (1000 / num(r["target_hz"]))}
        by = defaultdict(list)
        for r in network:
            if session_start[:16] <= r["time"][:16] <= session_end[:16] or not matches:
                by[r.get("player_id") or r["player"]].append(r)
        rows = []
        for pid, rs in sorted(by.items(), key=lambda x: st.median(num(r["ping_avg_ms"]) for r in x[1])):
            avg = [num(r["ping_avg_ms"]) for r in rs]
            jit = [num(r["ping_jitter_ms"]) for r in rs]
            loss = [num(r["loss_pct"]) for r in rs if r["loss_pct"] != ""]
            med = st.median(avg)
            bad = [r for r in rs if (r["loss_pct"] != "" and num(r["loss_pct"]) > 1) or num(r["ping_max_ms"]) > med + 50]
            bad_on_spike = sum(1 for r in bad if r["time"] in spike_windows)
            rows.append((name(pid) if pid in names else rs[0]["player"], "yes" if pid in played else "spectator",
                         f0(len(rs) / 6), f1(med), f1(pct(avg, .95)), f0(max(num(r["ping_max_ms"]) for r in rs)),
                         f1(st.median(jit)), f1(pct(jit, .95)),
                         "%.2f" % (sum(loss) / len(loss) if loss else 0), f1(max(loss or [0])),
                         "%d (%d%%)" % (len(bad), 100 * len(bad) / len(rs)), bad_on_spike))
        w("Session time, from MPManager's per-player rows (10 s windows; ping sampled every second). "
          "A bad window has loss over 1% or a ping spike over 50 ms above the player's median; the last column "
          "counts bad windows that fell on a server stutter (if 0, the problem was the connection, not the server).")
        w("")
        w(table(["Player", "Played", "Minutes", "Ping ms", "Ping p95", "Worst", "Jitter", "Jitter p95",
                 "Loss % avg", "Loss % max", "Bad windows", "On server stutter"], rows,
                ["l", "l"] + ["r"] * 10))
        w("")
    else:
        w("No network metrics given.")
        w("")

    # ------------------------------------------------------------------------------------------ damage vs vanilla
    w("## Damage compared with vanilla")
    w("")
    if balance:
        bal = [r for r in balance if in_round(r["time"])]
        w("From MPBalancer's hit log (damaging hits only, rounds only: warmup left out). `vanilla` is what the same "
          "hit would have dealt without MPBalancer, `dealt` what it dealt (before capping at the victim's health).")
        w("")
        groups = defaultdict(list)
        for r in bal:
            src = r["src"]
            if src == "thrust":
                src = "thrust (mounted)" if r["mounted"] == "1" else "thrust (on foot)"
            groups[(r["type"], src, r["victim"])].append(r)
        rows = []
        for (t, src, v), rs in sorted(groups.items(), key=lambda x: (x[0][0] != "pierce", -len(x[1]))):
            van = sum(num(r["vanilla"]) for r in rs)
            dealt = sum(num(r["after"]) for r in rs)
            rows.append((t, src, v, len(rs), f1(van / len(rs)), f1(dealt / len(rs)), f0(van), f0(dealt),
                         "%+.0f%%" % (100 * (dealt / van - 1)) if van else ""))
        w(table(["Type", "Source", "Victim", "Hits", "Vanilla avg", "Dealt avg", "Vanilla total", "Dealt total",
                 "Change"], rows, ["l", "l", "l"] + ["r"] * 6))
        w("")
        w("Pierce thrusts on players by relative speed (attacker vs victim):")
        w("")
        rows = []
        for lo, hi, label in ((0, 4, "0–4"), (4, 8, "4–8"), (8, 12, "8–12"), (12, 16, "12–16"), (16, 999, "16+")):
            for mounted in ("1", "0"):
                rs = [r for r in bal if r["type"] == "pierce" and r["src"] == "thrust" and r["victim"] == "human"
                      and r["mounted"] == mounted and lo <= num(r["speed"]) < hi]
                if rs:
                    van = sum(num(r["vanilla"]) for r in rs)
                    dealt = sum(num(r["after"]) for r in rs)
                    rows.append((label + " m/s", "mounted" if mounted == "1" else "on foot", len(rs),
                                 f1(van / len(rs)), f1(dealt / len(rs)), "%+.0f%%" % (100 * (dealt / van - 1))))
        w(table(["Speed", "Attacker", "Hits", "Vanilla avg", "Dealt avg", "Change"], rows, ["l", "l", "r", "r", "r", "r"]))
        w("")
        w("Pierce damage on players by weapon (10+ hits):")
        w("")
        weap = defaultdict(list)
        for r in bal:
            if r["type"] == "pierce" and r["victim"] == "human":
                weap[r["weapon"]].append(r)
        rows = []
        for wpn, rs in sorted(weap.items(), key=lambda x: -len(x[1])):
            if len(rs) >= 10:
                van = sum(num(r["vanilla"]) for r in rs)
                dealt = sum(num(r["after"]) for r in rs)
                rows.append((wpn.replace("mp_", ""), rs[0]["src"], len(rs), f1(van / len(rs)), f1(dealt / len(rs)),
                             "%+.0f%%" % (100 * (dealt / van - 1))))
        w(table(["Weapon", "Source", "Hits", "Vanilla avg", "Dealt avg", "Change"], rows, ["l", "l", "r", "r", "r", "r"]))
        w("")
    else:
        w("No MPBalancer hit log given.")
        w("")

    # ------------------------------------------------------------------------------------------ players
    P = defaultdict(Counter)
    classes = defaultdict(Counter)
    rounds_played = defaultdict(set)
    vanilla_thrust = defaultdict(lambda: [0.0, 0.0, 0])  # player -> vanilla, dealt, hits (joined with MPBalancer)
    bal_index = defaultdict(list)
    for r in balance:
        bal_index[r["time"][:19]].append(r)
    joined = attempted = 0
    team_by_kind = Counter()

    for m in matches:
        for s in m.spawns.values():
            pid = s["player"]
            P[pid]["lives"] += 1
            classes[pid][short_class(s["class"])] += 1
            rounds_played[pid].add((m.id, s["round"]))
        # Damage each life took from each player (enemies and teammates), for assists.
        life_damage = defaultdict(Counter)
        for h in m.hits:
            a, v = h["attacker"], h["victim"]
            hp = num(h["hp"])
            ap = m.player(a)
            if not a or a == v:
                continue
            arole = role(m.spawns[a]["class"]) if a in m.spawns else ""
            if h["victim_kind"] == "horse":
                P[ap]["horse_dmg"] += hp
                continue
            vp = m.player(v)
            if hp > 0:
                life_damage[v][ap] += hp
            if h["team"] == "1":
                P[ap]["team_hits"] += 1
                P[ap]["team_dmg"] += hp
                P[vp]["team_taken"] += hp
                team_by_kind[("mounted " if h["attacker_mounted"] == "1" else "") + h["attack"]] += hp
                continue
            P[ap]["dmg"] += hp
            P[vp]["taken"] += hp
            P[ap]["dmg_" + arole] += hp
            if v in m.spawns:
                P[vp]["taken_" + role(m.spawns[v]["class"])] += hp
            if h["attack"] == "missile":
                P[ap]["m_hits"] += 1
                if h["block"] != "none":
                    P[ap]["m_blocked"] += 1
                else:
                    P[ap]["m_dmg"] += hp
                    P[ap]["m_dist"] += num(h["distance"])
                    if h["part"] in ("head", "neck"):
                        P[ap]["m_head"] += 1
            if h["attacker_mounted"] == "1" and h["attack"] in ("thrust", "couch") and hp > 0:
                P[ap]["cav_hits"] += 1
                P[ap]["cav_speed"] += num(h["attacker_speed"])
                if h["attack"] == "couch":
                    P[ap]["couches"] += 1
            if h["attack"] == "charge":
                P[ap]["bumps"] += 1
            # Vanilla comparison for thrusts: the MPBalancer row of the same hit (same second, weapon and body part,
            # and damage before capping equal to MPStats' raw).
            if h["attack"] in ("thrust", "couch") and h["block"] == "none" and num(h["raw"]) > 0:
                attempted += 1
                cands = [r for r in bal_index.get(h["time"][:19], []) if r["weapon"] == h["weapon"]
                         and r["part"] == h["part"] and abs(num(r["after"]) - num(h["raw"])) <= 1]
                if cands:
                    joined += 1
                    vt = vanilla_thrust[ap]
                    vt[0] += num(cands[0]["vanilla"])
                    vt[1] += num(cands[0]["after"])
                    vt[2] += 1
        for s in m.shots:
            P[m.player(s["shooter"])]["shots"] += 1
        # The game's scoreboard: each player's last row of the match (values are cumulative over the match).
        if m.scores is not None:
            last = {}
            for r in m.scores:
                if r["player"] not in last or int(r["round"]) >= int(last[r["player"]]["round"]):
                    last[r["player"]] = r
            for pid, r in last.items():
                P[pid]["score"] += int(r["score"])
                P[pid]["game_kills"] += int(r["kills"])
                P[pid]["game_deaths"] += int(r["deaths"])
                P[pid]["game_assists"] += int(r["assists"])
            P["__scored_matches"]["n"] += 1
        # Deaths, kills, assists, first death of each round.
        first = {}
        for d in m.deaths:
            if d["victim_kind"] != "human":
                if d["victim_kind"] == "horse":
                    P[m.player(d["victim"])]["horses_lost"] += 1
                continue
            vp = m.player(d["victim"])
            if d["state"] in ("left",):
                P[vp]["left"] += 1
                continue
            P[vp]["deaths"] += 1
            k = d["killer"]
            if k and m.side(k) == m.side(d["victim"]):
                P[m.player(k)]["team_kills"] += 1
            elif k:
                P[m.player(k)]["kills"] += 1
            # The game's rule (Agent.GetAssistingHitter): one assist per kill, to whoever but the killer did the
            # most damage to this life, if at least 35; -1 instead if that was a teammate of the victim.
            killer_player = m.player(k) if k else ""
            hitters = [(dmg, pl) for pl, dmg in life_damage[d["victim"]].items() if pl != killer_player]
            if hitters:
                dmg, pl = max(hitters)
                if dmg >= 35:
                    friendly = any(s["player"] == pl and s["side"] == m.side(d["victim"]) for s in m.spawns.values()
                                   if s["round"] == d["round"])
                    P[pl]["assists"] += -1 if friendly else 1
            if d["round"] not in first or d["time"] < first[d["round"]]["time"]:
                first[d["round"]] = d
        for d in first.values():
            P[m.player(d["victim"])]["first_death"] += 1
            if d["killer"]:
                P[m.player(d["killer"])]["first_kill"] += 1

    players = [p for p in P if p and not p.startswith("__")]
    scored = P["__scored_matches"]["n"]
    main_class = lambda p: ", ".join("%s ×%d" % (c, n) for c, n in classes[p].most_common(2))

    w("## Player performance")
    w("")
    w("From MPStats' match records. Damage is the health actually removed (overkill not counted), on players only "
      "unless marked horses. A kill is credited to whoever landed the killing hit; assists follow the game's "
      "rule: per kill, the other player who did the most damage to that life, if 35 or more (-1 if a teammate). KDA = (kills + assists) / deaths. First death: died first in a "
      "round. Score is the game's scoreboard score (recorded since MPStats keeps scores.csv: %d of %d matches here)."
      % (scored, len(matches)))
    w("")
    rows = []
    for p in sorted(players, key=lambda p: (-P[p]["score"], -P[p]["dmg"] / max(1, len(rounds_played[p])))):
        c = P[p]
        nr = len(rounds_played[p])
        rows.append((name(p), main_class(p), nr, c["score"] if scored else "", f0(c["score"] / max(1, nr)) if scored else "",
                     c["lives"], c["kills"], c["deaths"], c["assists"],
                     "%.2f" % ((c["kills"] + c["assists"]) / max(1, c["deaths"])), f0(c["dmg"]),
                     f0(c["dmg"] / max(1, nr)), f0(c["taken"]), "%.2f" % (c["dmg"] / max(1, c["taken"])),
                     f0(c["horse_dmg"]), c["first_death"], c["first_kill"]))
    w(table(["Player", "Main classes", "Rounds", "Score", "Score/round", "Lives", "K", "D", "A", "KDA", "Damage",
             "Dmg/round", "Taken", "Dealt/taken", "Horse dmg", "First death", "First kill"], rows, ["l", "l"] + ["r"] * 15))
    if scored:
        off = [(name(p), "%d/%d/%d" % (P[p]["kills"], P[p]["deaths"], P[p]["assists"]),
                "%d/%d/%d" % (P[p]["game_kills"], P[p]["game_deaths"], P[p]["game_assists"]))
               for p in players if (P[p]["game_kills"], P[p]["game_deaths"], P[p]["game_assists"])
               != (P[p]["kills"], P[p]["deaths"], P[p]["assists"]) and P[p]["score"]]
        if off:
            w("")
            w("K/D/A computed from the events vs the game's scoreboard, where they differ (the scoreboard also "
              "counts team switches as deaths and team kills against the killer):")
            w("")
            w(table(["Player", "From events", "Scoreboard"], off, ["l", "r", "r"]))
    w("")

    # By role.
    R = defaultdict(Counter)
    for m in matches:
        dead = {d["victim"] for d in m.deaths if d["victim_kind"] == "human" and d["state"] != "left"}
        for s in m.spawns.values():
            R[role(s["class"])]["lives"] += 1
            R[role(s["class"])]["deaths"] += s["spawn"] in dead
        for h in m.hits:
            if not h["attacker"] or h["attacker"] == h["victim"] or h["team"] == "1":
                continue
            hp = num(h["hp"])
            ar = role(m.spawns[h["attacker"]]["class"]) if h["attacker"] in m.spawns else "?"
            if h["victim_kind"] == "horse":
                R[ar]["horse"] += hp
                continue
            R[ar]["dealt"] += hp
            if h["victim"] in m.spawns:
                R[role(m.spawns[h["victim"]]["class"])]["taken"] += hp
        for d in m.deaths:
            if d["victim_kind"] == "human" and d["killer"] and m.side(d["killer"]) != m.side(d["victim"]) and d["state"] != "left":
                R[role(m.spawns[d["killer"]]["class"])]["kills"] += 1
    w("### By role")
    w("")
    w(table(["Role", "Lives", "Kills", "Deaths", "K/D", "Dealt", "Taken", "Dealt/taken", "Dealt per life",
             "Horse damage"],
            [(r, c["lives"], c["kills"], c["deaths"], "%.2f" % (c["kills"] / max(1, c["deaths"])), f0(c["dealt"]),
              f0(c["taken"]), "%.2f" % (c["dealt"] / max(1, c["taken"])), f1(c["dealt"] / max(1, c["lives"])),
              f0(c["horse"]))
             for r, c in sorted(R.items(), key=lambda x: -x[1]["lives"])]))
    w("")

    w("### Infantry: damage dealt vs received")
    w("")
    w("Damage dealt while playing an infantry class, and damage taken on infantry lives.")
    w("")
    rows = [(name(p), f0(P[p]["dmg_infantry"]), f0(P[p]["taken_infantry"]),
             "%.2f" % (P[p]["dmg_infantry"] / max(1, P[p]["taken_infantry"])))
            for p in players if P[p]["taken_infantry"] or P[p]["dmg_infantry"]]
    w(table(["Player", "Dealt as infantry", "Taken as infantry", "Ratio"],
            sorted(rows, key=lambda r: -float(r[3]))))
    w("")

    archers = [p for p in players if P[p]["shots"] >= 5]
    if archers:
        w("### Ranged (5+ shots)")
        w("")
        w("Hits are missiles that reached a player; blocked = stopped by a shield. Headshots include the neck.")
        w("")
        rows = []
        for p in sorted(archers, key=lambda p: -P[p]["m_dmg"]):
            c = P[p]
            landed = c["m_hits"] - c["m_blocked"]
            rows.append((name(p), c["shots"], c["m_hits"], c["m_blocked"], "%.0f%%" % (100 * landed / c["shots"]),
                         c["m_head"], f0(c["m_dmg"]), f1(c["m_dmg"] / max(1, landed)),
                         f1(c["m_dist"] / max(1, landed))))
        w(table(["Player", "Shots", "Hits", "Blocked", "Landed %", "Head/neck", "Damage", "Dmg per landed",
                 "Avg distance m"], rows))
        w("")

    riders = [p for p in players if P[p]["cav_hits"] >= 5]
    if riders:
        w("### Cavalry (5+ mounted thrusts or couches landed)")
        w("")
        w("Speed is the rider's own speed when the hit landed. Vanilla comparison: those of the player's thrusts "
          "found in MPBalancer's log (%d of %d thrusts and couches matched)." % (joined, attempted))
        w("")
        rows = []
        for p in sorted(riders, key=lambda p: -P[p]["cav_hits"]):
            c = P[p]
            vt = vanilla_thrust[p]
            rows.append((name(p), c["cav_hits"], f1(c["cav_speed"] / c["cav_hits"]), c["couches"], c["bumps"],
                         c["horses_lost"], f0(vt[0]), f0(vt[1]), "%+.0f%%" % (100 * (vt[1] / vt[0] - 1)) if vt[0] else ""))
        w(table(["Player", "Thrusts landed", "Avg speed m/s", "Couches", "Bumps", "Horses lost", "Thrust dmg vanilla",
                 "Thrust dmg dealt", "Change"], rows))
        w("")

    w("### Team hits")
    w("")
    tk = [p for p in players if P[p]["team_hits"] or P[p]["team_taken"] or P[p]["team_kills"]]
    w(table(["Player", "Team hits", "Team damage dealt", "Team kills", "Team damage taken"],
            [(name(p), P[p]["team_hits"], f0(P[p]["team_dmg"]), P[p]["team_kills"], f0(P[p]["team_taken"]))
             for p in sorted(tk, key=lambda p: -P[p]["team_dmg"])]))
    w("")
    if team_by_kind:
        w("Team damage by attack: " + ", ".join("%s %s" % (k, f0(v)) for k, v in team_by_kind.most_common()) + ".")
        w("")
    print("\n".join(out))


if __name__ == "__main__":
    main()
