"""Protocol additions for the non-combat RL environment (P1).

Every check is additive: existing fields keep their shape; these tests only assert the new ones.
"""
import pytest


def _to_map(game, seed):
    state = game.start(seed=seed)
    return game.skip_neow(state)


class TestCardSelectPurpose:
    def test_upgrade_at_rest_site(self, game):
        _to_map(game, "p1a")
        state = game.enter_room("rest_site")
        smith = next(o for o in state["options"] if o["option_id"] == "SMITH")
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select"
        assert state["purpose"] == "upgrade"
        assert isinstance(state["prompt"], str)

    def test_remove_at_shop(self, game):
        _to_map(game, "p1b")
        game.set_player(gold=999)
        state = game.enter_room("shop")
        assert state["decision"] == "shop"
        state = game.act("remove_card")
        assert state["decision"] == "card_select"
        assert state["purpose"] == "remove"

    def test_purpose_is_cleared_between_selections(self, game):
        """A later selection must not inherit the previous call's purpose."""
        _to_map(game, "p1c")
        state = game.enter_room("rest_site")
        smith = next(o for o in state["options"] if o["option_id"] == "SMITH")
        state = game.act("choose_option", option_index=smith["index"])
        assert state["purpose"] == "upgrade"
        game.act("select_cards", indices="0")
        game.set_player(gold=999)
        state = game.enter_room("shop")
        state = game.act("remove_card")
        assert state["purpose"] == "remove"


class TestShopIds:
    def test_shop_entries_have_ids(self, game):
        _to_map(game, "p1d")
        state = game.enter_room("shop")
        assert state["decision"] == "shop"
        for card in state["cards"]:
            assert card["id"].startswith("CARD."), card
            assert "can_buy" in card
        for relic in state["relics"]:
            assert relic["id"], relic
            assert "can_buy" in relic
        for potion in state["potions"]:
            assert potion["id"], potion

    def test_shop_relic_id_matches_owned_relic_format(self, game):
        """A bought relic must show up in player.relics under the id the shop advertised."""
        _to_map(game, "p1e")
        game.set_player(gold=9999)
        state = game.enter_room("shop")
        relic = next(r for r in state["relics"] if r["can_buy"])
        state = game.act("buy_relic", relic_index=relic["index"])
        assert state["decision"] == "shop"
        assert relic["id"] in [r["id"] for r in state["player"]["relics"]]

    def test_owned_relics_and_potions_have_ids(self, game):
        state = game.start(seed="p1f")
        assert all(r["id"] for r in state["player"]["relics"])


class TestEventId:
    def test_event_choice_has_event_id(self, game):
        _to_map(game, "p1g")
        state = game.enter_room("event", event="WOOD_CARVINGS")
        assert state["decision"] == "event_choice"
        assert state["event_id"] == "EVENT.WOOD_CARVINGS"

    def test_neow_event_has_event_id(self, game):
        state = game.start(seed="p1h")
        assert state["decision"] == "event_choice"
        assert state["event_id"].startswith("EVENT.")


class TestMapSelectMap:
    def test_map_select_carries_full_map(self, game):
        state = _to_map(game, "p1i")
        assert state["decision"] == "map_select"
        m = state["map"]
        assert m["rows"] and m["boss"]["type"] == "Boss"
        assert "current_coord" in m
        # same content as the get_map command
        full = game.get_map()
        assert m["rows"] == full["rows"]
        assert m["boss"] == full["boss"]

    def test_choices_unchanged(self, game):
        state = _to_map(game, "p1j")
        assert state["choices"] and {"col", "row", "type"} <= set(state["choices"][0])


class TestTreasure:
    def test_treasure_room_awards_a_relic(self, game):
        state = _to_map(game, "p1k")
        before = len(state["player"]["relics"])
        state = game.enter_room("treasure")
        # the room resolves on its own; the next decision is a normal one with one more relic
        assert state["decision"] in ("map_select", "event_choice", "card_reward", "card_select", "bundle_select")
        player = state.get("player") or {}
        assert len(player.get("relics", [])) >= before + 1
