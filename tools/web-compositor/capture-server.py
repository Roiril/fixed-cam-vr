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
#   POST /export-build           : 現 show.json + 参照アセットを Assets/StreamingAssets/show/
#                                  へ焼き込み（URL を sa://assets/<file> に書換）。結果を JSON で返す
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
import socket
import subprocess
import sys
import threading
import time
import urllib.request
import uuid as _uuidlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ROOT = os.path.dirname(os.path.abspath(__file__))
CAPTURES = os.path.join(ROOT, 'captures')
MASKS = os.path.join(ROOT, 'masks')
# デモ撮影専用フォルダ（静止画 📷 / 録画 ⏺ の保存先。captures とは分ける）
RECORDINGS = os.path.join(ROOT, 'recordings')
STATIC_INPUTS = os.path.join(ROOT, 'static-inputs')
os.makedirs(CAPTURES, exist_ok=True)
os.makedirs(MASKS, exist_ok=True)
os.makedirs(RECORDINGS, exist_ok=True)

# /save?to= と /open-dir?dir= の保存先ホワイトリスト（パストラバーサル防止）
SAVE_DIRS = {'captures': CAPTURES, 'recordings': RECORDINGS}

# エクスポート時に「ローカル URL → 実ファイル」を解決するディレクトリ対応表。
LOCAL_URL_DIRS = {
    '/masks/': MASKS,
    '/captures/': CAPTURES,
    '/recordings/': RECORDINGS,
    '/static-inputs/': STATIC_INPUTS,
}
# リポジトリルート（tools/web-compositor から 2 つ上）。エクスポート先の解決に使う。
REPO_ROOT = os.path.dirname(os.path.dirname(ROOT))

# ---- ショー状態（show.json = 状態の正）----------------------------------
SHOW_FILE = os.path.join(ROOT, 'show.json')
LONGPOLL_MAX_SEC = 25.0
_show_cond = threading.Condition()


