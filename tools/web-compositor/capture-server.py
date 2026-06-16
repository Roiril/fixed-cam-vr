#!/usr/bin/env python3
# web compositor / オペレータ卓 用ローカルサーバ。
# - 静的配信（アプリ本体）
# - POST /save?type=image|video  : body のバイナリを captures/ に保存（= この PC 内）
# - GET  /captures/list          : 保存済み一覧（JSON、新しい順）
# - GET  /captures/<name>        : 保存物の配信（静的）
# - ショー制御（Unity 遠隔操作、.claude/plans/2026-06-11_web-operator-console.md）:
#   GET  /state?rev=N            : show.json（rev > N まで最大 25s ブロックする long-poll）
#   POST /state                  : show.json の部分更新（cameras/cues/post/control）
#   POST /command                : {type: playCue|stopCue|setCameraOverride|setPost}
#   POST /masks?name=            : マスク PNG 保存 → /masks/<name>.png で配信
#   POST /unity/heartbeat        : Unity が現状報告（アクティブカメラ等）
#   GET  /unity/status           : 直近 heartbeat + 経過秒（UI 表示用）
# - GET /cam?host=&port=&path=&auth=user:pass : MJPEG プロキシ（Basic 認証肩代わり。
#   ブラウザは <img> の URL 埋め込み認証をブロックするため iPhone/IP Camera Lite はここを経由する）
#   /cam は <メインポート+1>（既定 8100）でも同時に listen する。MJPEG は接続を張りっぱなしに
#   するため、メインポートと同居させるとブラウザの同一オリジン同時接続上限（6 本）を
#   食い潰して /state long-poll 等が詰まる → ストリームは別ポートに隔離するのが正
#   （JS 側は location.port+1 を自動算出。LAN 内利用なので追加ポート開放のみ注意）
# キャプチャ/録画は全てブラウザ側で行い、ここはその受け皿。スマホ側には何も書かない。

import base64
import datetime
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.request
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ROOT = os.path.dirname(os.path.abspath(__file__))
CAPTURES = os.path.join(ROOT, 'captures')
MASKS = os.path.join(ROOT, 'masks')
os.makedirs(CAPTURES, exist_ok=True)
os.makedirs(MASKS, exist_ok=True)

# ---- ショー状態（show.json = 状態の正）----------------------------------
SHOW_FILE = os.path.join(ROOT, 'show.json')
LONGPOLL_MAX_SEC = 25.0
_show_cond = threading.Condition()


def _default_show():
    return {
        'rev': 0,
        # host/port/auth は Unity 実機が参照する接続先（空 host は焼き込み .asset へフォールバック）。
        # post（カメラ別画像加工）は任意キー。未設定なら Unity は global post に従う。
        'cameras': [
            {'id': 'A', 'sourceId': 'Phone01', 'host': '', 'port': 8080, 'auth': ''},
            {'id': 'B', 'sourceId': 'Phone02', 'host': '', 'port': 8080, 'auth': ''},
            {'id': 'C', 'sourceId': 'Phone03', 'host': '', 'port': 8080, 'auth': ''},
        ],
        'cues': [],
        'post': {'exposure': 0.0, 'contrast': 1.0, 'saturation': 1.0, 'temperature': 0.0,
                 'vignette': 0.25, 'grain': 0.06, 'scanline': 0.0},
        'control': {'activeCue': None, 'cameraOverride': None},
    }


