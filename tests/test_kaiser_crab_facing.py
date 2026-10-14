"""Kaiser Crab (Surrounded): combat_play exposes the player's facing and each part's side."""

from conftest import Game


def _start():
    game = Game()
    state = game.send({"cmd": "start_combat", "character": "Ironclad", "seed": "facing1",
                       "encounter": "KAISER_CRAB_BOSS"})
    assert state["decision"] == "combat_play", state
    return game, state


def _by_id(state, monster_id):
    return next(e for e in state["enemies"] if e["id"] == monster_id)


def test_facing_and_sides_exposed():
    game, state = _start()
    try:
        assert state["facing"] == "Right"
        assert _by_id(state, "CRUSHER")["side"] == "Left"
        assert _by_id(state, "ROCKET")["side"] == "Right"
    finally:
        game.close()


def test_facing_flips_when_targeting_the_other_side():
    game, state = _start()
    try:
        crusher = _by_id(state, "CRUSHER")["index"]
        card = next(c for c in state["hand"] if c.get("can_play") and c.get("target_type") == "AnyEnemy"
                    or c.get("can_play") and c.get("needs_target"))
        state = game.act("play_card", card_index=card["index"], target_index=crusher)
        assert state["facing"] == "Left"
    finally:
        game.close()


def test_facing_absent_outside_kaiser_crab():
    game = Game()
    try:
        state = game.send({"cmd": "start_combat", "character": "Ironclad", "seed": "facing2",
                           "encounter": "NIBBITS_WEAK"})
        assert state["facing"] is None
        assert all(e["side"] is None for e in state["enemies"])
    finally:
        game.close()
