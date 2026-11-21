import ctypes
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
from threading import Event, Thread
import time

import window_regression as q
from close_dialog_regression import ui
from search_regression import Fixture, SearchFixtureServer, press, physical_click, wait_ui


def suggestions_visible(hwnd):
    try:
        return bool(json.loads(ui(hwnd, "搜索建议", "bounds"))["visible"])
    except AssertionError as error:
        if "Missing control" in str(error):
            return False
        raise


def type_character(hwnd, character):
    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", ctypes.c_ushort), ("wScan", ctypes.c_ushort), ("dwFlags", ctypes.c_uint),
                    ("time", ctypes.c_uint), ("dwExtraInfo", ctypes.c_size_t)]
    class INPUTUNION(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_byte * 32)]
    class INPUT(ctypes.Structure):
        _fields_ = [("type", ctypes.c_uint), ("union", INPUTUNION)]
    time.sleep(.3)
    q.user.SetForegroundWindow(hwnd)
    for flags in (4, 4 | 2):
        event = INPUT(type=1, union=INPUTUNION(ki=KEYBDINPUT(0, ord(character), flags, 0, 0)))
        assert q.user.SendInput(1, ctypes.byref(event), ctypes.sizeof(event)) == 1


def exercise(app, root, server):
    base = f"http://127.0.0.1:{server.server_port}"
    profile = q.make_profile(root, base, "autocomplete", "a", False)
    config = {"sites": [{"key": key, "type": 4, "name": name, "api": f"{base}/search/{key}"}
                        for key, name in (("a", "快站"), ("b", "慢站"), ("c", "重试站"), ("d", "空站"))]}
    (profile / "config" / "last-good.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.executemany("INSERT INTO settings VALUES (?,?)", [
            ("ui.animations", "false"), ("search.history", '["alpha建议","beta建议"]')])
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "autocomplete window")
        q.log_after(profile, "Vod home ready: a")
        ui(hwnd, "打开搜索", "invoke")
        ui(hwnd, "搜索关键词", "focus")
        type_character(hwnd, "a")
        q.wait_for(lambda: ui(hwnd, "搜索关键词", "read") == "a", "physical letter reaches the query editor")
        wait_ui(hwnd, "搜索建议")
        wait_ui(hwnd, "alpha建议")
        popup = json.loads(ui(hwnd, "搜索建议", "bounds"))
        assert popup["visible"] and popup["w"] > 0 and popup["h"] > 0, popup
        assert server.searches == [], server.searches
        ui(hwnd, "alpha建议", "invoke")
        assert ui(hwnd, "搜索关键词", "read") == "alpha建议"
        assert not suggestions_visible(hwnd)
        ui(hwnd, "搜索", "invoke")
        q.wait_for(lambda: len(server.searches) == 4, "suggestion selection does not add background searches")
        ui(hwnd, "搜索关键词", "set", "")
        ui(hwnd, "搜索关键词", "focus")
        type_character(hwnd, "b")
        wait_ui(hwnd, "beta建议")
        ui(hwnd, "搜索关键词", "focus")
        press(hwnd, 0x28)
        press(hwnd, 0x0D)
        q.wait_for(lambda: any(word == "beta建议" for _, word in server.searches), "keyboard selection submits the chosen suggestion")
        ui(hwnd, "搜索关键词", "set", "")
        ui(hwnd, "搜索关键词", "focus")
        type_character(hwnd, "a")
        wait_ui(hwnd, "alpha建议")
        physical_click(hwnd, "搜索历史")
        q.wait_for(lambda: not suggestions_visible(hwnd), "light dismiss closes suggestions")
        assert q.close(proc, hwnd) < 4000
        print(json.dumps({"physical_typing": True, "no_early_search": True, "keyboard_selection": True,
                          "light_dismiss": True, "suggestion_requests": len(server.searches)}, ensure_ascii=False))
    except Exception:
        print(q.log(profile))
        print("SEARCH REQUESTS", server.searches)
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=10)


if __name__ == "__main__":
    q.user.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    with tempfile.TemporaryDirectory(prefix="autocomplete-regression-", dir=Path(__file__).parent) as temp:
        with SearchFixtureServer(("127.0.0.1", 0), Fixture) as server:
            server.searches = []
            server.retry_ready = False
            server.slow = Event()
            server.stale = Event()
            Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(Path(sys.argv[1]).resolve(), Path(temp), server)
            finally:
                server.slow.set()
                server.stale.set()
                server.shutdown()
