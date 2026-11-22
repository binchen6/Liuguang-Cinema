import functools, http.server, json, os, pathlib, shutil, sqlite3, subprocess, sys, urllib.parse
root = pathlib.Path(__file__).parent
profile = root / 'profile'
profile.joinpath('config').mkdir(parents=True, exist_ok=True)
base = 'http://127.0.0.1:18789'

def _find_ffmpeg():
    env = os.environ.get('FFMPEG_PATH')
    if env and pathlib.Path(env).exists(): return pathlib.Path(env)
    which = shutil.which('ffmpeg')
    if which: return pathlib.Path(which)
    try:
        import imageio_ffmpeg
        return pathlib.Path(imageio_ffmpeg.get_ffmpeg_exe())
    except Exception:
        return pathlib.Path('ffmpeg')

ffmpeg = _find_ffmpeg()
item = {"vod_id":"local-film","vod_name":"流光验收影片","vod_year":"2026","type_name":"电影","vod_content":"本地播放链路验收样本。","vod_play_from":"验收线路","vod_play_url":"正片$fixture"}
site = {"name":"验收片源","key":"fixture","type":4,"api":base + '/api'}

def dump(name,data):
    (root / name).write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')

def create_files():
    for height,width in [(1080,1920),(720,1280),(420,746)]:
        if (root / f'v{height}.m3u8').exists(): continue
        subprocess.run([str(ffmpeg),'-hide_banner','-loglevel','error','-y','-f','lavfi','-i',f'testsrc2=s={width}x{height}:r=30','-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-t','17','-c:v','libx264','-preset','ultrafast','-pix_fmt','yuv420p','-g','60','-c:a','aac','-f','hls','-hls_time','2','-hls_playlist_type','vod',f'v{height}.m3u8'],cwd=root,check=True)
    if not (root / 'sample.mp4').exists():
        subprocess.run([str(ffmpeg),'-hide_banner','-loglevel','error','-y','-i','v420.m3u8','-c','copy','sample.mp4'],cwd=root,check=True)
    (root / 'master.m3u8').write_text('#EXTM3U\n#EXT-X-VERSION:3\n' + ''.join(f'#EXT-X-STREAM-INF:BANDWIDTH={bw},RESOLUTION={w}x{h}\nv{h}.m3u8\n' for h,w,bw in [(420,746,900000),(720,1280,1600000),(1080,1920,3800000)]),encoding='utf-8')
    (root / 'builtin.txt').write_text('内置,#genre#\n本地频道,' + base + '/master.m3u8\n',encoding='utf-8')
    (root / 'extra.m3u').write_text('#EXTM3U\n#EXTINF:-1 group-title="独立直播",导入频道\n' + base + '/master.m3u8\n',encoding='utf-8')
    (root / 'captions.vtt').write_text('WEBVTT\n\n00:00:00.500 --> 00:00:04.000\nLocal subtitle\n',encoding='utf-8')
    dump('danmaku.json', {'comments':[{'p':'1,1,16777215','m':'本地弹幕验收'},{'p':'5,1,16776960','m':'加群推广'}]})
    dump('profile/config/last-good.json',{'sites':[site],'lives':[{'name':'内置直播','url':base+'/builtin.txt'}]})
    dump('depot.json', {'urls':[{'name':'子仓甲','url':'./leaf.json'},{'name':'子仓乙','url':'./extra.m3u'}]})
    dump('leaf.json', {'sites':[dict(site,key='fixture-extra',name='导入片源')]})
    with sqlite3.connect(profile/'tv.db') as db:
        db.execute('CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
        db.execute("INSERT OR REPLACE INTO settings VALUES('source.autoRefresh','false')")
        db.execute("INSERT OR REPLACE INTO settings VALUES('ui.theme','1')")
        db.execute("INSERT OR REPLACE INTO settings VALUES('search.instant','false')")
        db.execute("INSERT OR REPLACE INTO settings VALUES('playback.autoNext','false')")

class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        uri = urllib.parse.urlsplit(self.path)
        if uri.path == '/api':
            q = urllib.parse.parse_qs(uri.query)
            if 'play' in q:
                result = dict(url=base+'/master.m3u8', subs=[base+'/captions.vtt'], danmaku=base+'/danmaku.json', parse=0)
            else:
                result = {'class':[{'type_id':'1','type_name':'电影'}], 'list':[item], 'page':1,'pagecount':1}
            data = json.dumps(result,ensure_ascii=False).encode()
            self.send_response(200)
            self.send_header('Content-Type','application/json; charset=utf-8')
            self.send_header('Content-Length',str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        return super().do_GET()

create_files()
print(base,flush=True)
http.server.ThreadingHTTPServer(('127.0.0.1',18789), functools.partial(Handler, directory=root)).serve_forever()
