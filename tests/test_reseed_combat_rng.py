"""reseed_combat_rng: future combat randomness is re-drawn; set_draw_order can name upgraded copies."""

from conftest import Game


def _start(seed="reseed1", encounter="NIBBITS_WEAK", **extra):
    game = Game()
    cmd = {"cmd": "start_combat", "character": "Ironclad", "seed": seed, "encounter": encounter, **extra}
    state = game.send(cmd)
    assert state["decision"] == "combat_play", state
    return game, state


def _observe(reseed, turns=4):
    """Intents + hand at the start of each turn after `reseed` was applied on turn 1."""
    game, state = _start()
    try:
        if reseed is not None:
            r = game.send({"cmd": "reseed_combat_rng", "seed": reseed})
            assert r["type"] == "ok", r
            assert "monster_ai" in r["streams"] and "shuffle" in r["streams"], r
        seen = []
        for _ in range(turns):
            if state.get("decision") != "combat_play":
                break
            seen.append(([str(e.get("intents")) for e in state["enemies"]], sorted(c["id"] for c in state["hand"])))
            state = game.act("end_turn")
        return seen
    finally:
        game.close()


def test_reseed_keeps_the_displayed_turn():
    """Turn-1 intents and hand were rolled before the reseed and must not change."""
    plain = _observe(None)
    reseeded = _observe("seedA")
    assert reseeded[0] == plain[0]


def test_same_reseed_is_deterministic():
    assert _observe("seedA") == _observe("seedA")


def test_different_reseed_changes_the_future():
    futures = {repr(_observe(s)[1:]) for s in ("seedA", "seedB", "seedC", "seedD", "seedE")}
    assert len(futures) > 1


def test_reseed_outside_combat_is_an_error():
    game = Game()
    try:
        game.skip_neow(game.start(seed="reseed2"))
        assert game.send({"cmd": "reseed_combat_rng", "seed": "x"})["type"] == "error"
    finally:
        game.close()


def test_reseed_requires_a_seed():
    game, _ = _start()
    try:
        assert game.send({"cmd": "reseed_combat_rng"})["type"] == "error"
    finally:
        game.close()


def test_set_draw_order_distinguishes_upgraded_copies():
    deck = ["STRIKE_IRONCLAD"] * 6 + ["STRIKE_IRONCLAD+"] * 6
    game, _ = _start("upg1", player={"deck": deck})
    try:
        # A bare id still matches either copy (first in pile order); "ID+" insists on the upgraded one.
        for _ in range(2):
            game.send({"cmd": "set_draw_order", "cards": ["STRIKE_IRONCLAD"]})
            game.send({"cmd": "set_draw_order", "cards": ["STRIKE_IRONCLAD+", "STRIKE_IRONCLAD+"]})
            pile = game.send({"cmd": "get_draw_pile"})["draw_pile"]
            assert [c["upgraded"] for c in pile[:2]] == [True, True]
        game.send({"cmd": "set_draw_order", "cards": ["STRIKE_IRONCLAD+", "STRIKE_IRONCLAD"]})
        pile = game.send({"cmd": "get_draw_pile"})["draw_pile"]
        assert pile[0]["upgraded"] is True
    finally:
        game.close()