def _load_show():
    try:
        with open(SHOW_FILE, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return _default_show()


_show = _load_show()
# Unity の直近 heartbeat（メモリのみ。再起動で消えてよい）
_unity_status = {'at': 0.0}


def _mutate_show(fn):
    """_show を fn で変更し rev++ → long-poll を起こして永続化。"""
    with _show_cond:
        fn(_show)
        _show['rev'] = int(_show.get('rev', 0)) + 1
        with open(SHOW_FILE, 'w', encoding='utf-8') as f:
            json.dump(_show, f, ensure_ascii=False, indent=2)
        _show_cond.notify_all()
        return _show['rev']

# 動画生成プロンプトのストア（PC 内 prompts.json）。LAN のどの端末からも共有。
PROMPTS_FILE = os.path.join(ROOT, 'prompts.json')
_prompts_lock = threading.Lock()


def _load_prompts():
    try:
        with open(PROMPTS_FILE, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return []


def _save_prompts(items):
    with open(PROMPTS_FILE, 'w', encoding='utf-8') as f:
        json.dump(items, f, ensure_ascii=False, indent=2)


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    # 全レスポンスに付与（保存物の別オリジン利用保険 + 開発中のキャッシュ無効化）
    def end_headers(self):
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json_body(self):
        length = int(self.headers.get('Content-Length', 0))
        data = self.rfile.read(length) if length else b'{}'
        return json.loads(data.decode('utf-8') or '{}')

    def do_GET(self):
        path = urlparse(self.path).path
        if path == '/captures/list':
            return self._json(self._list_captures())
        if path == '/prompts':
            return self._json(_load_prompts())
        if path == '/reveal':
            q = parse_qs(urlparse(self.path).query)
            return self._reveal(q.get('name', [''])[0])
        if path == '/state':
            return self._get_state()
        if path == '/unity/status':
            age = (time.time() - _unity_status['at']) if _unity_status['at'] else None
            return self._json({'status': _unity_status, 'ageSec': age,
                               'alive': age is not None and age < 6.0})
        if path == '/masks/list':
            return self._json(self._list_masks())
        if path == '/cam':
            return self._proxy_cam(parse_qs(urlparse(self.path).query))
        return super().do_GET()

    # MJPEG プロキシ。Basic 認証をサーバ側で肩代わりして同一オリジンで返す。
    # <img src="/cam?host=...&port=8081&auth=admin:admin"> で使う。
    def _proxy_cam(self, q):
        host = (q.get('host', [''])[0]).strip()
        if not re.fullmatch(r'[A-Za-z0-9.\-]{1,253}', host):
            return self._json({'ok': False, 'error': 'bad host'}, 400)
        try:
            port = int(q.get('port', ['8080'])[0])
        except ValueError:
            return self._json({'ok': False, 'error': 'bad port'}, 400)
        path = q.get('path', ['/video'])[0]
        if not path.startswith('/'):
            path = '/' + path
        auth = q.get('auth', [''])[0]

        req = urllib.request.Request(f'http://{host}:{port}{path}')
        if auth:
            token = base64.b64encode(auth.encode('utf-8')).decode('ascii')
            req.add_header('Authorization', f'Basic {token}')
        try:
            upstream = urllib.request.urlopen(req, timeout=5)
        except Exception as e:
            return self._json({'ok': False, 'error': f'upstream: {e}'}, 502)
        try:
            self.send_response(200)
            ctype = upstream.headers.get('Content-Type', 'multipart/x-mixed-replace')
            self.send_header('Content-Type', ctype)
            self.end_headers()
            while True:
                chunk = upstream.read(64 * 1024)
                if not chunk:
                    break
                self.wfile.write(chunk)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass  # クライアント側がタブを閉じた等。正常系
        finally:
            try:
                upstream.close()
            except Exception:
                pass

    # ---- show 状態（long-poll）----
    def _get_state(self):
        q = parse_qs(urlparse(self.path).query)
        try:
            known_rev = int(q.get('rev', ['-1'])[0])
        except ValueError:
            known_rev = -1
        deadline = time.monotonic() + LONGPOLL_MAX_SEC
        with _show_cond:
            # rev が進むまでブロック（known_rev 省略/-1 なら即返す）
            while known_rev >= 0 and int(_show.get('rev', 0)) <= known_rev:
                remain = deadline - time.monotonic()
                if remain <= 0:
                    break
                _show_cond.wait(remain)
            return self._json(_show)

    def _list_masks(self):
        items = []
        for n in os.listdir(MASKS):
            fp = os.path.join(MASKS, n)
            if os.path.isfile(fp):
                items.append({'name': n, 'url': '/masks/' + n, 'mtime': os.stat(fp).st_mtime})
        items.sort(key=lambda x: x['mtime'], reverse=True)
        return items

    # captures/<name> をファイルマネージャで開く（選択状態）。サーバは PC 上で動くので可能。
    def _reveal(self, name):
        if not name or name != os.path.basename(name):
            return self._json({'ok': False, 'error': 'bad name'}, 400)
        target = os.path.join(CAPTURES, name)
        if not os.path.isfile(target):
            return self._json({'ok': False, 'error': 'not found'}, 404)
        try:
            if sys.platform.startswith('win'):
                # explorer は /select で当該ファイルを選択表示。成功時も exit 1 を返すので戻り値は見ない。
                subprocess.Popen(f'explorer /select,"{target}"')
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', '-R', target])
            else:
                subprocess.Popen(['xdg-open', os.path.dirname(target)])
            return self._json({'ok': True, 'path': target})
        except Exception as e:
            return self._json({'ok': False, 'error': str(e)}, 500)

    def do_POST(self):
        parsed = urlparse(self.path)
        if parsed.path == '/state':
            return self._post_state()
        if parsed.path == '/command':
            return self._post_command()
        if parsed.path == '/masks':
            return self._post_mask(parse_qs(parsed.query))
        if parsed.path == '/unity/heartbeat':
            body = self._read_json_body()
            body['at'] = time.time()
            _unity_status.clear()
            _unity_status.update(body)
            return self._json({'ok': True})
        if parsed.path == '/save':
            q = parse_qs(parsed.query)
            typ = q.get('type', ['image'])[0]
            ext = 'webm' if typ == 'video' else 'jpg'
            length = int(self.headers.get('Content-Length', 0))
            data = self.rfile.read(length) if length else b''
            if not data:
                return self._json({'ok': False, 'error': 'empty body'}, 400)
            stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S_%f')[:-3]
            name = f'cap_{stamp}.{ext}'
            with open(os.path.join(CAPTURES, name), 'wb') as f:
                f.write(data)
            return self._json({'ok': True, 'name': name, 'url': '/captures/' + name,
                               'type': typ, 'size': len(data)})

        if parsed.path == '/prompts':
            body = self._read_json_body()
            title = (body.get('title') or '').strip()
            text = (body.get('text') or '').strip()
            kind = body.get('kind') or 'video'
            if kind not in ('image', 'video'):
                kind = 'video'
            if not text:
                return self._json({'ok': False, 'error': 'empty text'}, 400)
            now = datetime.datetime.now().timestamp()
            with _prompts_lock:
                items = _load_prompts()
                pid = body.get('id')
                if pid:  # 更新
                    for it in items:
                        if it.get('id') == pid:
                            it['title'], it['text'], it['kind'], it['mtime'] = title, text, kind, now
                            break
                    else:
                        pid = None
                if not pid:  # 新規
                    pid = 'p_' + datetime.datetime.now().strftime('%Y%m%d_%H%M%S_%f')[:-3]
                    items.append({'id': pid, 'title': title, 'text': text, 'kind': kind, 'mtime': now})
                _save_prompts(items)
            return self._json({'ok': True, 'id': pid, 'items': _load_prompts()})

        if parsed.path == '/prompts/delete':
            body = self._read_json_body()
            pid = body.get('id')
            with _prompts_lock:
                items = [it for it in _load_prompts() if it.get('id') != pid]
                _save_prompts(items)
            return self._json({'ok': True, 'items': items})

        return self._json({'ok': False, 'error': 'unknown endpoint'}, 404)

    # show.json の部分更新。トップレベルの許可キーのみ shallow に置換する。
    _STATE_KEYS = ('cameras', 'cues', 'post', 'control')

    def _post_state(self):
        body = self._read_json_body()
        patch = {k: body[k] for k in self._STATE_KEYS if k in body}
        if not patch:
            return self._json({'ok': False, 'error': 'no valid keys'}, 400)

        def apply(show):
            show.update(patch)
        rev = _mutate_show(apply)
        return self._json({'ok': True, 'rev': rev})

    def _post_command(self):
        body = self._read_json_body()
        typ = body.get('type')

        def apply(show):
            ctrl = show.setdefault('control', {})
            if typ == 'playCue':
                ctrl['activeCue'] = body.get('id')
            elif typ == 'stopCue':
                ctrl['activeCue'] = None
            elif typ == 'setCameraOverride':
                ctrl['cameraOverride'] = body.get('camera')  # None = ゾーン自律へ戻す
            elif typ == 'setPost':
                show.setdefault('post', {}).update(body.get('post') or {})
            else:
                raise ValueError(f'unknown command type: {typ}')
        try:
            rev = _mutate_show(apply)
        except ValueError as e:
            return self._json({'ok': False, 'error': str(e)}, 400)
        return self._json({'ok': True, 'rev': rev})

    def _post_mask(self, q):
        name = (q.get('name', [''])[0]).strip()
        # パストラバーサル拒否 + 拡張子は固定で .png
        if not re.fullmatch(r'[A-Za-z0-9_\-]{1,64}', name):
            return self._json({'ok': False, 'error': 'bad name (A-Za-z0-9_- only)'}, 400)
        length = int(self.headers.get('Content-Length', 0))
        data = self.rfile.read(length) if length else b''
        if not data.startswith(b'\x89PNG'):
            return self._json({'ok': False, 'error': 'not a png'}, 400)
        fname = name + '.png'
        with open(os.path.join(MASKS, fname), 'wb') as f:
            f.write(data)
        return self._json({'ok': True, 'name': fname, 'url': '/masks/' + fname,
                           'size': len(data)})

    def _list_captures(self):
        items = []
        for n in os.listdir(CAPTURES):
            fp = os.path.join(CAPTURES, n)
            if not os.path.isfile(fp):
                continue
            ext = n.rsplit('.', 1)[-1].lower() if '.' in n else ''
            typ = 'video' if ext in ('webm', 'mp4', 'mov') else 'image'
            st = os.stat(fp)
            items.append({'name': n, 'url': '/captures/' + n, 'type': typ,
                          'size': st.st_size, 'mtime': st.st_mtime})
        items.sort(key=lambda x: x['mtime'], reverse=True)
        return items

    def log_message(self, *args):
        pass  # 静かに


if __name__ == '__main__':
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8099
    cam_port = port + 1
    # MJPEG ストリーム専用の追加 listener（接続上限隔離。ハンドラは同一でよい）
    cam_srv = ThreadingHTTPServer(('0.0.0.0', cam_port), Handler)
    threading.Thread(target=cam_srv.serve_forever, daemon=True).start()
    print(f'web compositor capture-server : http://0.0.0.0:{port}/  (captures -> {CAPTURES})')
    print(f'  MJPEG stream proxy (/cam)    : http://0.0.0.0:{cam_port}/cam')
    ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever()
