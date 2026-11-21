"""Launch the app with a throw-away profile and capture a window screenshot.

Usage:
    py shot.py <exe> <out.png> [width] [height] [--theme dark|light]
               [--tag vod|live|cast|library|settings|search]

Notes:
 - TV_DATA_ROOT must live inside the workspace: profiles under %TEMP% crash the
   WinUI app in this session (0xC000027B).
 - The DSH GUI window is often on top; we temporarily raise the app with
   SetWindowPos(HWND_TOPMOST) so ImageGrab sees the app, then restore.
 - By default the app reads the real 饭太硬 config from the network so pages are
   filled with real posters. Pass --fixture for an isolated fake site config.
"""
import ctypes
import json
import os
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from pathlib import Path

from PIL import ImageGrab

sys.path.insert(0, str(Path(__file__).resolve().parent))
import window_regression as q
from close_dialog_regression import ui
from PIL import Image

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32), ("biHeight", ctypes.c_int32),
                ("biPlanes", ctypes.c_uint16), ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32), ("biClrImportant", ctypes.c_uint32)]


def print_window(hwnd):
    """PrintWindow(PW_RENDERFULLCONTENT) so occlusion by other windows does not matter."""
    gdi32 = ctypes.windll.gdi32
    u32 = ctypes.windll.user32
    rect = q.w.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(rect))
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if width <= 0 or height <= 0:
        return None
    hdc = u32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bitmap = gdi32.CreateCompatibleBitmap(hdc, width, height)
    gdi32.SelectObject(mem, bitmap)
    ok = u32.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT
    info = BITMAPINFOHEADER()
    info.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    info.biWidth, info.biHeight = width, -height
    info.biPlanes, info.biBitCount, info.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(width * height * 4)
    gdi32.GetDIBits(mem, bitmap, 0, height, buf, ctypes.byref(info), 0)
    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(mem)
    u32.ReleaseDC(hwnd, hdc)
    if not ok:
        return None
    return Image.frombuffer("RGBA", (width, height), buf, "raw", "BGRA", 0, 1).convert("RGB")

import close_dialog_regression as cdr

# The host display runs at 150%; a DPI-unaware script sees virtualized (96 DPI)
# window rects and would capture only a cropped part of the window. Become
# per-monitor-DPI-aware so GetWindowRect/ImageGrab work in physical pixels.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

user = ctypes.windll.user32
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001


def set_topmost(hwnd, topmost):
    user.SetWindowPos(hwnd, HWND_TOPMOST if topmost else HWND_NOTOPMOST, 0, 0, 0, 0,
                      SWP_NOMOVE | SWP_NOSIZE)


def main():
    exe = Path(sys.argv[1]).resolve()
    out = Path(sys.argv[2]).resolve()
    width = int(sys.argv[3]) if len(sys.argv) > 3 else 1408
    height = int(sys.argv[4]) if len(sys.argv) > 4 else 840
    theme, tag, fixture, scroll, scroll_pct, actions = "dark", None, False, None, None, []
    for i, arg in enumerate(sys.argv):
        if arg == "--theme":
            theme = sys.argv[i + 1]
        if arg == "--tag":
            tag = sys.argv[i + 1]
        if arg == "--fixture":
            fixture = True
        if arg == "--scroll":
            scroll = sys.argv[i + 1]
        if arg in ("--invoke", "--select"):
            actions.append((sys.argv[i + 1], "invoke" if arg == "--invoke" else "select"))
        if arg == "--scroll-pct":
            scroll_pct = sys.argv[i + 1]

    root = Path(__file__).resolve().parent.parent / "tmp" / f"shot-{time.strftime('%H%M%S')}-{os.getpid()}"
    profile = root / "profile"
    (profile / "config").mkdir(parents=True, exist_ok=True)
    if fixture:
        (profile / "config" / "last-good.json").write_text(
            json.dumps({"sites": [{"key": "wogg", "type": 4, "name": "玩偶哥哥",
                                   "api": "http://127.0.0.1:9/search"}]}, ensure_ascii=False),
            encoding="utf-8")
    with closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.executemany("INSERT OR REPLACE INTO settings VALUES (?,?)", [
            ("source.autoRefresh", "false"), ("source.homeSite", ""),
            ("douban.enabled", "true"),
            ("ui.theme", "1" if theme == "light" else "0"),
            ("ui.closeBehavior", "2"), ("ui.minimizeToTray", "false")])
        db.execute("CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, json TEXT NOT NULL, expires INTEGER NOT NULL)")
        db.execute("INSERT OR REPLACE INTO metadata VALUES ('douban.trending.movies', '{\"subjects\":[]}', ?)",
                   (int((time.time() + 3600) * 1000),))

    proc = q.launch(exe, profile)
    hwnd = None
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "window", 30)
        user.ShowWindow(hwnd, 9)  # SW_RESTORE
        time.sleep(0.6)
        scale = user.GetDpiForWindow(hwnd) / 96.0
        user.MoveWindow(hwnd, 10, 10, round(width * scale), round(height * scale), True)
        set_topmost(hwnd, True)
        user.SetForegroundWindow(hwnd)
        # 等首页数据与封面加载（真实网络片源约 3-8s）
        deadline = time.monotonic() + 25
        log = profile / "diagnostic.log"
        while time.monotonic() < deadline:
            if log.exists() and ("Vod home ready" in log.read_text(encoding="utf-8", errors="replace")
                                or "VodPage initialized" in log.read_text(encoding="utf-8", errors="replace")):
                break
            time.sleep(0.4)
        time.sleep(6)
        if tag:
            name = {"vod": "点播", "live": "直播", "cast": "投屏", "library": "媒体库",
                    "settings": "设置"}[tag]
            if tag == "settings":
                cdr.ui(hwnd, "设置", "invoke")
            else:
                cdr.ui(hwnd, name, "select")
            time.sleep(1.2)
        for name, action in actions:
            ui(hwnd, name, action)
            time.sleep(1.6)
        time.sleep(float(os.environ.get("SHOT_EXTRA", "0.6")))
        if scroll:
            ui(hwnd, scroll, "scroll-bottom")
            time.sleep(float(os.environ.get("SHOT_SETTLE", "1.2")))
        if scroll_pct:
            ui(hwnd, scroll or "点播海报", "scroll-set", scroll_pct)
            time.sleep(float(os.environ.get("SHOT_SETTLE", "2.0")))
        rect = q.w.RECT()
        user.GetWindowRect(hwnd, ctypes.byref(rect))
        print("debug rect", rect.left, rect.top, rect.right, rect.bottom, "poll", proc.poll(), "visible", user.IsWindowVisible(hwnd))
        img = print_window(hwnd)
        if img is None:
            img = ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True)
        print("debug img", img.size, img.mode)
        out.parent.mkdir(parents=True, exist_ok=True)
        img.save(str(out))
        print(f"saved {out} {img.size} {img.mode}")
        log = profile / "diagnostic.log"
        if log.exists():
            tail = log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-6:]
            print("--- log tail ---")
            print("\n".join(tail))
    finally:
        try:
            if hwnd:
                set_topmost(hwnd, False)
                user.PostMessageW(hwnd, 0x10, 0, 0)
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()


if __name__ == "__main__":
    main()
