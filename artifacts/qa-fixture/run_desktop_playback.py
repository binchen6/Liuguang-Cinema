import functools
from pathlib import Path
import sys
import tempfile
import threading

import desktop_playback_regression
import playback_fixture


if __name__ == "__main__":
    playback_fixture.prepare()
    handler = functools.partial(playback_fixture.Handler, directory=playback_fixture.ASSETS)
    with playback_fixture.Server(("127.0.0.1", 0), handler) as server, tempfile.TemporaryDirectory(
        prefix="desktop-playback-", dir=Path(__file__).parent, ignore_cleanup_errors=True
    ) as root:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            desktop_playback_regression.exercise(Path(sys.argv[1]).resolve(), f"http://127.0.0.1:{server.server_port}", Path(root))
        finally:
            server.shutdown()
