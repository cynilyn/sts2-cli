"""Tests for events."""
import pytest


class TestNeowEvent:
    def test_neow_is_first_event(self, game):
        state = game.start(seed="ne1")
        assert state["decision"] == "event_choice"
        assert "Neow" in str(state.get("event_name", ""))

    def test_neow_options(self, game):
        state = game.start(seed="ne2")
        for opt in state["options"]:
            assert "title" in opt
            assert isinstance(opt["title"], str)
            assert "is_locked" in opt

    def test_neow_option_vars(self, game):
        state = game.start(seed="ne3")
        for opt in state["options"]:
            if opt.get("vars"):
                for k, v in opt["vars"].items():
                    assert isinstance(v, (int, float))

    def test_choose_neow(self, game):
        state = game.start(seed="ne4")
        opts = [o for o in state["options"] if not o.get("is_locked")]
        state = game.act("choose_option", option_index=opts[0]["index"])
        assert state.get("decision") is not None


class TestEventDescriptions:
    def test_no_ismultiplayer_tag(self, game):
        state = game.start(seed="ed1")
        for opt in state.get("options", []):
            d = opt.get("description") or ""
            assert "IsMultiplayer" not in d


# --- Choosing an event option must change the state (no silent no-ops) ---

def _to_map(game, seed):
    return game.skip_neow(game.start(seed=seed))


def _signature(state):
    p = state.get("player", {})
    return (state.get("decision"), state.get("description"),
            tuple(o.get("text_key") for o in state.get("options", [])),
            p.get("hp"), p.get("max_hp"), p.get("gold"), len(p.get("relics", [])))


def _finish_to_map(game, state, max_steps=30):
    """Resolve whatever follow-up a verdict opens (card pick / reward) until the map is back."""
    for _ in range(max_steps):
        dec = state.get("decision")
        if dec == "map_select":
            return state
        if dec == "card_select":
            state = (game.act("skip_select") if state.get("min_select", 0) == 0
                     else game.act("select_cards", indices=",".join(str(i) for i in range(state["min_select"]))))
        elif dec == "card_reward":
            state = game.act("skip_card_reward")
        elif dec == "event_choice":
            state = game.act("choose_option", option_index=0)
        else:
            state = game.act("proceed")
    return state


TRIAL_VERDICT_PAGES = {"MERCHANT", "NOBLE", "NONDESCRIPT"}


class TestTrialEvent:
    """Accept used to NRE in headless (portrait/VFX) and return the same page forever."""

    @pytest.mark.parametrize("seed", ["tr1", "tr2", "tr3", "tr4"])
    @pytest.mark.parametrize("verdict", [0, 1])
    def test_accept_leads_to_verdict_then_map(self, game, seed, verdict):
        _to_map(game, seed)
        state = game.enter_room("event", event="TRIAL")
        assert state["options"][0]["text_key"] == "TRIAL.pages.INITIAL.options.ACCEPT"
        state = game.act("choose_option", option_index=0)
        keys = [o["text_key"] for o in state["options"]]
        assert state["decision"] == "event_choice"
        assert len(keys) == 2 and keys[0].endswith(".GUILTY") and keys[1].endswith(".INNOCENT")
        assert keys[0].split(".")[2] in TRIAL_VERDICT_PAGES
        state = game.act("choose_option", option_index=verdict)
        state = _finish_to_map(game, state)
        assert state["decision"] == "map_select"

    def test_all_three_verdict_pages_reachable(self, game):
        seen = set()
        for i in range(12):
            _to_map(game, f"trp{i}")
            game.enter_room("event", event="TRIAL")
            state = game.act("choose_option", option_index=0)
            seen.add(state["options"][0]["text_key"].split(".")[2])
            if seen == TRIAL_VERDICT_PAGES:
                break
        assert seen == TRIAL_VERDICT_PAGES

    def test_reject_then_accept(self, game):
        _to_map(game, "tr_rej")
        game.enter_room("event", event="TRIAL")
        state = game.act("choose_option", option_index=1)
        assert [o["text_key"] for o in state["options"]] == [
            "TRIAL.pages.REJECT.options.ACCEPT", "TRIAL.pages.REJECT.options.DOUBLE_DOWN"]
        state = game.act("choose_option", option_index=0)
        assert state["options"][0]["text_key"].split(".")[2] in TRIAL_VERDICT_PAGES


class TestEventOptionsChangeState:
    @pytest.mark.parametrize("event", ["DENSE_VEGETATION", "JUNGLE_MAZE_ADVENTURE"])
    def test_cosmetic_ui_calls_do_not_block_options(self, game, event):
        """Audio/rumble calls on the null UI singletons used to NRE and leave the page unchanged."""
        _to_map(game, "eo_" + event)
        before = game.enter_room("event", event=event)
        for opt in before["options"]:
            if opt.get("is_locked"):
                continue
            _to_map(game, "eo_" + event)
            before = game.enter_room("event", event=event)
            after = game.act("choose_option", option_index=opt["index"])
            assert _signature(after) != _signature(before), opt["text_key"]

    def test_every_first_page_option_changes_state(self, game):
        import json, os
        loc = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           "localization_eng", "events.json")
        with open(loc, encoding="utf-8") as f:
            ids = sorted({k.split(".")[0] for k in json.load(f) if ".pages.INITIAL" in k})
        # MOCK/DEPRECATED are test fixtures; the rest are skipped by the simulator on entry.
        skip = {"MOCK_EVENT_MODEL", "DEPRECATED_EVENT", "CRYSTAL_SPHERE", "FAKE_MERCHANT", "THE_FUTURE_OF_POTIONS"}
        stuck = []
        for event in ids:
            if event in skip:
                continue
            index = 0
            while True:
                _to_map(game, "sw_" + event)
                before = game.enter_room("event", event=event)
                if before.get("decision") != "event_choice" or index >= len(before["options"]):
                    break
                if not before["options"][index].get("is_locked"):
                    after = game.act("choose_option", option_index=index)
                    if _signature(after) == _signature(before):
                        stuck.append((event, before["options"][index]["text_key"]))
                index += 1
        assert not stuck, stuck
