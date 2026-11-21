import argparse
from contextlib import closing
import ctypes as c
from ctypes import wintypes as w
import http.server
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

user = c.WinDLL("user32", use_last_error=True)
shell = c.WinDLL("shell32", use_last_error=True)
window_callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
user.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
user.IsWindowVisible.argtypes = [w.HWND]
user.IsIconic.argtypes = [w.HWND]
user.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
user.ShowWindow.argtypes = [w.HWND, c.c_int]
user.MoveWindow.argtypes = [w.HWND, c.c_int, c.c_int, c.c_int, c.c_int, w.BOOL]
user.IsZoomed.argtypes = [w.HWND]


class IconIdentifier(c.Structure):
    _fields_ = [("size", w.DWORD), ("window", w.HWND), ("id", w.UINT),
                ("guid", c.c_ubyte * 16)]


shell.Shell_NotifyIconGetRect.argtypes = [c.POINTER(IconIdentifier), c.POINTER(w.RECT)]
shell.Shell_NotifyIconGetRect.restype = c.c_long


def has_icon(hwnd):
    identifier = IconIdentifier(c.sizeof(IconIdentifier), hwnd, 1)
    return shell.Shell_NotifyIconGetRect(c.byref(identifier), c.byref(w.RECT())) == 0


def find_window(pid):
    found = []

    @window_callback
    def callback(hwnd, _):
        current = w.DWORD()
        user.GetWindowThreadProcessId(hwnd, c.byref(current))
        if current.value == pid:
            name = c.create_unicode_buffer(256)
            user.GetClassNameW(hwnd, name, len(name))
            if name.value == "WinUIDesktopWin32WindowClass":
                found.append(hwnd)
        return True

    user.EnumWindows(callback, 0)
    return next(iter(found), None)


