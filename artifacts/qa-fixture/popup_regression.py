import functools
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time

import playback_fixture
import window_regression as q
from close_dialog_regression import ui
from shot import print_window


def capture(hwnd, name):
    directory = os.environ.get("POPUP_CAPTURE_DIR")
    if not directory:
        return
    image = print_window(hwnd)
    if image is not None:
        target = Path(directory)
        target.mkdir(parents=True, exist_ok=True)
        image.save(target / f"{name}.png")


def rect(hwnd, name, kind="bounds"):
    for _ in range(5):
        value = json.loads(ui(hwnd, name, kind))
        if value["visible"] and value["w"] > 0 and value["h"] > 0:
            return value
        time.sleep(.15)
    raise AssertionError((name, value))


def verify(hwnd, trigger, popup, open_with="invoke", item=False):
    anchor = rect(hwnd, trigger)
    ui(hwnd, trigger, open_with)
    content = rect(hwnd, popup, "menu-flyout-bounds" if item else "flyout-bounds")
    gap_x = max(anchor["x"] - content["x"] - content["w"], content["x"] - anchor["x"] - anchor["w"], 0)
    gap_y = max(anchor["y"] - content["y"] - content["h"], content["y"] - anchor["y"] - anchor["h"], 0)
    assert gap_x <= 70 and gap_y <= 82, (trigger, popup, anchor, content)
    print(json.dumps({"trigger": trigger, "panel": popup, "gap_px": [gap_x, gap_y]}, ensure_ascii=False), flush=True)
    return anchor


def escape(hwnd):
    q.user.SetForegroundWindow(hwnd)
    q.user.keybd_event(0x1b, 0, 0, 0)
    q.user.keybd_event(0x1b, 0, 2, 0)
    time.sleep(.24)


def settings_reveal(hwnd, name):
    for percent in (None, *range(0, 101, 15), 100):
        if percent is not None:
            ui(hwnd, "设置内容", "scroll-set", str(percent))
            time.sleep(.2)
        value = json.loads(ui(hwnd, name, "bounds"))
        if value["visible"] and value["w"] > 0 and value["h"] > 0:
            return
    raise AssertionError((name, value))


