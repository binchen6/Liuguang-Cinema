import functools
from ctypes import c_void_p
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, ProxyHandler, build_opener
from xml.etree import ElementTree as XML

import window_regression as q
from close_dialog_regression import ui

SOAP = "http://schemas.xmlsoap.org/soap/envelope/"
DEVICE = "urn:schemas-upnp-org:device:MediaRenderer:1"
ADDRESS = "239.255.255.250"
root = Path(__file__).parent
http = build_opener(ProxyHandler({}))

class VideoHandler(SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def copyfile(self, source, outputfile):
        try:
            super().copyfile(source, outputfile)
        except (ConnectionResetError, BrokenPipeError):
            pass


def send(url, method="GET", body=None, headers=None):
    with http.open(Request(url, data=body, method=method, headers=headers or {}), timeout=4) as response:
        return response.status, response.read()


def av(origin, name, **values):
    service = "urn:schemas-upnp-org:service:AVTransport:1"
    envelope = XML.Element(XML.QName(SOAP, "Envelope"))
    body = XML.SubElement(envelope, XML.QName(SOAP, "Body"))
    action = XML.SubElement(body, XML.QName(service, name))
    XML.SubElement(action, "InstanceID").text = "0"
    for key, value in values.items():
        XML.SubElement(action, key).text = value
    code, response = send(origin + "/upnp/AVTransport/control", "POST", XML.tostring(envelope),
                          {"SOAPAction": f'"{service}#{name}"', "Content-Type": "text/xml"})
    assert code == 200
    return {element.tag.split("}")[-1]: element.text for element in XML.fromstring(response).iter()}


def discover(address, origin):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP) as probe:
        probe.bind((address, 0))
        probe.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(address))
        probe.settimeout(0.6)
        datagram = f'M-SEARCH * HTTP/1.1\r\nHOST: {ADDRESS}:1900\r\nMAN: "ssdp:discover"\r\nMX: 1\r\nST: {DEVICE}\r\n\r\n'
        for _ in range(3):
            probe.sendto(datagram.encode("ascii"), (ADDRESS, 1900))
            until = time.monotonic() + 0.8
            while time.monotonic() < until:
                try:
                    answer = probe.recv(4096).decode("ascii")
                except socket.timeout:
                    break
                if f"LOCATION: {origin}/upnp/device.xml" in answer and f"ST: {DEVICE}" in answer:
                    return True
        return False


def status(origin, value):
    return av(origin, "GetTransportInfo").get("CurrentTransportState") == value


