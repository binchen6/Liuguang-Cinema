import ctypes
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time
from threading import Thread

import window_regression as q
from close_dialog_regression import ui


def position(profile):
    try:
        with q.closing(sqlite3.connect(profile / "tv.db")) as db:
            row = db.execute("SELECT position FROM history WHERE title='本地库样片'").fetchone()
            return row[0] if row else 0
    except sqlite3.OperationalError:
        return 0


def local_count(profile):
    with q.closing(sqlite3.connect(profile / "tv.db")) as db:
        return db.execute("SELECT COUNT(*) FROM local_files").fetchone()[0]


def working_set(pid):
    command = f"(Get-Process -Id {pid}).WorkingSet64"
    return int(subprocess.check_output(["powershell", "-NoProfile", "-Command", command], creationflags=subprocess.CREATE_NO_WINDOW).strip()) // (1024 * 1024)


def exercise(app, root, fixture):
    sample = root / "本地库样片.mp4"
    sample.write_bytes((Path(__file__).parent / "sample.mp4").read_bytes())
    sample.with_suffix(".srt").write_text("1\n00:00:00,000 --> 00:00:02,000\n本地字幕\n", encoding="utf-8")
    sample.with_suffix(".xml").write_text('<i><d p="1,1,25,16777215,0,0,0,0">本地弹幕</d></i>', encoding="utf-8")
    profile = q.make_profile(root, fixture, "local-library", "available", False)
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("CREATE TABLE local_files (path TEXT PRIMARY KEY COLLATE NOCASE, title TEXT NOT NULL, folder TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, added INTEGER NOT NULL)")
        db.execute("INSERT INTO local_files VALUES (?,?,?,?,?,?)", (str(sample), "本地库样片", "", sample.stat().st_size, 0, 1))
    full = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(full.pid), "local library window")
        q.log_after(profile, "Vod home ready")
        full_mb = working_set(full.pid)
        ui(hwnd, "媒体库", "select")
        ui(hwnd, "本地视频", "select")
        ui(hwnd, "本地库样片", "invoke")
        q.log_after(profile, "Play requested: Vod; site=local")
        q.wait_for(lambda: position(profile) > 0, "local library video actually advances", 20)
        full_playing_mb = working_set(full.pid)
        q.wake_player_controls(hwnd)
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "从本地库移除", "invoke")
        q.wait_for(lambda: local_count(profile) == 0, "local index row removed")
        assert sample.exists(), "removing a local library entry must not delete the source file"
        assert q.close(full, hwnd) < 4000
    finally:
        if full.poll() is None:
            full.terminate()
            full.wait(timeout=10)

    direct_profile = q.make_profile(root, fixture, "direct-player", "available", False)
    direct = subprocess.Popen([str(app), str(sample)], env={**os.environ, "TV_DATA_ROOT": str(direct_profile)}, cwd=str(app.parent), creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        hwnd = q.wait_for(lambda: q.find_window(direct.pid), "direct player window")
        q.log_after(direct_profile, "Application launch (direct local player)")
        q.wait_for(lambda: position(direct_profile) > 0, "direct local player actually advances", 20)
        direct_mb = working_set(direct.pid)
        log = q.log(direct_profile)
        assert "Vod reload started" not in log and "Vod config loaded" not in log, log
        q.wake_player_controls(hwnd)
        ui(hwnd, "播放暂停")
        assert q.close(direct, hwnd) < 4000
        assert sample.exists()
        print(json.dumps({"library_playback": True, "remove_index_keeps_file": True, "direct_playback": True,
                          "full_working_set_mb": full_mb, "full_playing_working_set_mb": full_playing_mb,
                          "direct_working_set_mb": direct_mb}, ensure_ascii=False))
    finally:
        if direct.poll() is None:
            direct.terminate()
            direct.wait(timeout=10)


if __name__ == "__main__":
    q.user.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    with tempfile.TemporaryDirectory(prefix="local-library-regression-", dir=Path(__file__).parent) as temp:
        with q.FixtureServer(("127.0.0.1", 0), q.Fixture) as server:
            Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(Path(sys.argv[1]).resolve(), Path(temp), f"http://127.0.0.1:{server.server_port}")
            finally:
                server.shutdown()
