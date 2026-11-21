from contextlib import closing, suppress
import ctypes as ctypes
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
from threading import Event, Thread
import time
from urllib.parse import parse_qs, urlsplit

from close_dialog_regression import ui
from popup_regression import verify
import window_regression as q

PY_ROOT = Path(__file__).parent
SITES = (("a", "快站"), ("b", "慢站"), ("c", "重试站"), ("d", "空站"))

class SearchFixtureServer(q.FixtureServer):
    def handle_error(self, request, client_address):
        if not isinstance(sys.exception(), ConnectionAbortedError):
            super().handle_error(request, client_address)


class Fixture(q.Fixture):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        parts = urlsplit(self.path)
        site = parts.path.rsplit("/", 1)[-1]
        params = parse_qs(parts.query)
        keyword = params.get("wd", [None])[0]
        if keyword:
            self.server.searches.append((site, keyword))
            if site == "b" and keyword == "山海":
                self.server.slow.wait(30)
            if keyword == "过期":
                self.server.stale.wait(30)
            if site == "c" and keyword == "山海" and not self.server.retry_ready:
                self.send_error(403)
                return

        title = "点播验收" if not keyword else f"{keyword}·{dict(SITES)[site]}"
        films = [] if keyword and site == "d" else [{
            "vod_id": f"{site}-{keyword or 'home'}", "vod_name": title, "vod_year": "2024", "type_name": "电影",
            "vod_play_from": "本地验收", "vod_play_url": "正片$https://example.invalid/sample.mp4"
        }]
        if keyword == "山海" and site != "d":
            films.append({**films[0], "vod_id": f"{site}-shared", "vod_name": "山海剧场", "vod_year": "2020" if site == "c" else "2024"})
        if keyword == "滚动" and site == "a":
            films = [{**films[0], "vod_id": f"{site}-entry{index:02}", "vod_name": f"滚动条目{index:02}"}
                     for index in range(1, 31)]
        if not keyword and getattr(self.server, "many_posters", False) and "ids" not in params:
            films = [{**films[0], "vod_id": f"vod{index}", "vod_name": f"点播片目{index:02}"} for index in range(64)]
        if item := params.get("ids"):
            name = item[0].partition("-")[2]
            films[0]["vod_id"] = item[0]
            films[0]["vod_name"] = "点播验收" if name == "home" else f"{name}·{dict(SITES)[site]}"
            if name == "shared":
                films[0]["vod_name"] = "山海剧场"
                films[0]["vod_play_url"] = f"{dict(SITES)[site]}正片$https://example.invalid/{site}.mp4"
        raw = json.dumps({"class": [{"type_id": "1", "type_name": "电影"}], "list": films}, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        with suppress(ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            self.wfile.write(raw)


def search_history(profile):
    with closing(sqlite3.connect(profile / "tv.db")) as db:
        value = db.execute("SELECT value FROM settings WHERE key='search.history'").fetchone()
    return json.loads(value[0]) if value else []


def wait_ui(hwnd, name, timeout=8.0):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            return ui(hwnd, name)
        except AssertionError as exc:
            last = exc
            time.sleep(.25)
    raise last


def physical_click(hwnd, label, bounds="bounds"):
    box = json.loads(ui(hwnd, label, bounds))
    assert box["visible"] and box["w"] and box["h"], box
    q.user.SetForegroundWindow(hwnd)
    q.user.SetCursorPos(round(box["x"] + box["w"] / 2), round(box["y"] + box["h"] / 2))
    time.sleep(.2)
    cursor = q.w.POINT()
    q.user.GetPhysicalCursorPos(ctypes.byref(cursor))
    assert box["x"] <= cursor.x < box["x"] + box["w"] and box["y"] <= cursor.y < box["y"] + box["h"], (label, box, cursor.x, cursor.y)
    q.user.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(.07)
    q.user.mouse_event(0x0004, 0, 0, 0, 0)


def press(hwnd, *keys):
    time.sleep(.3)
    q.user.SetForegroundWindow(hwnd)
    for key in keys:
        q.user.keybd_event(key, 0, 0, 0)
    for key in reversed(keys):
        q.user.keybd_event(key, 0, 2, 0)


def screenshot(hwnd, filename="search-results.png"):
    from PIL import ImageGrab
    rect = q.w.RECT()
    q.user.GetWindowRect(hwnd, ctypes.byref(rect))
    ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True).save(PY_ROOT / filename)

def dark_caption_is_legible(hwnd):
    from PIL import ImageGrab
    rect = json.loads(ui(hwnd, "Minimize", "bounds"))
    x = round(rect["x"] + rect["w"] / 2)
    y = round(rect["y"] + rect["h"] / 2)
    sample = ImageGrab.grab(bbox=(x - 12, y - 8, x + 12, y + 8), all_screens=True).convert("L")
    return any(level >= 170 for level in sample.getdata())


def exercise(app, profile_root, server):
    base = f"http://127.0.0.1:{server.server_port}"
    profile = q.make_profile(profile_root, base, "search", "a", False)
    config = {"sites": [{"key": key, "type": 4, "name": name, "api": f"{base}/search/{key}"} for key, name in SITES]}
    (profile / "config" / "last-good.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    with closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.executemany("INSERT INTO settings VALUES (?,?)", [
            ("ui.animations", "false"), ("search.instant", "true"), ("search.history", '["历史甲","历史乙"]')
        ])
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "search window")
        q.log_after(profile, "Vod home ready: a")
        ui(hwnd, "打开搜索", "invoke")
        ui(hwnd, "搜索历史")
        ui(hwnd, "历史甲", "invoke")
        assert ui(hwnd, "搜索关键词", "read") == "历史甲"
        q.wait_for(lambda: all((site, "历史甲") in server.searches for site, _ in SITES), "history click submits search")
        history_request_count = len(server.searches)
        ui(hwnd, "搜索关键词", "set", "")
        wait_ui(hwnd, "搜索历史")
        ui(hwnd, "删除此条搜索记录", "invoke")
        q.wait_for(lambda: len(search_history(profile)) == 1, "single history entry deleted")
        ui(hwnd, "清空", "invoke")
        q.wait_for(lambda: not search_history(profile), "history cleared")
        ui(hwnd, "搜索关键词", "set", "山海")
        time.sleep(.85)
        assert len(server.searches) == history_request_count, server.searches
        ui(hwnd, "搜索关键词", "focus")
        press(hwnd, 0x0D)
        q.wait_for(lambda: ("b", "山海") in server.searches, "slow site dispatched")
        wait_ui(hwnd, "山海·快站")
        ui(hwnd, "重试")
        ui(hwnd, "无结果")
        assert not server.slow.is_set()
        verify(hwnd, "筛选", "年份")
        verify(hwnd, "排序", "相关度", "expand", item=True)
        press(hwnd, 0x1B)
        press(hwnd, 0x1B)
        server.retry_ready = True
        requests_before = len(server.searches)
        ui(hwnd, "重试", "invoke")
        wait_ui(hwnd, "山海·重试站")
        assert server.searches[requests_before:] == [("c", "山海")], server.searches
        server.slow.set()
        wait_ui(hwnd, "山海·慢站")
        wait_ui(hwnd, "山海 · 5 条结果 · 4 个站点")
        # 1.2 的结果卡比 1.1.9 大，6 条结果会占两行；先滚到结果区底部让第二行可见再点击。
        ui(hwnd, "搜索结果", "scroll-bottom")
        time.sleep(.3)
        ui(hwnd, "山海·重试站", "invoke")
        ui(hwnd, "下载")
        ui(hwnd, "返回", "invoke")
        time.sleep(.8)
        wait_ui(hwnd, "山海·重试站")
        assert ui(hwnd, "搜索关键词", "read") == "山海"
        ui(hwnd, "快站", "select")
        time.sleep(.4)
        wait_ui(hwnd, "山海·快站")
        ui(hwnd, "山海剧场", "invoke")
        time.sleep(.8)
        wait_ui(hwnd, "线路")
        wait_ui(hwnd, "本地验收")
        wait_ui(hwnd, "快站正片")
        verify(hwnd, "片源", "慢站", "expand", item=True)
        ui(hwnd, "慢站", "toggle")
        wait_ui(hwnd, "慢站正片")
        ui(hwnd, "返回", "invoke")
        time.sleep(.8)
        wait_ui(hwnd, "搜索关键词")
        q.user.MoveWindow(hwnd, 45, 55, 840, 615, True)
        time.sleep(.35)
        screenshot(hwnd)
        ui(hwnd, "搜索关键词", "set", "过期")
        ui(hwnd, "搜索", "invoke")
        q.wait_for(lambda: any(word == "过期" for _, word in server.searches), "superseded query started")
        ui(hwnd, "搜索关键词", "set", "新片")
        ui(hwnd, "搜索", "invoke")
        ui(hwnd, "新片·快站")
        server.stale.set()
        time.sleep(.45)
        assert ui(hwnd, "搜索关键词", "read") == "新片"
        assert search_history(profile)[0] == "新片"
        request_count = len(server.searches)
        ui(hwnd, "搜索关键词", "set", "")
        ui(hwnd, "搜索历史")
        time.sleep(.65)
        assert len(server.searches) == request_count
        press(hwnd, 0x12, 0x25)
        wait_ui(hwnd, "点播验收")
        press(hwnd, 0x11, 0x46)
        wait_ui(hwnd, "搜索关键词")
        ui(hwnd, "全部站点", "select")
        time.sleep(.4)
        ui(hwnd, "快站", "select")
        time.sleep(.4)
        mark = len(server.searches)
        ui(hwnd, "搜索关键词", "set", "滚动")
        ui(hwnd, "搜索", "invoke")
        wait_ui(hwnd, "滚动条目01")
        assert sorted(server.searches[mark:]) == [("a", "滚动"), ("b", "滚动"), ("c", "滚动"), ("d", "滚动")], server.searches
        ui(hwnd, "搜索结果", "scroll-bottom")
        assert float(ui(hwnd, "搜索结果", "scroll-percent")) > 90
        ui(hwnd, "滚动条目30", "invoke")
        ui(hwnd, "下载")
        ui(hwnd, "返回", "invoke")
        time.sleep(.8)
        wait_ui(hwnd, "滚动条目30")
        assert float(ui(hwnd, "搜索结果", "scroll-percent")) > 90
        ui(hwnd, "设置", "invoke")
        ui(hwnd, "外观与内容", "expand")
        ui(hwnd, "设置内容", "scroll-bottom")
        ui(hwnd, "主题", "expand")
        physical_click(hwnd, "深色", "menu-bounds")
        time.sleep(.8)
        assert dark_caption_is_legible(hwnd), "dark caption minimize glyph lacks a light foreground"
        screenshot(hwnd, "search-results-dark.png")
        assert q.close(proc, hwnd) < 3500
        print(json.dumps({"title_bar_click": True, "only_submit": True, "history_delete_clear": True,
                          "partial_results": True, "retry_single_site": True, "back_preserves_query": True,
                          "detail_line_and_episodes": True, "cancels_previous_query": True, "alt_left": True, "ctrl_f": True,
                          "scope_filters_results": True, "scroll_position_restored": True, "dark_caption_contrast": True,
                          "scope_and_filter_popups_anchored": True}))
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
    with tempfile.TemporaryDirectory(prefix="search-regression-", dir=PY_ROOT) as temp:
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