def exercise(app, fixture, local, artifact=None):
    q.user.SetProcessDpiAwarenessContext(c_void_p(-4))
    profile = q.make_profile(local, fixture, "receiver", "available", False)
    proc = q.launch(app, profile)
    try:
        hwnd = q.wait_for(lambda: q.find_window(proc.pid), "receiver window")
        q.log_after(profile, "Vod home ready: available")
        q.user.MoveWindow(hwnd, 40, 45, 1120, 720, True)
        ui(hwnd, "投屏", "select")
        ui(hwnd, "接收手机视频")
        if artifact is not None:
            from ctypes import byref, wintypes
            from PIL import ImageGrab
            bounds = wintypes.RECT()
            q.user.GetWindowRect(hwnd, byref(bounds))
            time.sleep(.45)
            ImageGrab.grab(bbox=(bounds.left, bounds.top, bounds.right, bounds.bottom)).save(artifact)
        ui(hwnd, "开始接收", "invoke")
        q.log_after(profile, "Cast receiver ready: DLNA and browser")
        trigger = json.loads(ui(hwnd, "手机扫码", "bounds"))
        if not trigger["visible"] or trigger["w"] <= 0:
            # 1.2 起投屏内容舞台本身不再滚动，左栏是独立 ScrollViewer「投屏侧栏」。
            ui(hwnd, "投屏侧栏", "scroll-bottom")
            trigger = json.loads(ui(hwnd, "手机扫码", "bounds"))
        assert trigger["visible"] and trigger["w"] > 0, trigger
        ui(hwnd, "手机扫码", "invoke")
        q.log_after(profile, "Cast sender panel shown")
        url = ui(hwnd, "投屏链接", "read")
        sender = urlsplit(url)
        origin = f"{sender.scheme}://{sender.netloc}"
        popup = json.loads(ui(hwnd, "复制链接", "bounds"))
        qr = json.loads(ui(hwnd, "投屏二维码", "bounds"))
        from ctypes import byref
        bounds = q.w.RECT()
        q.user.GetWindowRect(hwnd, byref(bounds))
        assert qr["visible"] and qr["w"] >= 240 and abs(qr["h"] - qr["w"]) <= 2, qr
        assert qr["x"] >= bounds.left and qr["y"] >= bounds.top and qr["x"] + qr["w"] <= bounds.right, (qr, bounds)
        assert popup["visible"] and popup["y"] >= qr["y"] + qr["h"], (qr, popup)
        assert popup["y"] + popup["h"] <= bounds.bottom, (popup, bounds)
        assert "发送视频" in send(url)[1].decode("utf-8")
        assert discover(sender.hostname, origin), "missing SSDP discovery response"
        assert b"MediaRenderer" in send(origin + "/upnp/device.xml")[1]
        assert b"SetAVTransportURI" in send(origin + "/upnp/AVTransport/description.xml")[1]
        ui(hwnd, "复制链接", "invoke")
        q.user.PostMessageW(hwnd, 0x100, 0x1b, 0)
        q.user.PostMessageW(hwnd, 0x101, 0x1b, 0)

        with ThreadingHTTPServer((sender.hostname, 0), functools.partial(VideoHandler, directory=root)) as media:
            threading.Thread(target=media.serve_forever, daemon=True).start()
            source = f"http://{sender.hostname}:{media.server_port}/sample.mp4"
            av(origin, "SetAVTransportURI", CurrentURI=source,
               CurrentURIMetaData="<item><title>手机 DLNA 验收</title></item>")
            av(origin, "Play", Speed="1")
            ui(hwnd, "手机 DLNA 验收")
            assert status(origin, "NO_MEDIA_PRESENT")
            ui(hwnd, "接收", "invoke")
            q.wait_for(lambda: status(origin, "PLAYING"), "approved DLNA is playing", 15)
            av(origin, "Pause")
            q.wait_for(lambda: status(origin, "PAUSED_PLAYBACK"), "phone pause")
            av(origin, "Seek", Unit="REL_TIME", Target="00:00:03")
            q.wait_for(lambda: av(origin, "GetPositionInfo").get("RelTime", "") == "00:00:03", "phone seek")
            av(origin, "Stop")
            q.wait_for(lambda: status(origin, "NO_MEDIA_PRESENT"), "phone stop")
            media.shutdown()

        payload = (root / "sample.mp4").read_bytes()
        upload = origin + sender.path.replace("/sender/", "/file/")
        assert send(upload, "POST", payload, {"X-Video-Name": "BrowserTest.mp4"})[0] == 204
        ui(hwnd, "BrowserTest")
        ui(hwnd, "接收", "invoke")
        q.wait_for(lambda: status(origin, "PLAYING"), "uploaded phone video plays", 15)
        ui(hwnd, "停止接收", "invoke")
        ui(hwnd, "开始接收")
        def closed():
            try:
                send(url)
                return False
            except (URLError, HTTPError, ConnectionError):
                return True
        q.wait_for(closed, "network stopped")
        q.wait_for(lambda: not list((profile / "cache").glob("cast-*")), "temporary media removed")
        offset = len(q.log(profile))
        ui(hwnd, "开始接收", "invoke")
        q.log_after(profile, "Cast receiver ready", offset)
        ui(hwnd, "手机扫码", "invoke")
        q.log_after(profile, "Cast sender panel shown", offset)
        url = ui(hwnd, "投屏链接", "read")
        q.close(proc, hwnd)
        q.wait_for(closed, "exit shuts down a still-running receiver")
        print(json.dumps({"cast_ssdp": True, "cast_native_play_pause_seek_stop": True,
                          "phone_browser_video": True, "desk_approval": True, "qr_fully_visible": True, "cleanup": True}))
    except Exception:
        print(q.log(profile))
        raise
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=8)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="cast-regression-", dir=root, ignore_cleanup_errors=True) as directory:
        with q.FixtureServer(("127.0.0.1", 0), q.Fixture) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                artifact = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
                exercise(Path(sys.argv[1]).resolve(), f"http://127.0.0.1:{server.server_port}", Path(directory), artifact)
            finally:
                server.shutdown()
