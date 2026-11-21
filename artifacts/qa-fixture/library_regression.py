import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import threading
import time
import window_regression as q
from close_dialog_regression import ui


def exercise(app, base, root):
    profile = q.make_profile(root, base, "library", "available")
    now = int(time.time() * 1000)
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.executescript("""
        CREATE TABLE history (id TEXT PRIMARY KEY,title TEXT NOT NULL,picture TEXT NOT NULL,kind TEXT NOT NULL,
            subtitle TEXT NOT NULL,position INTEGER NOT NULL,duration INTEGER NOT NULL,updated INTEGER NOT NULL,request TEXT NOT NULL);
        CREATE TABLE favorites (id TEXT PRIMARY KEY,title TEXT NOT NULL,picture TEXT NOT NULL,kind TEXT NOT NULL,
            subtitle TEXT NOT NULL,added INTEGER NOT NULL,data TEXT NOT NULL);
        CREATE TABLE downloads (path TEXT PRIMARY KEY COLLATE NOCASE,title TEXT NOT NULL,episode TEXT NOT NULL,added INTEGER NOT NULL);
        """)
        db.executemany("INSERT INTO history VALUES (?,?,?,?,?,?,?,?,?)",
                       [("a", "历史验收剧集", "", "Vod", "测试站 第02集", 9700, 10000, now, "{}"),
                        ("b", "另一段视频", "", "Vod", "样片", 200, 2000, now - 1000, "{}")])
        db.execute("INSERT INTO favorites VALUES ('a','收藏验收剧集','','Vod','测试站 第02集',?,'{}')", (now,))
        db.execute("INSERT INTO downloads VALUES ('C:/example.invalid/video.mkv','下载验收剧集','第02集',?)", (now,))
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "Main window")
        q.log_after(profile, "Vod home ready")
        ui(hwnd, "媒体库", "select")
        ui(hwnd, "历史验收剧集")
        ui(hwnd, "已看完")
        ui(hwnd, "搜索媒体库", "set", "没有此片")
        ui(hwnd, "没有匹配项")
        ui(hwnd, "搜索媒体库", "set", "剧集 第02集")
        ui(hwnd, "历史验收剧集")
        ui(hwnd, "收藏", "select")
        ui(hwnd, "收藏验收剧集")
        ui(hwnd, "下载", "select")
        ui(hwnd, "下载验收剧集")
        ui(hwnd, "设置", "invoke")
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "下载验收剧集")
        q.close(proc, hwnd)
        print(json.dumps({"filters_all_tabs": True, "complete_marker": True, "search_and_tab_preserved": True}))
    except Exception:
        print(q.log(profile))
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=8)


if __name__ == "__main__":
    app = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="library-", dir=Path(__file__).parent) as root:
        with q.FixtureServer(("127.0.0.1", 0), q.Fixture) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(app, f"http://127.0.0.1:{server.server_port}", Path(root))
            finally:
                server.shutdown()
