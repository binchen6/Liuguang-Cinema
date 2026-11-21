from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import re
from urllib.parse import urljoin
from urllib.request import urlopen


ROOT = Path(__file__).parent / "apple-color"
MASTER = "https://devstreaming-cdn.apple.com/videos/streaming/examples/adv_dv_atmos/main.m3u8"
VARIANTS = {"sdr": "hls250", "pq": "hdrhls750_hdr10plus", "dolby": "hdrhls750_dolbyvision"}


def read(url, limit):
    with urlopen(url, timeout=10) as response:
        data = response.read(limit + 1)
        if len(data) > limit:
            raise ValueError("Unexpectedly large developer sample")
        return data


def prepare_sample(name, marker, main):
    tag, path = next((line, main[index + 1]) for index, line in enumerate(main[:-1])
                     if line.startswith("#EXT-X-STREAM-INF:") and "RESOLUTION=1280x720" in line
                     and "mp4a." in line and main[index + 1].split("/")[-2].endswith("_" + marker))
    url = urljoin(MASTER, path)
    folder = ROOT / name
    folder.mkdir(exist_ok=True)
    lines = read(url, 256_000).decode().splitlines()
    output = []
    media = []
    for line in lines:
        if line.startswith("#EXT-X-MAP:"):
            media.append(re.search('URI="([^"]+)"', line)[1])
        elif not line.startswith("#"):
            media.append(line)
        output.append(line)
        if len(media) == 4:
            break
    concat = bytearray()
    for piece in media:
        data = read(urljoin(url, piece), 5_000_000)
        (folder / Path(piece).name).write_bytes(data)
        concat.extend(data)
    (folder / "quick.mp4").write_bytes(concat)
    (folder / "test.m3u8").write_text("\n".join(output + ["#EXT-X-ENDLIST", ""]), encoding="utf-8")
    tag = re.sub(r',(?:AUDIO|SUBTITLES|CLOSED-CAPTIONS|STABLE-VARIANT-ID|HDCP-LEVEL)=(?:"[^"]+"|[^,]+)', "", tag)
    return tag + f"\n{name}/test.m3u8\n"


if __name__ == "__main__":
    ROOT.mkdir(exist_ok=True)
    text = read(MASTER, 256_000).decode()
    (ROOT / "upstream.m3u8").write_text(text, encoding="utf-8")
    lines = text.splitlines()
    with ThreadPoolExecutor(max_workers=3) as pool:
        variants = list(pool.map(lambda pair: prepare_sample(*pair, lines), VARIANTS.items()))
    (ROOT / "test-master.m3u8").write_text("#EXTM3U\n#EXT-X-VERSION:7\n" + "".join(variants), encoding="utf-8")
    print("Prepared bounded 720p Apple developer samples for SDR, PQ and Dolby Vision.")
