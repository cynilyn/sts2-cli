"""Tests for shop scenarios."""
import pytest


class TestShopStructure:
    def test_shop_fields(self, game):
        state = game.start(seed="ss1")
        game.skip_neow(state)
        state = game.enter_room("shop")
        assert state["decision"] == "shop"
        assert "cards" in state
        assert "relics" in state
        assert "potions" in state
        assert "card_removal_cost" in state

    def test_shop_cards_have_description(self, game):
        state = game.start(seed="ss2")
        game.skip_neow(state)
        state = game.enter_room("shop")
        for card in state["cards"]:
            assert isinstance(card["name"], str)
            assert "description" in card
            assert "cost" in card
            assert "type" in card
            assert "card_cost" in card

    def test_shop_cards_have_upgrade_preview(self, game):
        state = game.start(seed="ss3")
        game.skip_neow(state)
        state = game.enter_room("shop")
        has_upgrade = any(c.get("after_upgrade") for c in state["cards"])
        assert has_upgrade

    def test_shop_relics_have_description(self, game):
        state = game.start(seed="ss4")
        game.skip_neow(state)
        state = game.enter_room("shop")
        for r in state["relics"]:
            assert isinstance(r["name"], str)
            assert "description" in r

    def test_shop_potions_have_description(self, game):
        state = game.start(seed="ss5")
        game.skip_neow(state)
        state = game.enter_room("shop")
        for p in state["potions"]:
            assert isinstance(p["name"], str)
            assert "description" in p


class TestShopBuy:
    def test_buy_card_reduces_gold(self, game):
        state = game.start(seed="sb1")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("shop")
        gold_before = state["player"]["gold"]
        deck_before = state["player"]["deck_size"]
        stocked = [c for c in state["cards"] if c.get("is_stocked")]
        assert stocked
        card = stocked[0]
        state = game.act("buy_card", card_index=card["index"])
        if state.get("decision") == "shop":
            assert state["player"]["gold"] < gold_before
            assert state["player"]["deck_size"] == deck_before + 1

    def test_buy_insufficient_gold(self, game):
        state = game.start(seed="sb2")
        game.skip_neow(state)
        game.set_player(gold=0)
        state = game.enter_room("shop")
        stocked = [c for c in state["cards"] if c.get("is_stocked")]
        if stocked:
            state = game.act("buy_card", card_index=stocked[0]["index"])
            assert state.get("type") == "error"

    def test_leave_shop(self, game):
        state = game.start(seed="sb3")
        game.skip_neow(state)
        state = game.enter_room("shop")
        state = game.act("leave_room")
        assert state["decision"] == "map_select"


class TestShopRemove:
    def test_remove_card_flow(self, game):
        state = game.start(seed="sr1")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("shop")
        deck_before = state["player"]["deck_size"]
        state = game.act("remove_card")
        # Should trigger card_select
        if state["decision"] == "card_select":
            state = game.act("select_cards", indices="0")
            # Should return to shop with deck_size - 1
            if state.get("decision") == "shop":
                assert state["player"]["deck_size"] == deck_before - 1


class TestSozuPotions:
    """Sozu: no potions can be obtained. The shop must say so instead of a silent no-op."""

    def _sozu_shop(self, game, seed):
        state = game.start(seed=seed)
        game.skip_neow(state)
        game.set_player(relics=["SOZU"], gold=999)
        return game.enter_room("shop")

    def test_potions_not_buyable_with_sozu(self, game):
        state = self._sozu_shop(game, "sozu1")
        assert state["decision"] == "shop"
        assert state["potions"] and all(not p["can_buy"] for p in state["potions"])

    def test_buy_potion_returns_error(self, game):
        self._sozu_shop(game, "sozu2")
        result = game.act("buy_potion", potion_index=0)
        assert result.get("type") == "error"
        assert "Sozu" in result["message"]

    def test_potions_buyable_without_sozu(self, game):
        state = game.start(seed="sozu3")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("shop")
        assert any(p["can_buy"] for p in state["potions"])
