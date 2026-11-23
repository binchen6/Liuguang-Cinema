import hashlib
import json
import os
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from pathlib import Path

import window_regression as q
from close_dialog_regression import ui


class Fixture(q.Fixture):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        if self.path == "/latest":
            data = json.dumps(self.server.release, ensure_ascii=False).encode()
        elif self.path == "/update.exe":
            data = self.server.patch
        else:
            data = json.dumps({"class": [{"type_id": "1", "type_name": "电影"}],
                               "list": [{"vod_id": "film", "vod_name": "更新安装验收"}]}, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Content-Type", "application/octet-stream" if self.path == "/update.exe" else "application/json")
        self.end_headers()
        self.wfile.write(data)


def process_id(app):
    escaped = str(app).replace("'", "''")
    result = subprocess.run(["pwsh", "-NoProfile", "-Command",
        f"Get-Process -Name TV -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{escaped}' }} | Select-Object -First 1 -ExpandProperty Id; exit 0"],
        capture_output=True, text=True)
    output = result.stdout.strip()
    return int(output) if output else None


def main():
    app = Path(sys.argv[1]).resolve()
    patch = Path(sys.argv[2]).resolve()
    profile = Path(sys.argv[3]).resolve()
    with q.FixtureServer(("127.0.0.1", 0), Fixture) as server:
        server.patch = patch.read_bytes()
        base = f"http://127.0.0.1:{server.server_port}"
        digest = hashlib.sha256(server.patch).hexdigest()
        server.release = {
            "tag_name": "v1.8.2", "name": "自动安装链路验收", "body": "- 校验并安装测试补丁",
            "html_url": base + "/release", "published_at": "2026-11-23T00:00:00Z", "draft": False, "prerelease": False,
            "assets": [{"name": "TV-Update-1.8.2-from-1.8.1-x64.exe", "size": len(server.patch),
                        "browser_download_url": base + "/update.exe", "digest": "sha256:" + digest}]
        }
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        profile.mkdir(parents=True, exist_ok=True)
        with closing(sqlite3.connect(profile / "tv.db")) as db, db:
            db.execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
            db.execute("INSERT OR REPLACE INTO settings VALUES ('ui.closeBehavior','2')")
        previous = os.environ.get("TV_UPDATE_API_URL")
        os.environ["TV_UPDATE_API_URL"] = base + "/latest"
        process = None
        try:
            process = q.launch(app, profile)
            hwnd = q.wait_for(lambda: q.find_window(process.pid), "update installation window")
            ui(hwnd, "发现新版本")
            ui(hwnd, "GitHub 正式版")
            ui(hwnd, "下载安装", "invoke")
            process.wait(timeout=90)
            assert process.returncode == 0, process.returncode
            pid = q.wait_for(lambda: process_id(app), "updated application restarted", 45)
            new_hwnd = q.wait_for(lambda: q.find_window(pid), "updated application window", 30)
            ui(new_hwnd, "流光影院")
            assert (profile / "update-install.log").exists(), "installer log missing"
            print("PASS dialog -> incremental download -> SHA-256 -> clean exit -> install -> restart", flush=True)
            q.user.PostMessageW(new_hwnd, 0x10, 0, 0)
            q.wait_for(lambda: process_id(app) is None, "updated application closed", 20)
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
            pid = process_id(app)
            if pid:
                hwnd = q.find_window(pid)
                if hwnd:
                    q.user.PostMessageW(hwnd, 0x10, 0, 0)
            if previous is None:
                os.environ.pop("TV_UPDATE_API_URL", None)
            else:
                os.environ["TV_UPDATE_API_URL"] = previous
            server.shutdown()
            thread.join(timeout=5)


if __name__ == "__main__":
    import threading
    main()