def _default_show():
    return {
        'rev': 0,
        # host/port/auth は Unity 実機が参照する接続先（空 host は焼き込み .asset へフォールバック）。
        # post（カメラ別画像加工）は任意キー。未設定なら Unity は global post に従う。
        # pinned=True は「卓で host を手入力した」印。自動追従（discovery）を抑止する。
        # 既存 show.json に pinned が無くても「未固定＝追従対象」として扱う（後方互換）。
        'cameras': [
            {'id': 'A', 'sourceId': 'Phone01', 'host': '', 'port': 8080, 'auth': '', 'pinned': False},
            {'id': 'B', 'sourceId': 'Phone02', 'host': '', 'port': 8080, 'auth': '', 'pinned': False},
            {'id': 'C', 'sourceId': 'Phone03', 'host': '', 'port': 8080, 'auth': '', 'pinned': False},
        ],
        'cues': [],
        'post': {'exposure': 0.0, 'contrast': 1.0, 'saturation': 1.0, 'temperature': 0.0,
                 'vignette': 0.25, 'grain': 0.06, 'scanline': 0.0},
        # autoFollow=True: discovery で発見したカメラ IP を cameras[i].host へ自動反映する
        # （pinned カメラは除外）。UI トグルで切替。欠落は ON 扱い（後方互換）。
        'control': {'activeCue': None, 'cameraOverride': None, 'autoFollow': True},
        # ゾーン校正レイアウト（course space）。Web フロアマップが編集し Unity が展開する。
        # grid = タイルペイント（12×12・0.15m）。cells は rows 本の文字列、rows[0]=北端
        # （z=+0.9）・col0=西端（x=-0.9）。文字 '0'..'8'=カメラ index、'.'=未割当。
        # cell(r,c) 中心: x=-w/2+(c+0.5)·tileM, z=+d/2-(r+0.5)·tileM。
        # cuts = 後方互換（正準ループ上の切れ目）。grid が正で、grid 無しの端末は cuts から展開する。
        # 下記 grid の初期塗りは既定 cuts（s=0.125→cam1 / 0.375→cam2 / 0.875→cam0）を
        # 各タイル中心へ射影して静的生成した結果（floormap.js の cellsFromCuts と一致）。
        # course.order = 周回の巡回順（カメラ index の配列。order[0]=スタート領域）。
        # フロアマップ UI が grid の塗りから角度順で提案し CW/CCW で反転できる。周回カウント
        # （schedule 発火）はこの順に沿って進む。
        # regPoints = HMD 位置合わせのタッチ基準点（course space・順序=タッチ順・2〜5 点）。
        # 各要素 {x, z, label?}。フロアマップ UI が編集。未設定（下記のように省略）なら Unity は
        # 既定 2 点 (-0.5,0.5)/(0.5,0.5) へフォールバックする。フィールドを足さなくても layout は
        # shallow 置換で丸ごと通るため、UI が保存すれば自動で乗る（focus: 後方互換維持）。
        'layout': {
            'rev': 1,
            'floor': {'w': 1.8, 'd': 1.8},
            'wall': {'corner': [-0.5, 0.5], 'endX': [0.5, 0.5], 'endZ': [-0.5, -0.5]},
            'course': {'order': [0, 1, 2]},
            'grid': {
                'tileM': 0.15, 'cols': 12, 'rows': 12,
                'cells': [
                    '222222222221',
                    '222222222221',
                    '222222222111',
                    '222......111',
                    '222......111',
                    '222......111',
                    '222......111',
                    '222......111',
                    '222......111',
                    '222000000011',
                    '220000000001',
                    '000000000001',
                ],
            },
            'cuts': [
                {'s': 0.125, 'camAfter': 1},
                {'s': 0.375, 'camAfter': 2},
                {'s': 0.875, 'camAfter': 0},
            ],
            'overlapM': 0.08,
            'hysteresisM': 0.12,
        },
        # schedule = 事前オーサリングの正体（何周目 lap のどのゾーン camera で cueId を発火するか）。
        # lap は 1 始まり、camera はカメラ index（ゾーンは cameraIndex でキー）。Web 卓が編集し
        # export-build で APK に焼き込む。ライブ control.activeCue が非空の間は Unity 側で抑止される。
        'schedule': {'rev': 1, 'entries': []},
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


# ---- UDP 発見プロトコル fixedcam-discovery/1 -----------------------------------
# 卓 PC がスマホ配信カメラ / 他 PC 卓を LAN 上で発見し、cameras[i].host を自動追従する。
# 設計は .claude/plans/2026-07-18_connection-robustness.md が正。
#   - :8830 で announce を listen（proto + show トークン一致のみ採用）
#   - 2 秒毎に probe をブロードキャスト（subnet-directed 優先 + 255.255.255.255）
#     → スマホは unicast で announce 応答。probe への unicast 応答も同ソケットで受信
#   - 自機も show-server として announce（5 秒毎ブロードキャスト + probe への unicast 応答）
#   - GET /discovery で発見表を返す。二重 ID（同一 id・異 uuid）は conflict として報告
# キルスイッチ: 環境変数 FIXEDCAM_DISCOVERY=0 で無効化（bind 失敗も本体機能は継続）。
DISCOVERY_ENABLED = os.environ.get('FIXEDCAM_DISCOVERY', '1') != '0'
DISCOVERY_PORT = 8830
DISCOVERY_PROTO = 'fixedcam-discovery/1'
# show トークン: 隣ブース混線対策。不一致パケットは無視。streamer/Unity 側の既定と揃える。
SHOW_TOKEN = os.environ.get('FIXEDCAM_SHOW', 'mawarimi')
SERVER_VERSION = '0.3.0'

_server_uuid = 'srv-' + _uuidlib.uuid4().hex[:12]  # 自機 announce の install uuid（起動毎）
_http_port = 8099                                   # __main__ で実ポートに更新
_disc_lock = threading.Lock()
_disc = {}                                          # uuid -> {role,id,ip,port,uuid,version,name,lastSeen}
_last_follow = None                                 # 直近の自動追従イベント {at, changes:[{id,host,port}]}


class _NoChange(Exception):
    """自動追従で変更が無い時に _mutate_show を空振りさせる番兵。"""


def _local_ip():
    """外向き UDP ソケットで自機の LAN IP を推定（実際には送信しない）。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(('8.8.8.8', 80))
        return s.getsockname()[0]
    except OSError:
        return None
    finally:
        s.close()


def _disc_targets():
    """probe/announce の送信先。subnet-directed（/24 前提）優先 + 全体ブロードキャスト。"""
    outs = ['255.255.255.255']
    ip = _local_ip()
    if ip:
        p = ip.split('.')
        if len(p) == 4:
            outs.insert(0, '.'.join(p[:3]) + '.255')
    return outs


def _server_announce():
    return {'proto': DISCOVERY_PROTO, 'type': 'announce', 'show': SHOW_TOKEN,
            'role': 'show-server', 'id': 'PC', 'uuid': _server_uuid,
            'httpPort': _http_port, 'version': SERVER_VERSION, 'name': socket.gethostname()}


def _disc_send(sock, obj, addr):
    try:
        sock.sendto(json.dumps(obj).encode('utf-8'), addr)
    except OSError:
        pass


def _disc_broadcast(sock, obj):
    data = json.dumps(obj).encode('utf-8')
    for host in _disc_targets():
        try:
            sock.sendto(data, (host, DISCOVERY_PORT))
        except OSError:
            pass


def _disc_note(pkt, addr):
    role = pkt.get('role') or 'camera'
    u = pkt.get('uuid') or f"{role}:{pkt.get('id')}:{addr[0]}"
    with _disc_lock:
        _disc[u] = {
            'role': role,
            'id': str(pkt.get('id') if pkt.get('id') is not None else '?'),
            'ip': addr[0],
            'port': int(pkt.get('httpPort') or 8080),
            'uuid': pkt.get('uuid') or u,
            'version': str(pkt.get('version') or ''),
            'name': str(pkt.get('name') or ''),
            'lastSeen': time.time(),
        }


def _disc_listen(sock):
    while True:
        try:
            data, addr = sock.recvfrom(2048)
        except OSError:
            break
        try:
            pkt = json.loads(data.decode('utf-8'))
        except Exception:
            continue
        if pkt.get('proto') != DISCOVERY_PROTO or pkt.get('show') != SHOW_TOKEN:
            continue  # 別プロトコル / 別ショーのパケットは無視
        typ = pkt.get('type')
        if typ == 'announce':
            if pkt.get('uuid') == _server_uuid:
                continue  # 自機 announce の反射
            _disc_note(pkt, addr)
        elif typ == 'probe':
            # probe を出した相手（Quest / 別 PC）へ自機 show-server を unicast 応答
            _disc_send(sock, _server_announce(), addr)


def _disc_sender(sock):
    seq = 0
    last_announce = 0.0
    while True:
        seq += 1
        _disc_broadcast(sock, {'proto': DISCOVERY_PROTO, 'type': 'probe',
                               'show': SHOW_TOKEN, 'seq': seq})
        now = time.time()
        if now - last_announce >= 5.0:
            _disc_broadcast(sock, _server_announce())
            last_announce = now
        try:
            _auto_follow()
        except Exception:
            pass  # 追従は保険。失敗しても discovery/本体は継続
        time.sleep(2.0)


def _disc_snapshot_cameras(ttl=12.0):
    """発見表から camera を id 毎に集約。best={id->最新entry}, conflicts={二重ID}。"""
    now = time.time()
    by_id = {}
    with _disc_lock:
        for e in _disc.values():
            if e['role'] != 'camera' or e['id'] == '?':
                continue
            if now - e['lastSeen'] > ttl:
                continue
            by_id.setdefault(e['id'], []).append(dict(e))
    best, conflicts = {}, set()
    for cid, lst in by_id.items():
        if len({e['uuid'] for e in lst}) > 1:
            conflicts.add(cid)  # 別機が同 ID → 曖昧なので追従しない
            continue
        best[cid] = max(lst, key=lambda e: e['lastSeen'])
    return best, conflicts


def _auto_follow():
    """発見した camera IP を cameras[i].host へ反映（pinned 除外・変化時のみ rev++）。"""
    if not DISCOVERY_ENABLED:
        return
    best, conflicts = _disc_snapshot_cameras()
    if not best:
        return

    def apply(show):
        ctrl = show.setdefault('control', {})
        if not ctrl.get('autoFollow', True):
            raise _NoChange()
        changes = []
        for cam in show.get('cameras', []):
            if cam.get('pinned'):
                continue
            cid = cam.get('id')
            if cid in conflicts:
                continue
            e = best.get(cid)
            if not e:
                continue
            if cam.get('host') != e['ip'] or int(cam.get('port') or 0) != int(e['port']):
                cam['host'] = e['ip']
                cam['port'] = int(e['port'])
                changes.append({'id': cid, 'host': e['ip'], 'port': int(e['port'])})
        if not changes:
            raise _NoChange()
        apply.changes = changes

    try:
        _mutate_show(apply)
    except _NoChange:
        return
    global _last_follow
    _last_follow = {'at': time.time(), 'changes': apply.changes}


def _probe_info(host, port, auth, timeout=3.0):
    """PC → カメラ /info へ実 HTTP GET。(ok, detail) を返す（疎通診断用）。"""
    req = urllib.request.Request(f'http://{host}:{port}/info')
    if auth:
        token = base64.b64encode(auth.encode('utf-8')).decode('ascii')
        req.add_header('Authorization', f'Basic {token}')
    try:
        r = urllib.request.urlopen(req, timeout=timeout)
        body = r.read(2048)
        try:
            j = json.loads(body.decode('utf-8'))
            nm = j.get('deviceName') or j.get('cameraId') or j.get('name') or ''
            return True, ('/info OK ' + str(nm)).strip()
        except Exception:
            return True, f'/info HTTP {getattr(r, "status", 200)}'
    except Exception as e:
        return False, str(e)[:100]


def _start_discovery(http_port):
    global _http_port
    _http_port = http_port
    if not DISCOVERY_ENABLED:
        print('  discovery                   : 無効（FIXEDCAM_DISCOVERY=0）')
        return
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.bind(('', DISCOVERY_PORT))
    except OSError as e:
        print(f'  discovery                   : bind 失敗（{e}）→ 無効化（本体機能は継続）')
        return
    threading.Thread(target=_disc_listen, args=(sock,), daemon=True).start()
    threading.Thread(target=_disc_sender, args=(sock,), daemon=True).start()
    print(f'  discovery (fixedcam/1)       : udp :{DISCOVERY_PORT}  show="{SHOW_TOKEN}"  uuid={_server_uuid}')


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
        if path == '/open-dir':
            q = parse_qs(urlparse(self.path).query)
            return self._open_dir(q.get('dir', ['recordings'])[0])
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
        if path == '/discovery':
            return self._get_discovery()
        if path == '/diag':
            return self._get_diag()
        return super().do_GET()

    # 発見表（camera / show-server）+ 二重 ID 警告 + 自動追従状態を返す。
    def _get_discovery(self):
        now = time.time()
        devices = []
        with _disc_lock:
            for u in list(_disc):  # 30s 以上音沙汰なしは掃除
                if now - _disc[u]['lastSeen'] > 30.0:
                    del _disc[u]
            for e in _disc.values():
                devices.append({
                    'role': e['role'], 'id': e['id'], 'ip': e['ip'], 'port': e['port'],
                    'uuid': e['uuid'], 'version': e['version'], 'name': e['name'],
                    'ageSec': round(now - e['lastSeen'], 1),
                })
        # 自機（show-server）も一覧に出す（PC 卓が複数居る二重サーバの検知用）。
        devices.append({'role': 'show-server', 'id': 'PC', 'ip': _local_ip() or '127.0.0.1',
                        'port': _http_port, 'uuid': _server_uuid, 'version': SERVER_VERSION,
                        'name': socket.gethostname(), 'ageSec': 0.0, 'self': True})
        # 二重 ID: 直近（<12s）の camera で同 id に異なる uuid が複数。
        by_id = {}
        for d in devices:
            if d['role'] != 'camera' or d['id'] == '?' or d['ageSec'] > 12.0:
                continue
            by_id.setdefault(d['id'], set()).add(d['uuid'])
        conflicts = sorted(cid for cid, us in by_id.items() if len(us) > 1)
        devices.sort(key=lambda d: (d['role'] != 'show-server', d['id'], d['ip']))
        with _show_cond:
            auto_follow = bool(_show.get('control', {}).get('autoFollow', True))
        return self._json({'devices': devices, 'conflicts': conflicts,
                           'autoFollow': auto_follow, 'lastFollow': _last_follow,
                           'enabled': DISCOVERY_ENABLED, 'showToken': SHOW_TOKEN})

    # 疎通診断: (a) PC→各カメラ /info 実接続 (b) ビーコン受信 (c) Quest heartbeat。
    # ビーコンは来るのに /info が ✕ なら AP のクライアント間遮断が濃厚（現地即判定用）。
    def _get_diag(self):
        now = time.time()
        with _show_cond:
            cams = json.loads(json.dumps(_show.get('cameras', [])))
        with _disc_lock:
            disc_by_id = {}
            for e in _disc.values():
                if e['role'] != 'camera' or e['id'] == '?':
                    continue
                cur = disc_by_id.get(e['id'])
                if not cur or e['lastSeen'] > cur['lastSeen']:
                    disc_by_id[e['id']] = dict(e)
        results = []
        for cam in cams:
            cid = cam.get('id')
            host = (cam.get('host') or '').strip()
            port = int(cam.get('port') or 8080)
            auth = cam.get('auth') or ''
            row = {'id': cid, 'host': host, 'port': port, 'pinned': bool(cam.get('pinned'))}
            if host:
                ok, detail = _probe_info(host, port, auth)
                row['http'] = ok
                row['httpDetail'] = detail
            else:
                row['http'] = None
                row['httpDetail'] = 'host 未設定'
            e = disc_by_id.get(cid)
            row['beacon'] = bool(e) and (now - e['lastSeen'] < 12.0)
            row['beaconAgeSec'] = round(now - e['lastSeen'], 1) if e else None
            row['beaconIp'] = e['ip'] if e else None
            results.append(row)
        age = (now - _unity_status['at']) if _unity_status['at'] else None
        quest = {'alive': age is not None and age < 6.0,
                 'ageSec': round(age, 1) if age is not None else None,
                 'activeCamera': _unity_status.get('activeCamera')}
        return self._json({'cameras': results, 'quest': quest,
                           'discoveryEnabled': DISCOVERY_ENABLED})

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

    # 撮影フォルダ（recordings/ 等）をファイルマネージャで開く。
    def _open_dir(self, key):
        target = SAVE_DIRS.get(key)
        if not target:
            return self._json({'ok': False, 'error': 'bad dir'}, 400)
        os.makedirs(target, exist_ok=True)
        try:
            if sys.platform.startswith('win'):
                subprocess.Popen(['explorer', target])
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', target])
            else:
                subprocess.Popen(['xdg-open', target])
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
        if parsed.path == '/export-build':
            return self._export_build()
        if parsed.path == '/save':
            q = parse_qs(parsed.query)
            typ = q.get('type', ['image'])[0]
            to = q.get('to', ['captures'])[0]
            dest = SAVE_DIRS.get(to, CAPTURES)
            cam = (q.get('cam', [''])[0]) or ''
            cam = cam if re.fullmatch(r'[A-Za-z0-9_\-]{1,16}', cam) else ''
            ext = 'webm' if typ == 'video' else 'jpg'
            length = int(self.headers.get('Content-Length', 0))
            data = self.rfile.read(length) if length else b''
            if not data:
                return self._json({'ok': False, 'error': 'empty body'}, 400)
            stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S_%f')[:-3]
            prefix = f'cam{cam}_' if cam else 'cap_'
            name = f'{prefix}{stamp}.{ext}'
            with open(os.path.join(dest, name), 'wb') as f:
                f.write(data)
            url = ('/recordings/' if dest is RECORDINGS else '/captures/') + name
            return self._json({'ok': True, 'name': name, 'url': url, 'dir': to,
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
    _STATE_KEYS = ('cameras', 'cues', 'post', 'control', 'layout', 'schedule')

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
            elif typ == 'setAutoFollow':
                ctrl['autoFollow'] = bool(body.get('on'))
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

    # ローカル URL（/masks/... /captures/... /static-inputs/... /recordings/...）を実ファイルへ解決。
    # 外部 http URL・空・sa:// は None（＝焼き込み対象外）。パストラバーサルは拒否。
    def _resolve_local_asset(self, url):
        if not url or not url.startswith('/'):
            return None
        clean = url.split('?', 1)[0]
        for prefix, base in LOCAL_URL_DIRS.items():
            if clean.startswith(prefix):
                from urllib.parse import unquote
                name = unquote(clean[len(prefix):])
                fp = os.path.abspath(os.path.join(base, name))
                # base の外へ出る参照は拒否
                if os.path.commonpath([fp, os.path.abspath(base)]) != os.path.abspath(base):
                    return None
                return fp if os.path.isfile(fp) else None
        return None

    # 現 show.json + 参照アセットを Assets/StreamingAssets/show/ へ焼き込む。
    # cues の maskUrl/sourceUrl の実ファイルを assets/ へコピーし URL を sa://assets/<file> に書換。
    def _export_build(self):
        import shutil
        show = json.loads(json.dumps(_show))  # deep copy（現物 _show は不変）
        out_dir = os.path.join(REPO_ROOT, 'Assets', 'StreamingAssets', 'show')
        assets_dir = os.path.join(out_dir, 'assets')
        os.makedirs(assets_dir, exist_ok=True)
        # 前回のエクスポート物を掃除（orphan 蓄積とファイル名衝突を防ぐ。show/assets 配下のみ）。
        for n in os.listdir(assets_dir):
            p = os.path.join(assets_dir, n)
            if os.path.isfile(p):
                try:
                    os.remove(p)
                except OSError:
                    pass

        copied = []            # [{from, to, size}]
        used_names = set()     # 本エクスポートで割当済みのファイル名
        src_to_dest = {}       # 実ファイル abs → dest 名（同一ファイルは 1 回だけコピー）

        def bake(url):
            fp = self._resolve_local_asset(url)
            if not fp:
                return url  # 外部 URL / 空 / 解決不能はそのまま
            if fp in src_to_dest:
                return 'sa://assets/' + src_to_dest[fp]
            base = os.path.basename(fp)
            stem, ext = os.path.splitext(base)
            name, i = base, 1
            while name in used_names:  # 別ファイルの同名は連番回避
                name = f'{stem}_{i}{ext}'
                i += 1
            used_names.add(name)
            src_to_dest[fp] = name
            dest = os.path.join(assets_dir, name)
            shutil.copy2(fp, dest)
            copied.append({'from': url, 'to': 'assets/' + name, 'size': os.path.getsize(dest)})
            return 'sa://assets/' + name

        for cue in show.get('cues', []):
            if cue.get('maskUrl'):
                cue['maskUrl'] = bake(cue['maskUrl'])
            if cue.get('sourceUrl'):
                cue['sourceUrl'] = bake(cue['sourceUrl'])

        show_path = os.path.join(out_dir, 'show.json')
        # UTF-8 / LF 固定（Unity JsonUtility が読む契約）。
        with open(show_path, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(show, f, ensure_ascii=False, indent=2)
        total = sum(c['size'] for c in copied)
        return self._json({'ok': True, 'outDir': out_dir, 'showJson': show_path,
                           'copied': copied, 'count': len(copied), 'totalBytes': total})

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
    _start_discovery(port)
    ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever()
