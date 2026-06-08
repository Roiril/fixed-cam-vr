#!/usr/bin/env python3
# web compositor 用ローカルサーバ。
# - 静的配信（アプリ本体）
# - POST /save?type=image|video  : body のバイナリを captures/ に保存（= この PC 内）
# - GET  /captures/list          : 保存済み一覧（JSON、新しい順）
# - GET  /captures/<name>        : 保存物の配信（静的）
# キャプチャ/録画は全てブラウザ側で行い、ここはその受け皿。スマホ側には何も書かない。

import datetime
import json
import os
import subprocess
import sys
import threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ROOT = os.path.dirname(os.path.abspath(__file__))
CAPTURES = os.path.join(ROOT, 'captures')
os.makedirs(CAPTURES, exist_ok=True)

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
        return super().do_GET()

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
    print(f'web compositor capture-server : http://0.0.0.0:{port}/  (captures -> {CAPTURES})')
    ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever()
