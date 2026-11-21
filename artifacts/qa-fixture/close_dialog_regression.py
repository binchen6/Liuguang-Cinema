import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import window_regression as q


def ui(hwnd, text, action="find", value=None):
    command = Path(__file__).with_name("window_control.ps1")
    arguments = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(command), str(hwnd), text, action]
    if value is not None: arguments.append(value)
    result = subprocess.run(arguments, timeout=12, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise AssertionError(f"{action} {text}: {result.stderr.decode('utf-8', errors='replace')}")
    return result.stdout.decode("utf-8", errors="replace").strip()


def make_profile(root, base, name):
    profile = q.make_profile(root, base, name, "available", False)
    with q.closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("DELETE FROM settings WHERE key='ui.closeBehavior'")
    return profile


def read_default(profile):
    with q.closing(sqlite3.connect(profile / "tv.db")) as db:
        item = db.execute("SELECT value FROM settings WHERE key='ui.closeBehavior'").fetchone()
        return item[0] if item else None


def start(app, profile, processes):
    offset = len(q.log(profile))
    proc = q.launch(app, profile)
    processes.append(proc)
    hwnd = q.wait_for(lambda: q.find_window(proc.pid), "main window")
    q.log_after(profile, "Vod home ready", offset)
    return proc, hwnd


def exercise(app, base, root):
    processes = []
    try:
        tray = make_profile(root, base, "tray")
        process, hwnd = start(app, tray, processes)
        q.user.PostMessageW(hwnd, 0x10, 0, 0)
        ui(hwnd, "取消", "invoke")
        assert q.user.IsWindowVisible(hwnd) and process.poll() is None and read_default(tray) is None
        time.sleep(.35)
        q.user.PostMessageW(hwnd, 0x10, 0, 0)
        ui(hwnd, "记住此选择", "toggle")
        ui(hwnd, "最小化到托盘", "invoke")
        try:
            tray_available = bool(q.wait_for(lambda: q.has_icon(hwnd), "tray icon registered", 5))
        except AssertionError:
            tray_available = False
        if tray_available:
            q.wait_for(lambda: not q.user.IsWindowVisible(hwnd), "close hides to the tray")
            assert read_default(tray) == "1"

            q.user.PostMessageW(hwnd, 0x8001, 0, 0x10400)
            q.wait_for(lambda: q.user.IsWindowVisible(hwnd), "restored after using remembered tray choice")
            offset = len(q.log(tray))
            q.user.PostMessageW(hwnd, 0x10, 0, 0)
            q.log_after(tray, "Window moved to tray", offset)
            assert not q.user.IsWindowVisible(hwnd)
            tray_note = "hidden to tray, restored, hidden again"
        else:
            # 本会话的 .NET 进程无法注册托盘图标，应用会优雅降级：记住的偏好仍然保存，
            # 窗口留在任务栏并保持可用。
            tray_note = "sandboxed: Shell_NotifyIcon unavailable, preference saved with taskbar fallback"
            q.wait_for(lambda: read_default(tray) == "1", "remembered tray preference saved")
            time.sleep(.8)
            assert process.poll() is None and q.user.IsWindowVisible(hwnd), "window stays usable without tray"
            assert "系统托盘不可用" in q.log(tray) or "窗口未收起到托盘" in q.log(tray), q.log(tray)[-600:]

        exit_profile = make_profile(root, base, "exit")
        exiting, exit_hwnd = start(app, exit_profile, processes)
        q.user.PostMessageW(exit_hwnd, 0x10, 0, 0)
        ui(exit_hwnd, "记住此选择", "toggle")
        ui(exit_hwnd, "退出", "invoke")
        assert exiting.wait(timeout=12) == 0 and read_default(exit_profile) == "2"
        relaunched, relaunched_hwnd = start(app, exit_profile, processes)
        elapsed = q.close(relaunched, relaunched_hwnd)
        assert elapsed < 2500
        print(json.dumps({"cancel": True, "remember_tray": tray_note, "remember_exit": True, "direct_exit_ms": elapsed}))
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)


if __name__ == "__main__":
    app = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="close-choices-", dir=Path(__file__).parent) as root:
        with q.FixtureServer(("127.0.0.1", 0), q.Fixture) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(app, f"http://127.0.0.1:{server.server_port}", Path(root))
            finally:
                server.shutdown()
