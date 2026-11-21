import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import time
import urllib.request
import window_regression as q
from close_dialog_regression import ui


def events(base):
    with urllib.request.urlopen(base + "/events", timeout=3) as response:
        return json.load(response)


def progress(profile):
    try:
        with q.closing(sqlite3.connect(profile / "tv.db")) as db:
            item = db.execute("SELECT position FROM history WHERE title = '播放验收样片'").fetchone()
            return item[0] if item else 0
    except sqlite3.OperationalError:
        return 0


def exercise(app, base, root):
    profile = q.make_profile(root, base, "desktop", "playback", False)
    config = {"sites": [{"key": "playback", "type": 4, "name": "播放器验收", "api": base + "/api"}]}
    (profile / "config" / "last-good.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("INSERT INTO settings VALUES ('ui.animations','false')")
    before = len(events(base))
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "test player main window")
        q.log_after(profile, "Vod home ready")
        ui(hwnd, "播放验收样片", "invoke")
        ui(hwnd, "正片", "invoke")
        q.log_after(profile, "嗅探已找到媒体")
        q.wait_for(lambda: progress(profile) > 0, "LibVLC playback saves a positive position", 18)
        seen = events(base)[before:]
        assert "/orphan.m4s" in seen and "/denied.mp4" in seen and "/manifest" in seen
        assert any(item in seen for item in ["/v420.m3u8", "/v720.m3u8", "/v1080.m3u8"])
        tested = {}
        for label, height in (("420p", 420), ("720p", 720), ("原画（1080p）", 1080), ("自动", None)):
            mark = len(events(base))
            q.wake_player_controls(hwnd)
            ui(hwnd, "清晰度", "expand")
            assert ui(hwnd, label, "menu-enabled") == "True"
            ui(hwnd, label, "toggle")
            def has_media():
                requests = events(base)[mark:]
                choices = [height] if height else [420, 720, 1080]
                return any(any(path.startswith(f"/v{value}") and path.endswith(".ts") for path in requests)
                           and f"/v{value}.m3u8" in requests for value in choices)
            q.wait_for(has_media, f"selected {label} media playlist and segment requested", 15)
            tested[label] = height or "adaptive"
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "下载")
        mark = len(q.log(profile))
        ui(hwnd, "正片", "invoke")
        q.log_after(profile, "Play requested", mark)
        print(json.dumps({"sniff": "2xx extensionless manifest selected during desktop playback", "quality_choices": tested,
                          "history_position_ms": progress(profile), "back_to_detail_and_reenter": True}))
        q.close(proc, hwnd)
    except Exception:
        print(q.log(profile))
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=8)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="desktop-playback-", dir=Path(__file__).parent) as data:
        exercise(Path(sys.argv[1]).resolve(), sys.argv[2], Path(data))
