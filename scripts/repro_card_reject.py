"""Capture the exact state when play_card is rejected with "could not be played".

Reuses the sts2_bot probe runner (read-only import) and wraps Sts2Process.act so every rejected
play_card is dumped together with the last combat state the caller saw.

  STS2_BOT=C:/Users/cynilyn/fork_repository/sts2_bot STS2_CLI_PROJECT=<csproj> \
    python scripts/repro_card_reject.py --characters Ironclad --runs 5 --out logs/reject_cases.jsonl
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import pathlib
import sys

BOT = pathlib.Path(os.environ.get("STS2_BOT", "C:/Users/cynilyn/fork_repository/sts2_bot"))
sys.path.insert(0, str(BOT))

from rl import full_run_probe as fp  # noqa: E402
from rl.sts2_process import Sts2Process, Sts2ProcessError  # noqa: E402


FIELDS: collections.Counter = collections.Counter()


def tally_fields(r: dict) -> None:
    """Count the protocol additions seen in a response (work B: purpose/prompt, ids, event_id, map)."""
    d = r.get("decision")
    if d is None:
        return
    FIELDS[f"decision:{d}"] += 1
    if d == "card_select":
        FIELDS[f"card_select.purpose:{r.get('purpose')}"] += 1
        FIELDS[f"card_select.prompt:{r.get('prompt')}"] += 1
    elif d == "event_choice":
        FIELDS["event_choice.event_id:" + ("ok" if str(r.get("event_id", "")).startswith("EVENT.") else "MISSING")] += 1
    elif d == "map_select":
        m = r.get("map") or {}
        FIELDS["map_select.map:" + ("ok" if m.get("rows") and m.get("boss") else "MISSING")] += 1
    elif d == "shop":
        for key in ("cards", "relics", "potions"):
            for e in r.get(key) or []:
                FIELDS[f"shop.{key}.id:" + ("ok" if e.get("id") else "MISSING")] += 1
    p = r.get("player") or {}
    for key in ("relics", "potions"):
        for e in p.get(key) or []:
            FIELDS[f"player.{key}.id:" + ("ok" if e.get("id") else "MISSING")] += 1


def slim_state(s: dict) -> dict:
    return {
        "round": s.get("round"), "energy": s.get("energy"),
        "player": s.get("player"),
        "hand": s.get("hand"),
        "enemies": s.get("enemies"),
        "context": s.get("context"),
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--characters", nargs="+", default=["Ironclad"])
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--god", action="store_true", default=True)
    ap.add_argument("--policy", default="heuristic")
    ap.add_argument("--ascension", type=int, default=0)
    ap.add_argument("--out", required=True)
    ap.add_argument("--max-seconds", type=float, default=240)
    args = ap.parse_args()
    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)

    for ch in args.characters:
        for i in range(args.start, args.start + args.runs):
            seed = f"probe_{args.policy}_{ch}_{i}" + (f"_A{args.ascension}" if args.ascension else "")
            proc = Sts2Process(timeout=45, stderr_path=out.parent / f"stderr_{seed}.txt")
            last = {"state": None}
            real_act = proc.act

            def act(name, _real=real_act, _last=last, **a):
                r = _real(name, **a)
                tally_fields(r)
                if name == "play_card" and r.get("type") == "error" and "could not be played" in str(r.get("message")):
                    st = _last["state"] or {}
                    idx = a.get("card_index")
                    hand = st.get("hand") or []
                    card = next((c for c in hand if c.get("index") == idx), None)
                    rec = {"seed": seed, "args": a, "message": r.get("message"), "card": card,
                           "state": slim_state(st)}
                    with open(out, "a", encoding="utf-8") as f:
                        f.write(json.dumps(rec, ensure_ascii=False) + "\n")
                elif r.get("type") != "error":
                    _last["state"] = r
                return r

            proc.act = act
            try:
                res = fp.play_run(proc, ch, seed, args.god, 20000, args.max_seconds, args.policy, args.ascension)
            except Sts2ProcessError as e:
                res = {"outcome": "process_error", "message": str(e)}
            finally:
                proc.close()
            print(seed, res.get("outcome"), res.get("errors"), res.get("error_msgs"), flush=True)


if __name__ == "__main__":
    main()
    fields_path = pathlib.Path(sys.argv[sys.argv.index("--out") + 1]).with_suffix(".fields.json")
    fields_path.write_text(json.dumps(dict(sorted(FIELDS.items())), indent=1), encoding="utf-8")

