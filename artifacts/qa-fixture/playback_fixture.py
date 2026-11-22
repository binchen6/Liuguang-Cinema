import functools
import http.server
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import urllib.parse

def _find_ffmpeg() -> Path:
    env = os.environ.get("FFMPEG_PATH")
    if env and Path(env).exists():
        return Path(env)
    which = shutil.which("ffmpeg")
    if which:
        return Path(which)
    try:
        import imageio_ffmpeg
        return Path(imageio_ffmpeg.get_ffmpeg_exe())
    except Exception:
        return Path("ffmpeg")

ROOT = Path(__file__).resolve().parent
ASSETS = ROOT / "rendition-fixture"
FFMPEG = _find_ffmpeg()
PAYLOAD = bytes(range(251)) * 42001
RANGES = []
EVENTS = []
EXTERNAL = ""
FAIL_CHUNK_ONCE = True


def prepare():
    ASSETS.mkdir(exist_ok=True)
    for height in [420, 720, 1080]:
        manifest = ASSETS / f"v{height}.m3u8"
        if not manifest.exists():
            call_ffmpeg("-i", str(ROOT / f"v{height}.m3u8"), "-c:v", "copy", "-an",
                        "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", manifest.name)
    for index, frequency in enumerate([440, 880]):
        manifest = ASSETS / f"a{index}.m3u8"
        if not manifest.exists():
            call_ffmpeg("-f", "lavfi", "-i", f"sine=frequency={frequency}:sample_rate=48000", "-t", "17",
                        "-c:a", "aac", "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", manifest.name)
    (ASSETS / "text.vtt").write_text("WEBVTT\n\n00:00:00.500 --> 00:00:08.000\nFixture text\n", encoding="utf-8")
    (ASSETS / "subtitle.m3u8").write_text("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:17\n#EXTINF:17.0,\ntext.vtt\n#EXT-X-ENDLIST\n", encoding="utf-8")
    local = ASSETS / "tracks.mkv"
    if not local.exists():
        call_ffmpeg("-i", str(ROOT / "v420.m3u8"), "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
                    "-i", "text.vtt", "-map", "0:v", "-map", "0:a", "-map", "1:a", "-map", "2:s", "-t", "17",
                    "-c:v", "copy", "-c:a", "aac", "-c:s", "srt", "-metadata:s:a:0", "title=Fixture Mandarin",
                    "-metadata:s:a:1", "title=Fixture English", "-metadata:s:s:0", "title=Fixture Subtitle", local.name)


def call_ffmpeg(*args):
    subprocess.run([str(FFMPEG), "-hide_banner", "-loglevel", "error", "-y", *args], cwd=ASSETS, check=True)


MASTER = """#EXTM3U
#EXT-X-VERSION:3
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="dual",NAME="Mandarin, main",LANGUAGE="zh",DEFAULT=YES,AUTOSELECT=YES,URI="/a0.m3u8"
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="dual",NAME="English",LANGUAGE="en",DEFAULT=NO,AUTOSELECT=YES,URI="/a1.m3u8"
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub",NAME="Fixture",LANGUAGE="en",DEFAULT=NO,AUTOSELECT=YES,URI="/subtitle.m3u8"
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="other",NAME="Unused",DEFAULT=YES,URI="/a1.m3u8"
#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=746x420,CODECS="avc1.64001f,mp4a.40.2",AUDIO="dual",SUBTITLES="sub"
/v420.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=1600000,RESOLUTION=1280x720,CODECS="avc1.640020,mp4a.40.2",AUDIO="dual",SUBTITLES="sub"
/v720.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="other"
/v1080.m3u8
"""


