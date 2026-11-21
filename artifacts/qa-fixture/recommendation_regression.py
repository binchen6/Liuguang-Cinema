import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import threading
import time
import window_regression as q
from close_dialog_regression import ui


def prepare(root, base, name, empty=False):
    profile = q.make_profile(root, base, name, "available", False)
    if empty:
        config = profile / "config" / "last-good.json"
        data = json.loads(config.read_text(encoding="utf-8"))
        next(site for site in data["sites"] if site["key"] == "available")["api"] = base + "/empty"
        config.write_text(json.dumps(data), encoding="utf-8")
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("UPDATE metadata SET json=? WHERE key='douban.trending.movies'", (json.dumps({"subjects": [
            {"id": "123456", "title": "豆瓣推荐验收影片", "rate": "8.5", "cover": ""}
        ]}),))
    return profile


def choice(profile):
    with q.closing(sqlite3.connect(profile / "tv.db")) as db:
        row = db.execute("SELECT value FROM settings WHERE key='vod.recommendations'").fetchone()
        return row[0] if row else "0"

def assert_options_near_button(hwnd, button):
    choices = [json.loads(ui(hwnd, name, "menu-bounds")) for name in ("站点推荐", "豆瓣推荐")]
    left = min(choice["x"] for choice in choices)
    right = max(choice["x"] + choice["w"] for choice in choices)
    top = min(choice["y"] for choice in choices)
    bottom = max(choice["y"] + choice["h"] for choice in choices)
    assert max(left, button["x"]) < min(right, button["x"] + button["w"]) + 12, (button, choices)
    assert min(abs(top - button["y"] - button["h"]), abs(bottom - button["y"])) < 32, (button, choices)

def open_recommendation(hwnd):
    ui(hwnd, "设置内容", "scroll-bottom")
    button = json.loads(ui(hwnd, "推荐来源", "bounds"))
    assert button["visible"] and button["w"] > 0 and button["h"] > 0, button
    ui(hwnd, "推荐来源", "expand")
    assert_options_near_button(hwnd, button)


def exercise(app, base, root):
    profile = prepare(root, base, "choice")
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "main window")
        q.log_after(profile, "Vod home ready: available")
        ui(hwnd, "启动验收影片")
        ui(hwnd, "设置", "invoke")
        ui(hwnd, "外观与内容", "expand")
        open_recommendation(hwnd)
        ui(hwnd, "豆瓣推荐", "toggle")
        q.wait_for(lambda: choice(profile) == "1", "selection saved")
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "豆瓣推荐验收影片")
        q.close(proc, hwnd)
        proc = q.launch(app, profile)
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "window relaunched")
        ui(hwnd, "豆瓣推荐验收影片")
        ui(hwnd, "设置", "invoke")
        ui(hwnd, "外观与内容", "expand")
        q.user.MoveWindow(hwnd, 38, 52, 840, 615, True)
        open_recommendation(hwnd)
        ui(hwnd, "站点推荐", "toggle")
        q.wait_for(lambda: choice(profile) == "0", "site selection saved")
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "启动验收影片")
        q.close(proc, hwnd)

        empty = prepare(root, base, "empty", True)
        proc = q.launch(app, empty)
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "fallback window")
        ui(hwnd, "豆瓣推荐验收影片")
        ui(hwnd, "验收分类")
        q.close(proc, hwnd)
        print(json.dumps({"site_default": True, "switches_both_ways": True, "anchored_wide_and_narrow": True,
                          "persisted_on_restart": True, "fallback_keeps_site_categories": True}))
    except Exception:
        print(q.log(profile))
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=8)


if __name__ == "__main__":
    app = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="recommendations-", dir=Path(__file__).parent) as root:
        with q.FixtureServer(("127.0.0.1", 0), q.Fixture) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(app, f"http://127.0.0.1:{server.server_port}", Path(root))
            finally:
                server.shutdown()
