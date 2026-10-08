"""Single-target cards while every enemy is down.

Test Subject (act 3 boss) spends a stretch of the player's turn at hp 0 / not alive (only ADAPTABLE_POWER left)
while the play phase keeps running. `enemies` is empty then, an AnyEnemy card has no target and the game never
starts it, so `can_play=true` followed by "Card could not be played (still in hand after action)" was a lie.
"""

DECK = ["BLUDGEON", "BLUDGEON", "ANGER", "ANGER", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD",
        "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]


def _fight_until_no_enemy(game, max_steps=400):
    state = game.send({"cmd": "start_combat", "character": "Ironclad", "seed": "dead_target",
                       "encounter": "TEST_SUBJECT_BOSS",
                       "player": {"hp": 9999, "max_hp": 9999, "deck": DECK}})
    for _ in range(max_steps):
        if state.get("decision") != "combat_play":
            return None
        if not state["enemies"]:
            return state
        energy = state["energy"]
        attacks = [c for c in state["hand"] if c["can_play"] and c["cost"] <= energy
                   and c["target_type"] == "AnyEnemy"]
        if attacks:
            best = max(attacks, key=lambda c: (c.get("stats") or {}).get("damage", 0))
            state = game.act("play_card", card_index=best["index"],
                             target_index=state["enemies"][0]["index"])
        else:
            state = game.act("end_turn")
    return None


def test_single_target_cards_not_playable_without_living_enemy(game):
    state = _fight_until_no_enemy(game)
    assert state is not None, "never reached the no-living-enemy window; adjust the deck or seed"
    targeted = [c for c in state["hand"] if c["target_type"] == "AnyEnemy"]
    assert targeted, "hand has no single-target card to check"
    assert all(c["can_play"] is False for c in targeted)


def test_playing_single_target_card_without_enemy_is_a_clear_error(game):
    state = _fight_until_no_enemy(game)
    assert state is not None
    card = next(c for c in state["hand"] if c["target_type"] == "AnyEnemy")
    r = game.act("play_card", card_index=card["index"])
    assert r["type"] == "error"
    assert "no living enemy" in r["message"]


def test_turn_can_end_and_combat_continues(game):
    state = _fight_until_no_enemy(game)
    assert state is not None
    state = game.act("end_turn")
    assert state.get("type") != "error", state
    assert state["decision"] in ("combat_play", "card_reward", "map_select", "game_over", "card_select")
