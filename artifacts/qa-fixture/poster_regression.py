import io
import json
import sys
import tempfile
import threading
from pathlib import Path

from PIL import Image

import window_regression as q
from close_dialog_regression import ui


FORMATS = (("jpg", "JPEG", (218, 72, 56)), ("png", "PNG", (44, 164, 104)), ("webp", "WEBP", (62, 112, 218)))


class PosterFixture(q.Fixture):
    protocol_version = "HTTP/1.1"
    offline = False
    image_requests = []
    images = {}

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path.startswith("/poster/"):
            type(self).image_requests.append(path)
            if type(self).offline:
                self.send_error(503)
                return
            extension = path.rsplit("/", 1)[-1]
            data = type(self).images[extension]
            self.send_response(200)
            self.send_header("Content-Type", "image/" + ("jpeg" if extension == "jpg" else extension))
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        films = [{
            "vod_id": "poster-" + extension,
            "vod_name": "封面验收-" + extension,
            "vod_pic": self.server.base + "/poster/" + extension,
            "vod_play_from": "本地验收",
            "vod_play_url": "正片$https://example.invalid/sample.mp4"
        } for extension, _, _ in FORMATS]
        raw = json.dumps({"class": [{"type_id": "1", "type_name": "电影"}], "list": films,
                          "page": 1, "pagecount": 1}, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def covers_loaded(hwnd, profile, phase):
    try:
        for extension, _, _ in FORMATS:
            ui(hwnd, "封面验收-" + extension)
        if phase == "network" and len(set(PosterFixture.image_requests)) != 3:
            return False
        if len(list((profile / "cache/images").glob("*.img"))) != 3:
            return False
        log = q.log(profile)
        return all(error not in log for error in ("图片加载失败", "图片解码失败", "封面绑定失败"))
    except (AssertionError, RuntimeError):
        return False


def main():
    app = Path(sys.argv[1]).resolve()
    for extension, image_format, color in FORMATS:
        image = Image.new("RGB", (160, 240), color)
        stream = io.BytesIO()
        image.save(stream, image_format)
        PosterFixture.images[extension] = stream.getvalue()
    with tempfile.TemporaryDirectory(prefix="poster-regression-", dir=Path(__file__).parent) as temporary:
        with q.FixtureServer(("127.0.0.1", 0), PosterFixture) as server:
            server.base = f"http://127.0.0.1:{server.server_port}"
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            profile = q.make_profile(Path(temporary), server.base, "posters", "posters", False)
            config = {"sites": [{"key": "posters", "type": 4, "name": "封面验收", "api": server.base + "/home"}]}
            (profile / "config/last-good.json").write_text(json.dumps(config), encoding="utf-8")
            processes = []
            try:
                for phase in ("network", "offline-disk"):
                    PosterFixture.offline = phase == "offline-disk"
                    process = q.launch(app, profile)
                    processes.append(process)
                    hwnd = q.wait_for(lambda: q.find_window(process.pid), "poster window")
                    try:
                        q.wait_for(lambda: covers_loaded(hwnd, profile, phase), phase + " posters loaded", 25)
                    except Exception:
                        print(q.log(profile), flush=True)
                        print("image requests", PosterFixture.image_requests, flush=True)
                        raise
                    q.close(process, hwnd)
                    print("PASS", phase, "PNG/JPEG/WebP cards, cache and error-free decode", flush=True)
                assert len(set(PosterFixture.image_requests)) == 3, PosterFixture.image_requests
                assert not (profile / "last-error.log").exists()
                print(json.dumps({"network_decode": True, "offline_disk_decode": True,
                                  "image_downloads": len(PosterFixture.image_requests), "global_input": False}))
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=10)
                server.shutdown()
                thread.join(timeout=5)


if __name__ == "__main__":
    q.user.SetProcessDpiAwarenessContext(__import__("ctypes").c_void_p(-4))
    main()
