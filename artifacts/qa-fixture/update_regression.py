import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
from contextlib import closing
from pathlib import Path

import window_regression as q
from close_dialog_regression import ui


class UpdateFixture(q.Fixture):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        if self.path == "/latest":
            current = self.server.current_version
            update = f"{current[0]}.{current[1]}.{current[2] + 1}"
            payload = {
                "tag_name": "v" + update,
                "name": "流光影院更新验收",
                "body": "## 本次更新\n- **封面显示修复**\n- 增量更新与自动安装",
                "html_url": self.server.base + "release",
                "published_at": "2026-11-23T00:00:00Z",
                "draft": False,
                "prerelease": False,
                "assets": [{"name": f"TV-Setup-{update}-x64.exe", "size": 12,
                            "browser_download_url": self.server.base + "setup.exe",
                            "digest": "sha256:" + "0" * 64}]
            }
        else:
            payload = {"class": [{"type_id": "1", "type_name": "电影"}],
                       "list": [{"vod_id": "film", "vod_name": "更新验收影片"}], "page": 1, "pagecount": 1}
        raw = json.dumps(payload, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def auto_update_enabled(profile):
    with closing(sqlite3.connect(profile / "tv.db")) as db:
        row = db.execute("SELECT value FROM settings WHERE key='updates.autoCheck'").fetchone()
    return row[0] if row else None


def main():
    app = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="update-regression-", dir=Path(__file__).parent) as temporary:
        with q.FixtureServer(("127.0.0.1", 0), UpdateFixture) as server:
            server.base = f"http://127.0.0.1:{server.server_port}/"
            escaped = str(app).replace("'", "''")
            version = subprocess.check_output(["pwsh", "-NoProfile", "-Command",
                f"((Get-Item -LiteralPath '{escaped}').VersionInfo.FileVersion)"], text=True).strip()
            server.current_version = tuple(map(int, version.split(".")[:3]))
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            profile = q.make_profile(Path(temporary), server.base, "update", "updates", False)
            config = {"sites": [{"key": "updates", "type": 4, "name": "更新验收", "api": server.base + "home"}]}
            (profile / "config/last-good.json").write_text(json.dumps(config), encoding="utf-8")
            previous = os.environ.get("TV_UPDATE_API_URL")
            os.environ["TV_UPDATE_API_URL"] = server.base + "latest"
            processes = []
            try:
                process = q.launch(app, profile)
                processes.append(process)
                hwnd = q.wait_for(lambda: q.find_window(process.pid), "update window")
                ui(hwnd, "发现新版本")
                ui(hwnd, "流光影院更新验收")
                ui(hwnd, "更新内容")
                download = json.loads(ui(hwnd, "下载安装", "bounds"))
                dialog = json.loads(ui(hwnd, "应用更新", "bounds"))
                assert dialog["visible"] and dialog["w"] >= 500 and dialog["h"] >= 400, dialog
                assert dialog["x"] <= download["x"] and dialog["y"] <= download["y"], (dialog, download)
                ui(hwnd, "稍后", "invoke")
                ui(hwnd, "设置", "invoke")
                ui(hwnd, "关于", "expand")
                ui(hwnd, "下载安装更新")
                assert auto_update_enabled(profile) is None
                ui(hwnd, "自动检查更新", "toggle")
                q.wait_for(lambda: auto_update_enabled(profile) == "false", "automatic update preference saved")
                ui(hwnd, "检查更新", "invoke")
                ui(hwnd, "发现新版本")
                ui(hwnd, "稍后", "invoke")
                print("PASS automatic dialog, release notes, settings entry, manual check and opt-out", flush=True)
                q.close(process, hwnd)

                offset = len(q.log(profile))
                process = q.launch(app, profile)
                processes.append(process)
                hwnd = q.wait_for(lambda: q.find_window(process.pid), "opt-out restart")
                q.log_after(profile, "Vod home ready", offset)
                time.sleep(1)
                try:
                    ui(hwnd, "发现新版本")
                    raise AssertionError("unexpected update dialog after opt-out")
                except AssertionError as error:
                    if str(error).startswith("unexpected"):
                        raise
                assert process.poll() is None
                q.close(process, hwnd)
                print("PASS disabled automatic check survives restart", flush=True)
            except Exception:
                print(q.log(profile), flush=True)
                error = profile / "last-error.log"
                if error.exists():
                    print(error.read_text(encoding="utf-8"), flush=True)
                raise
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=10)
                if previous is None:
                    os.environ.pop("TV_UPDATE_API_URL", None)
                else:
                    os.environ["TV_UPDATE_API_URL"] = previous
                server.shutdown()
                thread.join(timeout=5)


if __name__ == "__main__":
    q.user.SetProcessDpiAwarenessContext(__import__("ctypes").c_void_p(-4))
    main()
