"""Regression tests for native save/load behavior."""

from conftest import Game


def test_load_map_save_does_not_retrigger_neow(tmp_path):
    save_path = tmp_path / "map_select.save"

    game = Game()
    try:
        state = game.start(seed="sl1")
        state = game.skip_neow(state)
        assert state["decision"] == "map_select"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select"
    finally:
        game.close()


def test_load_pre_neow_save_preserves_neow_choice(tmp_path):
    save_path = tmp_path / "pre_neow.save"

    game = Game()
    try:
        state = game.start(seed="sl2")
        assert state["decision"] == "event_choice"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "event_choice"
    finally:
        game.close()


def test_load_save_twice_in_one_process(tmp_path):
    save_path = tmp_path / "twice.save"

    game = Game()
    try:
        state = game.skip_neow(game.start(seed="sl3"))
        assert state["decision"] == "map_select"
        assert game.send({"cmd": "write_continue_save", "path": str(save_path)})["success"] is True
        first = game.send({"cmd": "load_save", "path": str(save_path)})
        second = game.send({"cmd": "load_save", "path": str(save_path)})
        assert first["decision"] == "map_select"
        assert second["decision"] == "map_select", second
    finally:
        game.close()
