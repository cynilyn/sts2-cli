"""Regression tests: room-entry selections must not hang, and '?' roll state must survive checkpoints."""

import json
import threading
from pathlib import Path

import pytest

from conftest import Game


class _Timeout(Exception):
    pass


def _send_with_timeout(game, cmd, seconds=60):
    """A hang must fail the test instead of blocking the whole suite."""
    box = {}

    def run():
        box["r"] = game.send(cmd)

    t = threading.Thread(target=run, daemon=True)
    t.start()
    t.join(seconds)
    if "r" not in box:
        game.proc.kill()
        raise _Timeout(f"no response to {cmd} within {seconds}s")
    return box["r"]


def _drain_prompts(game, state, limit=40):
    """Answer card_reward / card_select prompts until a non-prompt decision comes back."""
    for _ in range(limit):
        dec = state.get("decision")
        if dec == "card_reward":
            state = game.act("skip_card_reward")
        elif dec == "card_select":
            state = game.act("select_cards", indices=",".join(str(i) for i in range(state.get("min_select", 1))))
        else:
            return state
    raise AssertionError("prompts did not end")


FIXTURES = Path(__file__).parent / "fixtures"


def test_lords_parasol_shop_entry_yields_prompts_then_shop():
    """Lord's Parasol buys the whole shop on entry; this shop stocks Orrery (5 card rewards on pickup).

    The game blocks the calling thread on a card reward, which used to leave select_map_node without any
    response (Act 3 shops in 2/2000 bot games). Fixture: seed sel4_Ironclad_1531, map node (6,5).
    """
    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(FIXTURES / "act3_shop_parasol_orrery.save")})
        assert state["decision"] == "map_select", state

        state = _send_with_timeout(
            game, {"cmd": "action", "action": "select_map_node", "args": {"col": 6, "row": 5}})
        assert state["decision"] == "card_reward", state
        state = _drain_prompts(game, state)
        assert state["decision"] == "shop", state
    finally:
        game.close()


def _load(game, path):
    state = game.send({"cmd": "load_save", "path": str(path)})
    assert state["decision"] == "map_select", state
    return state


def _write(game, path):
    result = game.send({"cmd": "write_continue_save", "path": str(path)})
    assert result.get("success") is True, result
    return json.loads(path.read_text(encoding="utf-8"))


def test_checkpoint_from_inside_room_keeps_pre_roll_state(tmp_path):
    """A checkpoint written after entering a node equals the one written on the map just before it."""
    map_save = tmp_path / "map.save"
    room_save = tmp_path / "room.save"

    game = Game()
    try:
        state = game.skip_neow(game.start(seed="preroll1"))
        before = _write(game, map_save)
        pick = state["choices"][0]
        entered = game.act("select_map_node", col=pick["col"], row=pick["row"])
        assert entered["decision"] != "map_select"
        inside = _write(game, room_save)
    finally:
        game.close()

    assert inside["odds"] == before["odds"]
    assert inside["rng"] == before["rng"]
    assert inside["visited_map_coords"] == before["visited_map_coords"]
    assert [len(a) for a in inside["map_point_history"]] == [len(a) for a in before["map_point_history"]]
    assert inside["acts"] == before["acts"]


def test_reloaded_checkpoint_reenters_the_same_room(tmp_path):
    room_save = tmp_path / "room.save"

    game = Game()
    try:
        state = game.skip_neow(game.start(seed="preroll2"))
        pick = state["choices"][0]
        entered = game.act("select_map_node", col=pick["col"], row=pick["row"])
        _write(game, room_save)
    finally:
        game.close()

    game = Game()
    try:
        _load(game, room_save)
        again = game.act("select_map_node", col=pick["col"], row=pick["row"])
        assert again["decision"] == entered["decision"]
        assert again.get("context", {}).get("room_type") == entered.get("context", {}).get("room_type")
        assert [e.get("id") for e in again.get("enemies", [])] == [e.get("id") for e in entered.get("enemies", [])]
    finally:
        game.close()


def test_load_save_keeps_saved_unknown_room_odds(tmp_path):
    """EnterAct resets the '?' odds to base; a mid-act continue must keep the saved ones (as the real game does)."""
    src = tmp_path / "map.save"
    edited = tmp_path / "edited.save"
    again = tmp_path / "again.save"

    game = Game()
    try:
        game.skip_neow(game.start(seed="odds1"))
        data = _write(game, src)
    finally:
        game.close()

    data["odds"]["unknown_map_point_monster_odds_value"] = 0.375
    data["odds"]["unknown_map_point_treasure_odds_value"] = 0.125
    data["odds"]["unknown_map_point_shop_odds_value"] = 0.0625
    edited.write_text(json.dumps(data), encoding="utf-8")

    game = Game()
    try:
        _load(game, edited)
        after = _write(game, again)
    finally:
        game.close()

    assert after["odds"]["unknown_map_point_monster_odds_value"] == pytest.approx(0.375)
    assert after["odds"]["unknown_map_point_treasure_odds_value"] == pytest.approx(0.125)
    assert after["odds"]["unknown_map_point_shop_odds_value"] == pytest.approx(0.0625)


def test_legacy_post_roll_entry_is_dropped_on_load(tmp_path):
    """Older checkpoints left the entered node's history entry behind; loading must not record it twice."""
    src = tmp_path / "map.save"
    legacy = tmp_path / "legacy.save"
    again = tmp_path / "again.save"

    game = Game()
    try:
        game.skip_neow(game.start(seed="legacy1"))
        data = _write(game, src)
    finally:
        game.close()

    stale = json.loads(json.dumps(data["map_point_history"][-1][-1]))
    stale["map_point_type"] = "monster"
    data["map_point_history"][-1].append(stale)
    legacy.write_text(json.dumps(data), encoding="utf-8")

    game = Game()
    try:
        _load(game, legacy)
        after = _write(game, again)
    finally:
        game.close()

    assert len(after["map_point_history"][-1]) == len(after["visited_map_coords"])


def test_legacy_post_roll_checkpoint_keeps_the_recorded_unknown_outcome():
    """Checkpoint written by an older build from inside a '?' room that had rolled a shop.

    Its odds / RNG are already past the roll, so re-rolling gives an event (EVENT.BUGSLAYER); the stale
    history entry says shop, and that recorded outcome is used. Seed shopp_Ironclad_2, act 2 node (6,5).
    """
    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(FIXTURES / "legacy_post_roll_unknown_shop.save")})
        assert state["decision"] == "map_select", state
        state = game.act("select_map_node", col=6, row=5)
        assert state["decision"] == "shop", state
    finally:
        game.close()