def wait_for(check, name, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if result := check():
            return result
        time.sleep(.025)
    raise AssertionError(name)


def wake_player_controls(hwnd):
    rect = w.RECT()
    user.GetClientRect(hwnd, c.byref(rect))
    point = w.POINT(rect.right // 2, rect.bottom // 2)
    user.ClientToScreen(hwnd, c.byref(point))
    user.SetForegroundWindow(hwnd)
    user.SetCursorPos(point.x + 4, point.y)
    time.sleep(.1)
    user.SetCursorPos(point.x, point.y)
    time.sleep(.15)


def log(profile):
    logfile = profile / "diagnostic.log"
    return logfile.read_text(encoding="utf-8") if logfile.exists() else ""


def log_after(profile, marker, offset=0):
    return wait_for(lambda: marker in log(profile)[offset:], f"missing event: {marker}")


class Fixture(http.server.BaseHTTPRequestHandler):
    offline = False
    requests = []

    def do_GET(self):
        path = urllib.parse.urlsplit(self.path).path
        Fixture.requests.append(path)
        if path == "/blocked" or Fixture.offline:
            self.send_error(403)
            return
        if path == "/api/search":
            data = {"code": 0, "data": {"total": 3, "merged_by_type": {
                "quark": [
                    {"url": "https://pan.quark.cn/s/fixtureShare", "note": "Test Film", "password": "a+b#"},
                    {"url": "https://pan.quark.cn/s/fixtureShare?pwd=a%2Bb%23", "note": "Duplicate"},
                    {"url": "https://spoof.local/s/fixtureShare", "note": "Ignored"}]}}}
        elif path == "/empty":
            data = {"class": [{"type_id": "1", "type_name": "验收分类"}], "list": []}
        else:
            data = {"class": [{"type_id": "1", "type_name": "电影"}], "list": [
                {"vod_id": "local-film", "vod_name": "启动验收影片", "type_name": "电影"}],
                "page": 1, "pagecount": 1}
        raw = json.dumps(data, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, *_):
        pass


class FixtureServer(http.server.ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        if not isinstance(sys.exception(), (BrokenPipeError, ConnectionResetError)):
            super().handle_error(request, client_address)


def make_profile(root, base, name, preferred, tray=True):
    profile = root / name
    (profile / "config").mkdir(parents=True)
    (profile / "config" / "last-good.json").write_text(json.dumps({"sites": [
        {"key": "blocked", "name": "启动阻断测试", "type": 4, "api": base + "/blocked"},
        {"key": "available", "name": "启动可用测试", "type": 4, "api": base + "/ok"},
        {"key": "pansou", "name": "盘搜", "type": 3, "api": "csp_S_zpsGuard", "ext": {"siteUrl": base}}
    ]}, ensure_ascii=False), encoding="utf-8")
    with closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.executemany("INSERT INTO settings VALUES (?,?)", [
            ("source.autoRefresh", "false"), ("source.homeSite", preferred),
            ("douban.enabled", "false"),
            ("ui.theme", "1"), ("ui.closeBehavior", "2"), ("ui.minimizeToTray", "true" if tray else "false")])
        db.execute("CREATE TABLE metadata (key TEXT PRIMARY KEY, json TEXT NOT NULL, expires INTEGER NOT NULL)")
        db.execute("INSERT INTO metadata VALUES ('douban.trending.movies', '{\"subjects\":[]}', ?)",
                   (int((time.time() + 3600) * 1000),))
    return profile


def launch(path, profile):
    return subprocess.Popen([str(path)], env={**os.environ, "TV_DATA_ROOT": str(profile.resolve())},
                            cwd=str(path.parent), creationflags=subprocess.CREATE_NO_WINDOW)


def close(process, hwnd):
    start = time.monotonic()
    user.PostMessageW(hwnd, 0x10, 0, 0)
    code = process.wait(timeout=15)
    assert code == 0, code
    return round((time.monotonic() - start) * 1000)


def run(exe, baseline, root, base):
    processes = []
    result = {}
    try:
        profile = make_profile(root, base, "fallback", "blocked")
        started = time.monotonic()
        current = launch(exe, profile)
        processes.append(current)
        hwnd = wait_for(lambda: find_window(current.pid), "1.1.1 window")
        log_after(profile, "Window activated")
        result["window_ms"] = round((time.monotonic() - started) * 1000)
        log_after(profile, "Vod home ready: available, 1 items")
        assert re.search(r"首页候选不可用 \(blocked\): .*403", log(profile))
        result["first_home_ms"] = round((time.monotonic() - started) * 1000)

        user.ShowWindow(hwnd, 3)
        wait_for(lambda: user.IsZoomed(hwnd), "maximize")
        tray_available = False
        try:
            tray_available = bool(wait_for(lambda: has_icon(hwnd), "tray icon registered", 5))
        except AssertionError:
            tray_available = False
        user.PostMessageW(hwnd, 0x112, 0xf020, 0)
        if tray_available:
            wait_for(lambda: not user.IsWindowVisible(hwnd), "window hidden after minimize")
            log_after(profile, "Window moved to tray")
            user.PostMessageW(hwnd, 0x8001, 0, 0x10400)
            wait_for(lambda: user.IsWindowVisible(hwnd) and not user.IsIconic(hwnd), "notification activation restored")
            assert user.IsZoomed(hwnd), "restore previous maximized state"
            wait_for(lambda: not has_icon(hwnd), "notification icon removed")
        else:
            time.sleep(1.2)
            assert current.poll() is None, "app survived minimize without tray"
            if "Window moved to tray" in log(profile) and not user.IsWindowVisible(hwnd):
                tray_available = True
                result["tray"] = "registered; shell icon enumeration unavailable, window restore verified"
                user.PostMessageW(hwnd, 0x8001, 0, 0x10400)
                wait_for(lambda: user.IsWindowVisible(hwnd) and not user.IsIconic(hwnd), "notification activation restored")
                assert user.IsZoomed(hwnd), "restore previous maximized state"
            else:
                assert "系统托盘不可用" in log(profile) or "窗口未收起到托盘" in log(profile), log(profile)[-600:]
                assert user.IsIconic(hwnd) or not user.IsWindowVisible(hwnd), "minimize still restores taskbar state"
                user.ShowWindow(hwnd, 9)
                wait_for(lambda: user.IsWindowVisible(hwnd) and not user.IsIconic(hwnd), "restored from minimize fallback")
                result["tray"] = "sandboxed: Shell_NotifyIcon unavailable, graceful taskbar fallback verified"

        user.PostMessageW(hwnd, 0x112, 0xf020, 0)
        wait_for(lambda: user.IsIconic(hwnd) if not tray_available else not user.IsWindowVisible(hwnd), "second minimize")
        redirect = launch(exe, profile)
        processes.append(redirect)
        assert redirect.wait(timeout=15) == 0
        wait_for(lambda: user.IsWindowVisible(hwnd) and not user.IsIconic(hwnd), "second launch restored original")
        user.ShowWindow(hwnd, 9)
        user.MoveWindow(hwnd, 45, 45, 250, 400, True)
        time.sleep(.35)
        assert current.poll() is None, "window survived narrow layout"
        error_log = profile / "last-error.log"
        if error_log.exists():
            assert "DivideByZeroException" not in error_log.read_text(encoding="utf-8")
        if tray_available:
            result["tray"] = "hidden after Shell_NotifyIcon success, activation restored, relaunch restored same window"
        result["new_idle_exit_ms"] = close(current, hwnd)
        assert not has_icon(hwnd)

        Fixture.offline = True
        offset = len(log(profile))
        started = time.monotonic()
        cached = launch(exe, profile)
        processes.append(cached)
        cached_hwnd = wait_for(lambda: find_window(cached.pid), "cached instance window")
        log_after(profile, "Vod home ready: available, 1 items", offset)
        result["cached_home_ms"] = round((time.monotonic() - started) * 1000)
        close(cached, cached_hwnd)
        Fixture.offline = False

        disabled = make_profile(root, base, "disabled", "available", tray=False)
        normal = launch(exe, disabled)
        processes.append(normal)
        normal_hwnd = wait_for(lambda: find_window(normal.pid), "normal minimize instance window")
        log_after(disabled, "Vod home ready: available")
        user.ShowWindow(normal_hwnd, 6)
        wait_for(lambda: user.IsIconic(normal_hwnd), "standard minimize when tray disabled")
        time.sleep(.25)
        assert user.IsWindowVisible(normal_hwnd) and not has_icon(normal_hwnd)
        close(normal, normal_hwnd)
        result["tray_off"] = "normal taskbar minimize"

        before = make_profile(root, base, "before", "available", tray=False)
        started = time.monotonic()
        old = launch(baseline, before)
        processes.append(old)
        old_window = wait_for(lambda: find_window(old.pid), "1.1.0 window")
        log_after(before, "VodPage initialized")
        result["old_home_ms"] = round((time.monotonic() - started) * 1000)
        result["old_idle_exit_ms"] = close(old, old_window)
        return result
    except Exception:
        print("process status", [(p.pid, p.poll()) for p in processes], "requests", Fixture.requests)
        raise
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("exe", type=Path)
    parser.add_argument("baseline", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="window-regression-", dir=Path(__file__).parent) as temporary:
        with FixtureServer(("127.0.0.1", 0), Fixture) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                print(json.dumps(run(args.exe.resolve(), args.baseline.resolve(), Path(temporary),
                                     f"http://127.0.0.1:{server.server_port}"), ensure_ascii=False))
            except Exception:
                for path in sorted(Path(temporary).glob("*/*.log")):
                    print(path.name, path.parent.name, path.read_text(encoding="utf-8"))
                raise
            finally:
                server.shutdown()
