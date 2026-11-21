from contextlib import closing
from functools import partial
import json
from pathlib import Path
import re
import sqlite3
import sys
import tempfile
from threading import Thread
import time
from urllib.parse import parse_qs, urlsplit

from apple_sample_fixture import ROOT
from close_dialog_regression import ui
from playback_fixture import Handler, Server
import window_regression as q


TITLE = "画质验收样片"
DECODER_ERROR = "此线路仅提供杜比视界 Profile 5，请切换线路。"


class ColorHandler(Handler):
    def respond(self, include_body):
        address = urlsplit(self.path)
        self.server.requests.append((self.command, address.path))
        if address.path == "/fixed.mp4":
            data = (ROOT / "fixed.mp4").read_bytes()
            if request_range := re.fullmatch(r"bytes=(\d+)-(\d*)", self.headers.get("Range", "")):
                start = int(request_range[1])
                end = min(int(request_range[2] or len(data) - 1), len(data) - 1)
                if start > end:
                    return self.send_payload(416, "video/mp4", b"", include_body, **{"Content-Range": f"bytes */{len(data)}"})
                return self.send_payload(206, "video/mp4", data[start:end + 1], include_body,
                                         **{"Content-Range": f"bytes {start}-{end}/{len(data)}"})
            return self.send_payload(200, "video/mp4", data, include_body, **{"Accept-Ranges": "bytes"})
        if address.path == "/dolby-only.m3u8":
            master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=4577671,VIDEO-RANGE=PQ,CODECS=\"dvh1.05.01\",RESOLUTION=1280x720\ndolby/test.m3u8\n"
            return self.send_payload(200, "application/vnd.apple.mpegurl", master.encode(), include_body)
        if address.path != "/api":
            return super().respond(include_body)
        args = parse_qs(address.query)
        if "play" in args:
            path = {"fixed": "fixed.mp4", "dolby": "dolby-only.m3u8"}.get(args["play"][0], "test-master.m3u8")
            payload = {"url": f"http://127.0.0.1:{self.server.server_port}/{path}", "parse": 0}
        else:
            payload = {"class": [{"type_id": "1", "type_name": "自测"}], "list": [
                {"vod_id": "colors", "vod_name": TITLE, "vod_play_from": "本地验收",
                 "vod_play_url": "多变体$variants#单文件$fixed#杜比单线路$dolby"}]}
        self.send_payload(200, "application/json", json.dumps(payload, ensure_ascii=False).encode(), include_body)


def progress(profile, episode=None):
    with closing(sqlite3.connect(profile / "tv.db")) as db:
        item = db.execute("SELECT MAX(position) FROM history WHERE title = ? AND subtitle LIKE ?",
                          (TITLE, f"%{episode}" if episode else "%")).fetchone()
        return item[0] or 0


def qualities(hwnd):
    q.wake_player_controls(hwnd)
    ui(hwnd, "清晰度", "expand")
    return {label: ui(hwnd, label, "menu-enabled") == "True"
            for label in ("自动", "原画（720p）")}


def exercise(app, directory, server):
    base = f"http://127.0.0.1:{server.server_port}"
    profile = q.make_profile(directory, base, "desktop-color", "colors", False)
    config = {"sites": [{"key": "colors", "type": 4, "name": "画质验收", "api": base + "/api"}]}
    (profile / "config" / "last-good.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    with closing(sqlite3.connect(profile / "tv.db")) as db, db:
        db.execute("INSERT INTO settings VALUES ('ui.animations','false')")
        db.execute("INSERT INTO settings VALUES ('playback.autoNext','false')")
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "color test main window")
        q.log_after(profile, "Vod home ready")
        ui(hwnd, TITLE, "invoke")
        ui(hwnd, "多变体", "invoke")
        selection = q.wait_for(lambda: re.search(r"HLS selected: 自动, (Pq|Sdr), 1 variants, HDR output=(True|False)", q.log(profile)),
                               "desktop selects the HLS range that can be displayed")
        hdr = selection[2] == "True"
        range_name, path_prefix = ("Pq", "pq") if hdr else ("Sdr", "sdr")
        assert selection[1] == range_name
        q.wait_for(lambda: progress(profile) > 2000, "decoded sample advances", 20)
        assert ("GET", f"/{path_prefix}/test.m3u8") in server.requests, server.requests
        assert all(not path.startswith("/dolby/") for _, path in server.requests), server.requests
        native_output = [line.split("LibVLC output ", 1)[1] for line in q.log(profile).splitlines() if "LibVLC output " in line]
        if hdr:
            assert any("Rec.2020 gamma:2084" in line for line in native_output), native_output
        expected = {"自动": True, "原画（720p）": True}
        assert qualities(hwnd) == expected
        offset = len(q.log(profile))
        ui(hwnd, "原画（720p）", "toggle")
        q.log_after(profile, f"HLS selected: 原画（720p）, {range_name}, 1 variants, HDR output={hdr}", offset)
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "杜比单线路", "invoke")
        q.log_after(profile, DECODER_ERROR)
        ui(hwnd, "播放失败：" + DECODER_ERROR)
        assert all(not path.startswith("/dolby/") for _, path in server.requests), server.requests
        ui(hwnd, "返回", "invoke")
        ui(hwnd, "单文件", "invoke")
        q.wait_for(lambda: ("GET", "/fixed.mp4") in server.requests, "single file requested")
        q.wait_for(lambda: progress(profile, "单文件") > 0, "resumed direct video is actually playing", 18)
        fixed_qualities = qualities(hwnd)
        assert fixed_qualities == expected, fixed_qualities
        mark = server.requests.count(("GET", "/fixed.mp4"))
        ui(hwnd, "原画（720p）", "toggle")
        assert server.requests.count(("GET", "/fixed.mp4")) == mark
        assert q.close(proc, hwnd) < 4000
        print(json.dumps({"hdr_output": hdr, "hls_range": range_name, "native_output": native_output,
                          "forced_quality": "原画（720p）", "dolby_profile5_without_base_reports_error": True,
                          "single_file_qualities": expected, "single_file_quality_caused_new_get": False,
                          "history_position_ms": progress(profile)}, ensure_ascii=False))
    except Exception:
        print(q.log(profile))
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=12)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="desktop-color-", dir=Path(__file__).parent) as profile:
        with Server(("127.0.0.1", 0), partial(ColorHandler, directory=ROOT)) as server:
            server.requests = []
            Thread(target=server.serve_forever, daemon=True).start()
            try:
                exercise(Path(sys.argv[1]).resolve(), Path(profile), server)
            finally:
                server.shutdown()
