import ctypes
import json
from pathlib import Path
import sys
import tempfile
from threading import Event, Thread
import time

from close_dialog_regression import ui
from search_regression import Fixture, SearchFixtureServer, SITES
import window_regression as q


def bounds(hwnd, name):
    box = json.loads(ui(hwnd, name, "bounds"))
    assert box["visible"] and box["w"] > 0 and box["h"] > 0, (name, box)
    return box


def rect(hwnd):
    r = q.w.RECT()
    q.user.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def exercise(exe, folder, server):
    base = f"http://127.0.0.1:{server.server_port}"
    profile = q.make_profile(folder, base, "layout", "a", False)
    config = {"sites": [{"key": key, "type": 4, "name": name, "api": f"{base}/search/{key}"} for key, name in SITES]}
    (profile / "config/last-good.json").write_text(json.dumps(config), encoding="utf-8")
    processes = []

    def start():
        mark = len(q.log(profile))
        proc = q.launch(exe, profile)
        processes.append(proc)
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "layout window")
        q.log_after(profile, "Vod home ready", mark)
        return proc, hwnd

    try:
        proc, hwnd = start()
        work = q.w.RECT()
        assert q.user.SystemParametersInfoW(0x30, 0, ctypes.byref(work), 0)
        w, h = work.right - work.left, work.bottom - work.top
        first = rect(hwnd)
        expected = [min(w, max(820, round(w * .55))), min(h, max(600, round(h * .55)))]
        assert all(abs(a - b) <= 2 for a, b in zip(first[2:], expected)), (first, expected)
        assert abs(first[0] - work.left - (w-first[2])/2) < 3
        assert abs(first[1] - work.top - (h-first[3])/2) < 3
        q.user.MoveWindow(hwnd, work.left+12, work.top+15, min(w-24, 944), min(h-30, 673), True)
        time.sleep(.5)
        resized = rect(hwnd)
        q.close(proc, hwnd)
        proc, hwnd = start()
        assert rect(hwnd) == resized, (rect(hwnd), resized)
        q.user.ShowWindow(hwnd, 3)
        q.wait_for(lambda: q.user.IsZoomed(hwnd), "maximize")
        time.sleep(.5)
        q.close(proc, hwnd)
        proc, hwnd = start()
        assert q.user.IsZoomed(hwnd)
        print("PASS window first launch, resize/restart and maximize/restart", first, resized, flush=True)
        search = bounds(hwnd, "打开搜索")
        refresh = bounds(hwnd, "重新加载")
        assert search["x"] > work.left+w*.6 and search["x"] < refresh["x"]
        initial_filter_y = bounds(hwnd, "筛选")["y"]
        ui(hwnd, "点播海报", "scroll-bottom")
        bounds(hwnd, "回到顶部")
        sticky_y = bounds(hwnd, "筛选")["y"]
        assert sticky_y < initial_filter_y - 35, (sticky_y, initial_filter_y)
        ui(hwnd, "回到顶部", "invoke")
        bounds(hwnd, "重新加载")
        print("PASS pinned filters and back to top", initial_filter_y, sticky_y, flush=True)
        ui(hwnd, "投屏", "select")
        center = bounds(hwnd, "开始接收")
        stage = bounds(hwnd, "投屏内容")
        assert abs(center["x"]+center["w"]/2 - stage["x"]-stage["w"]/2) < 30, (center, stage)
        ui(hwnd, "设置", "invoke")
        settings = bounds(hwnd, "设置内容")
        import_btn = bounds(hwnd, "导入仓库")
        assert 0 < import_btn["x"] - settings["x"] < settings["w"]*.2
        print("PASS wide cast and settings", center, import_btn, flush=True)
        ui(hwnd, "网盘", "expand")
        from popup_regression import verify
        verify(hwnd, "UC 网盘扫码", "关闭扫码")
        ui(hwnd, "等待扫码")
        ui(hwnd, "关闭扫码", "invoke")
        print("PASS UC QR ready and anchored", flush=True)
        ui(hwnd, "点播", "select")
        ui(hwnd, "打开搜索", "invoke")
        ui(hwnd, "搜索关键词", "set", "山海")
        ui(hwnd, "搜索", "invoke")
        ui(hwnd, "山海·快站")
        ui(hwnd, "慢站", "select")
        left = bounds(hwnd, "慢站")
        right = bounds(hwnd, "山海·慢站")
        assert left["x"]+left["w"] <= right["x"]+2, (left, right)
        ui(hwnd, "山海·慢站", "invoke")
        ui(hwnd, "下载")
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "山海·慢站")
        assert ui(hwnd, "搜索关键词", "read") == "山海"
        print("PASS right-side search, left site switch, right poster and detail return", left, right, flush=True)
        q.close(proc, hwnd)
    except Exception:
        print("layout process status", processes[-1].poll(), flush=True)
        print(q.log(profile)[-4000:], flush=True)
        error = profile / "last-error.log"
        if error.exists():
            print(error.read_text(encoding="utf-8")[-3000:], flush=True)
        raise
    finally:
        for proc in processes:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=10)


if __name__ == "__main__":
    q.user.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    with tempfile.TemporaryDirectory(prefix="layout-", dir=Path(__file__).parent, ignore_cleanup_errors=True) as temp:
        with SearchFixtureServer(("127.0.0.1", 0), Fixture) as server:
            server.searches = []
            server.many_posters = server.retry_ready = True
            server.slow = Event()
            server.stale = Event()
            server.slow.set()
            Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(Path(sys.argv[1]).resolve(), Path(temp), server)
            finally:
                server.shutdown()