class Server(http.server.ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        if not isinstance(sys.exception(), (BrokenPipeError, ConnectionResetError)):
            super().handle_error(request, client_address)


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        self.respond(True)

    def do_HEAD(self):
        self.respond(False)

    def respond(self, include_body):
        global FAIL_CHUNK_ONCE
        path = urllib.parse.urlsplit(self.path).path
        if path != "/events": EVENTS.append(path)
        base = f"http://127.0.0.1:{self.server.server_port}"
        if path == "/api":
            args = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
            if "play" in args:
                response = {"url": base + "/player", "parse": 1, "subtitle": [{"src": base + "/text.vtt"}]}
            else:
                response = {"class": [{"type_id": "1", "type_name": "自测"}], "list": [
                    {"vod_id": "media", "vod_name": "播放验收样片", "vod_play_from": "本地验收", "vod_play_url": "正片$media"}]}
            self.send_payload(200, "application/json", json.dumps(response, ensure_ascii=False).encode(), include_body)
        elif path == "/player":
            html = b"""<!doctype html><script>
            fetch('/denied.mp4').catch(()=>{});
            fetch('/orphan.m4s').catch(()=>{});
            setTimeout(()=>{fetch('/manifest').catch(()=>{})}, 1850);
            </script>"""
            self.send_payload(200, "text/html", html, include_body)
        elif path == "/denied.mp4":
            self.send_payload(403, "video/mp4", b"Denied", include_body)
        elif path == "/orphan.m4s":
            self.send_payload(200, "video/mp4", b"Orphan", include_body)
        elif path == "/manifest":
            self.send_payload(200, "application/vnd.apple.mpegurl", MASTER.encode(), include_body)
        elif path == "/subtitle-content":
            self.send_payload(200, "text/vtt", (ASSETS / "text.vtt").read_bytes(), include_body)
        elif path == "/private":
            if self.headers.get("Cookie") != "fixture=local" or "sign=sample" not in self.path:
                return self.send_payload(403, "text/plain", b"", include_body)
            manifest = f'#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:1,\n/access\n#EXTINF:1,\n{EXTERNAL}/external\n#EXT-X-ENDLIST\n'
            self.send_payload(200, "application/vnd.apple.mpegurl", manifest.encode(), include_body)
        elif path == "/access":
            good = self.headers.get("Cookie") == "fixture=local" and "sign=sample" in self.path
            self.send_payload(200 if good else 403, "text/plain", b"same origin", include_body)
        elif path == "/external":
            clean = not any(self.headers.get(key) for key in ("Cookie", "X-Test-Token", "Referer", "Authorization")) and "sign=" not in self.path
            self.send_payload(200 if clean else 400, "text/plain", b"cross origin", include_body)
        elif path == "/redirect":
            self.send_payload(302, "text/plain", b"", include_body, Location=EXTERNAL + "/external")
        elif path == "/expired":
            self.send_payload(403, "text/plain", b"Expired", include_body)
        elif path in ("/big", "/ignore.mp4"):
            header = self.headers.get("Range")
            RANGES.append((path, header))
            request = re.fullmatch(r"bytes=(\d+)-(\d*)", header or "")
            if path == "/ignore.mp4" or not request:
                return self.send_payload(200, "video/mp4", PAYLOAD, include_body)
            start = int(request[1])
            if path == "/big" and "stale=1" in self.path and start >= 2 * 1024**2:
                return self.send_payload(412, "text/plain", b"Expired chunk", include_body)
            stop = min(int(request[2]) if request[2] else len(PAYLOAD) - 1, len(PAYLOAD) - 1)
            if start == 2 * 1024**2 and FAIL_CHUNK_ONCE:
                FAIL_CHUNK_ONCE = False
                return self.send_payload(503, "text/plain", b"", include_body, **{"Retry-After": "0"})
            self.send_payload(206, "video/mp4", PAYLOAD[start:stop + 1], include_body,
                              **{"Content-Range": f"bytes {start}-{stop}/{len(PAYLOAD)}", "ETag": '"fixture"'})
        elif path == "/requests":
            self.send_payload(200, "application/json", json.dumps(RANGES).encode(), include_body)
        elif path == "/events":
            self.send_payload(200, "application/json", json.dumps(EVENTS).encode(), include_body)
        else:
            (super().do_GET if include_body else super().do_HEAD)()

    def send_payload(self, status, mimetype, data, include_body, **headers):
        self.send_response(status)
        self.send_header("Content-Type", mimetype)
        self.send_header("Content-Length", str(len(data)))
        for key, value in headers.items():
            self.send_header(key, value)
        self.end_headers()
        if include_body:
            self.wfile.write(data)

    def log_message(self, *_):
        pass


def serve(port, external_port=0):
    global EXTERNAL
    prepare()
    handler = functools.partial(Handler, directory=ASSETS)
    with Server(("127.0.0.1", external_port), handler) as external, Server(("127.0.0.1", port), handler) as main:
        EXTERNAL = f"http://127.0.0.1:{external.server_port}"
        threading.Thread(target=external.serve_forever, daemon=True).start()
        try:
            print(f"http://127.0.0.1:{main.server_port}", flush=True)
            main.serve_forever()
        finally:
            external.shutdown()


if __name__ == "__main__":
    serve(int(sys.argv[1]))