def exercise(app, base, root):
    profile = q.make_profile(root, base, "popup", "playback", False)
    config = {"sites": [{"key": "playback", "type": 4, "name": "播放器验收", "api": base + "/api"}]}
    (profile / "config" / "last-good.json").write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    with q.closing(q.sqlite3.connect(profile / "tv.db")) as db, db:
        imports = [{"Url": base + "/fixture", "Name": "直播验收", "Document": {"lives": [
            {"name": "验收直播仓库", "url": base + "/extra.m3u"}]}}]
        db.execute("INSERT INTO settings VALUES (?, ?)", ("source.imports", json.dumps(imports, ensure_ascii=False)))
    run = q.launch(app, profile)
    covered = []
    try:
        hwnd = q.wait_for(lambda: q.find_window(run.pid), "popup window")
        q.log_after(profile, "Vod home ready")
        verify(hwnd, "站点", "播放器验收", "expand", item=True)
        escape(hwnd)
        covered.append("站点")
        for trigger, content in (("筛选", "类型"),):
            verify(hwnd, trigger, content)
            verify(hwnd, "排序", "默认", "expand", item=True)
            escape(hwnd)
            escape(hwnd)
            covered.append(trigger)

        ui(hwnd, "设置", "invoke")
        verify(hwnd, "导入仓库", "仓库链接")
        escape(hwnd)
        covered.append("导入仓库")
        ui(hwnd, "网盘", "expand")
        capture(hwnd, "settings-cloud")
        for trigger in ("阿里云盘扫码", "夸克网盘扫码"):
            settings_reveal(hwnd, trigger)
            verify(hwnd, trigger, "关闭扫码")
            ui(hwnd, "关闭扫码", "invoke")
            time.sleep(.25)
            covered.append(trigger)
        ui(hwnd, "网络", "expand")
        settings_reveal(hwnd, "安全 DNS")
        verify(hwnd, "安全 DNS", "系统默认", "expand", item=True)
        escape(hwnd)
        covered.append("安全 DNS")
        ui(hwnd, "外观与内容", "expand")
        for trigger, content in (("主题", "浅色"), ("关闭窗口时", "退出应用"), ("推荐来源", "站点推荐")):
            settings_reveal(hwnd, trigger)
            verify(hwnd, trigger, content, "expand", item=True)
            escape(hwnd)
            covered.append(trigger)
        ui(hwnd, "存储", "expand")
        ui(hwnd, "设置内容", "scroll-bottom")
        verify(hwnd, "清理", "全部缓存", "expand", item=True)
        escape(hwnd)
        covered.append("存储清理")
        ui(hwnd, "直播", "select")
        verify(hwnd, "打开网络串流", "网络串流")
        escape(hwnd)
        covered.append("网络串流")
        verify(hwnd, "验收直播仓库", "验收直播仓库", "expand", item=True)
        escape(hwnd)
        covered.append("直播仓库")
        ui(hwnd, "点播", "select")
        ui(hwnd, "播放验收样片", "invoke")
        ui(hwnd, "来源与线路")
        capture(hwnd, "detail-before-line")
        verify(hwnd, "线路", "本地验收", "expand", item=True)
        escape(hwnd)
        covered.append("详情线路")
        capture(hwnd, "detail-actions")
        verify(hwnd, "下载", "下载集数")
        verify(hwnd, "下载集数", "正片", open_with="find")
        ui(hwnd, "正片", "select")
        ui(hwnd, "下载选中")
        escape(hwnd)
        covered.append("下载及集数")
        ui(hwnd, "正片", "invoke")
        q.log_after(profile, "嗅探已找到媒体")
        verify(hwnd, "选集", "集数列表")
        verify(hwnd, "线路", "本地验收", "expand", item=True)
        escape(hwnd)
        escape(hwnd)
        covered.append("播放器选集及线路")
        # 播放器控制层在 5 秒无操作后统一隐藏，验证前先唤醒控制层。
        q.wake_player_controls(hwnd)
        capture(hwnd, "player-controls")
        verify(hwnd, "音量", "静音")
        escape(hwnd)
        covered.append("音量")
        verify(hwnd, "清晰度", "自动", "expand", item=True)
        escape(hwnd)
        covered.append("清晰度")
        verify(hwnd, "1.0x", "1.25x", "expand", item=True)
        escape(hwnd)
        covered.append("倍速")
        gaps = {}
        for label, size in (("default", None), ("840x615", (840, 615))):
            if size:
                q.user.MoveWindow(hwnd, 38, 52, *size, True)
            verify(hwnd, "播放选项", "弹幕", item=True)
            item = rect(hwnd, "弹幕", "menu-bounds")
            ui(hwnd, "弹幕", "invoke")
            form = rect(hwnd, "显示弹幕", "flyout-bounds")
            gap_x = max(item["x"] - form["x"] - form["w"], form["x"] - item["x"] - item["w"], 0)
            gap_y = max(item["y"] - form["y"] - form["h"], form["y"] - item["y"] - item["h"], 0)
            gaps[label] = [gap_x, gap_y]
            assert gap_x <= 36 and gap_y <= 36, (item, form)
            escape(hwnd)
        covered.append("播放选项及弹幕")
        q.close(run, hwnd)
        print(json.dumps({"checked": covered, "danmaku_anchor_gap_px": gaps}, ensure_ascii=False))
    finally:
        if run.poll() is None:
            run.terminate()
            run.wait(timeout=8)


if __name__ == "__main__":
    playback_fixture.prepare()
    handler = functools.partial(playback_fixture.Handler, directory=playback_fixture.ASSETS)
    with playback_fixture.Server(("127.0.0.1", 0), handler) as server, tempfile.TemporaryDirectory(
        prefix="popup-regression-", dir=Path(__file__).parent, ignore_cleanup_errors=True
    ) as root:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            exercise(Path(sys.argv[1]).resolve(), f"http://127.0.0.1:{server.server_port}", Path(root))
        finally:
            server.shutdown()
