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
#   GET  /unity/devices          : 機ごとの heartbeat（deviceId を名乗る機・0185 / 0187）
#   GET  /scenarios/list         : 🕹 記録済みシナリオ一覧（本体は /scenarios/<name>.json で静的配信）
#   POST /scenarios/save         : {name, scenario} を scenarios/<name>.json へ保存（show.json は不変）
#   GET  /dwell/stats            : 区間 (lap,camera) の実測滞在時間の集計（heartbeat の dwell[] 由来）
#   POST /dwell/reset            : 実測滞在の集計をクリア（会場が変わった / リハをやり直す時）
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
import copy
import datetime
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid as _uuidlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, quote, unquote, urlencode

ROOT = os.path.dirname(os.path.abspath(__file__))

# 焼き込みの中身は別モジュール（卓の 📦 と `tools/export-show-build.py` が同じ関数を通るため）。
# ⚠ このファイルは importlib で読まれることがある（テスト）。その経路では自分の居る場所が
#    sys.path に入らないので、ここで自分で足す。
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)
import export_build as _export  # noqa: E402  （sys.path を整えた後でないと読めない）
import unity_devices as _udev  # noqa: E402  機ごとの heartbeat（0185 / 0187）

CAPTURES = os.path.join(ROOT, 'captures')
MASKS = os.path.join(ROOT, 'masks')
# デモ撮影専用フォルダ（静止画 📷 / 録画 ⏺ の保存先。captures とは分ける）
RECORDINGS = os.path.join(ROOT, 'recordings')
STATIC_INPUTS = os.path.join(ROOT, 'static-inputs')
# 動作確認用のダミー素材（「演出A」等の文字だけ）。make-test-assets.py で再生成できる。
# 実素材（captures/recordings）と混ざらないよう別フォルダに置き、素材一覧では末尾に並べる。
TESTASSETS = os.path.join(ROOT, 'testassets')
# 旧環境で撮った素材の退避先。**素材一覧には出さない**（もう使えないものを選ばせない）。
ARCHIVE = os.path.join(ROOT, 'archive')
# BGM 音源置き場（ここに mp3/ogg/wav/m4a を放り込むと卓の BGM ライブラリに出る）
AUDIO = os.path.join(ROOT, 'audio')
# 🕹 ショーシミュレーションで記録した「歩き方」（scenario JSON）。show.json とは混ぜない
# （ショーの設定と検証入力を分ける・計画 2026-07-25_show-simulator.md §6）。
SCENARIOS = os.path.join(ROOT, 'scenarios')
# 目の視界ジャックに流す当日写真（canon/LEDGER.md 0099）。
#   eyejack/      ← 当日、撮った写真をそのまま放り込む場所（人が触るのはここだけ）
#   eyejack/norm/ ← サーバが正規化したもの（実際に配るのはこちら）
# ⚠ **正規化は必ず PC で済ませる。** 12MP をそのまま端末へ配ると、Quest の
#   Texture2D.LoadImage で 1 枚 50MB 級の一時確保が走り、EXIF の回転も効かない（縦写真が横を向く）。
EYEJACK = os.path.join(ROOT, 'eyejack')
EYEJACK_NORM = os.path.join(EYEJACK, 'norm')
os.makedirs(CAPTURES, exist_ok=True)
os.makedirs(MASKS, exist_ok=True)
os.makedirs(RECORDINGS, exist_ok=True)
os.makedirs(AUDIO, exist_ok=True)
os.makedirs(TESTASSETS, exist_ok=True)
os.makedirs(EYEJACK_NORM, exist_ok=True)

# /save?to= と /open-dir?dir= の保存先ホワイトリスト（パストラバーサル防止）
SAVE_DIRS = {'captures': CAPTURES, 'recordings': RECORDINGS}

def _ffmpeg_exe():
    """ffmpeg の実行ファイル。PATH に無ければ imageio-ffmpeg の同梱版へ落ちる。無ければ None。"""
    exe = shutil.which('ffmpeg')
    if exe:
        return exe
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        return None


def _transcode_to_mp4(src):
    """
    録画（webm/VP9）を **mp4(H.264)** へ変換する。戻り値は (mp4 のパス or None, 人が読む注記)。

    ⚠⚠ **Quest へ配る動画は mp4 でなければならない。** ブラウザの MediaRecorder は
      webm しか吐かないが、Unity の VideoPlayer が Android で VP9 を再生できるかは端末依存で、
      **当日に「動画のカットだけ出ない」で詰む**（実際に動いている cue はすべて mp4）。
      変換に失敗したら **webm を残して注記を返す**（保存そのものは失敗させない）。

    ⚠ `-pix_fmt yuv420p` は必須。MediaRecorder の出力は yuv444 になることがあり、
      Android の MediaCodec がそれを開けない。
    """
    if not src.lower().endswith('.webm'):
        return None, ''
    exe = _ffmpeg_exe()
    if not exe:
        return None, 'ffmpeg が無いので webm のまま保存した（Quest で再生できない可能性がある）'
    dst = src[:-5] + '.mp4'
    cmd = [exe, '-y', '-loglevel', 'error', '-i', src,
           '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20',
           '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-an', dst]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=300)
    except Exception as e:
        return None, f'mp4 変換に失敗（webm のまま）: {e}'
    if r.returncode != 0 or not os.path.exists(dst) or os.path.getsize(dst) == 0:
        err = (r.stderr or b'').decode('utf-8', 'replace').strip().splitlines()
        return None, 'mp4 変換に失敗（webm のまま）: ' + (err[-1] if err else f'exit {r.returncode}')
    try:
        os.remove(src)   # 同じ中身が 2 つ並ぶと、素材一覧でどちらを選んだか分からなくなる
    except OSError:
        pass
    return dst, 'mp4 へ変換した（Quest で再生できる形式）'

def _probe_media(path):
    """
    動画の尺・寸法・符号化を測る（当日の素材が実機で再生できるかの検分）。

    ffmpeg の `-i` は stderr に情報を吐いて exit 1 で終わる（入力だけで出力が無いため）ので、
    **戻り値ではなく stderr を読む**。ffmpeg が無ければ空の dict（判定しない側へ倒す）。
    """
    exe = _ffmpeg_exe()
    if not exe or not os.path.isfile(path):
        return {}
    try:
        r = subprocess.run([exe, '-hide_banner', '-i', path],
                           capture_output=True, timeout=30)
    except Exception:
        return {}
    err = (r.stderr or b'').decode('utf-8', 'replace')
    out = {}
    m = re.search(r'Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)', err)
    if m:
        out['durSec'] = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
    m = re.search(r'Video:\s*([A-Za-z0-9]+)', err)
    if m:
        out['codec'] = m.group(1)
    # ⚠ 画素形式は括弧の中に**カンマを含む**（`yuv420p(tv, smpte170m, progressive), 480x640`）。
    #   `[^,]*` で読み飛ばそうとすると解像度の手前で外れて、符号化も寸法も黙って取れない。
    m = re.search(r',\s*([a-z][a-z0-9]*)(?:\([^)]*\))?,\s*(\d{2,5})x(\d{2,5})', err)
    if m:
        out['pixFmt'] = m.group(1)
        out['width'] = int(m.group(2))
        out['height'] = int(m.group(3))
    m = re.search(r'([\d.]+)\s*fps', err)
    if m:
        out['fps'] = float(m.group(1))
    return out


def _trim_head(src, in_point_sec):
    """
    素材の頭を `in_point_sec` だけ送った**別ファイル**を作る。戻り値は (パス or None, 注記)。

    端末内録画は全フレームが I フレームなので `-c copy` でも**正確に切れる**（再圧縮しない ＝
    画質が 1 ビットも落ちない）。ブラウザ録画由来の素材はキーフレーム間隔が長いので、
    `-c copy` だと指定より手前へ寄る。そこは注記で言う（黙って寄せない）。
    """
    exe = _ffmpeg_exe()
    if not exe:
        return None, 'ffmpeg が無いので頭を切れない（頭を送らずに採用してください）'
    stem, ext = os.path.splitext(os.path.basename(src))
    dst = os.path.join(RECORDINGS, f'{stem}-in{in_point_sec:.2f}'.replace('.', '_') + ext)
    if os.path.exists(dst):
        return dst, '同じ頭で切ったものが既にあったので再利用した'
    cmd = [exe, '-y', '-loglevel', 'error', '-ss', f'{in_point_sec:.3f}', '-i', src,
           '-c', 'copy', '-movflags', '+faststart', '-an', dst]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=120)
    except Exception as e:
        return None, f'頭の切り出しに失敗: {e}'
    if r.returncode != 0 or not os.path.exists(dst) or os.path.getsize(dst) == 0:
        err = (r.stderr or b'').decode('utf-8', 'replace').strip().splitlines()
        return None, '頭の切り出しに失敗: ' + (err[-1] if err else f'exit {r.returncode}')
    return dst, f'頭を {in_point_sec:.2f} 秒送った（再圧縮していない）'


# ---- ショット定義（shots.json）と「頭に何秒要るか」------------------------------
#
# ⚠ **定義は shots.json が単一の正**。読む相手が 3 つある —
#   卓のブラウザ（shoot-model.js）・ここ・**配信スマホ**（GET /shoot/plan で撮影パネルへ配る）。
#   スマホが自前で持つと、指示文と尺が黙って古くなる（現場で気づけない類の食い違い）。
#
# ⚠ 下の 2 つは shoot-model.js の `neededHeadSec` / `cutCount` の移植。
#   **同じフィクスチャで両方をテストする**（`shoot-fixture.json` を node と Python の双方が食う）。
#   片方だけ直せばテストが落ちる — 移植を「気をつける」で守らない。

SHOTS_JSON = os.path.join(ROOT, 'shots.json')
_shots_cache = {'mtime': None, 'shots': []}


def _shots_def():
    """shots.json のショット定義（mtime で読み直す）。読めなければ空。"""
    try:
        m = os.path.getmtime(SHOTS_JSON)
    except OSError:
        return []
    if _shots_cache['mtime'] != m:
        try:
            with open(SHOTS_JSON, encoding='utf-8') as f:
                d = json.load(f)
            _shots_cache['shots'] = [s for s in (d.get('shots') or []) if s.get('cueId')]
            _shots_cache['mtime'] = m
        except Exception as e:
            print(f'[shots] shots.json が読めない: {e}', file=sys.stderr)
            return _shots_cache['shots']
    return _shots_cache['shots']


def _num(v):
    try:
        f = float(v)
    except (TypeError, ValueError):
        return 0.0
    return f if f == f and f not in (float('inf'), float('-inf')) else 0.0


def needed_head_sec(segments, cue_id):
    """
    その cue の「頭から何秒が画に出るか」。同じ素材を複数のカットが trimStartSec を
    進めながら使うので **max(trimStartSec + durSec)**。参照が無ければ None。
    durSec<=0 は尺が別の条件で決まるカット（untilZoneChange 等）なので数えない。
    """
    if not cue_id:
        return None
    out = None
    for seg in segments or []:
        for take in (seg.get('takes') or []):
            for st in (take.get('steps') or []):
                if not st or st.get('cueId') != cue_id:
                    continue
                dur = _num(st.get('durSec'))
                if dur <= 0:
                    continue
                trim = max(0.0, _num(st.get('trimStartSec')))
                out = max(0.0 if out is None else out, trim + dur)
    return out


def cut_count(segments, cue_id):
    """その cue を参照しているカットの数。"""
    n = 0
    for seg in segments or []:
        for take in (seg.get('takes') or []):
            for st in (take.get('steps') or []):
                if st and st.get('cueId') == cue_id:
                    n += 1
    return n


# 余裕がこれ以下なら警告（shoot-model.js の SHORT_MARGIN_SEC と対）。
SHORT_MARGIN_SEC = 0.5


def shot_status(cue, need, adopted_dur):
    """
    1 ショットの状態。`cue` は show.json の cue（無ければ None）、`adopted_dur` は
    採用中テイクの尺（分からなければ None）。返すのは (level, reason, fix)。
    """
    if not cue:
        return 'ng', 'この cue が show.json に無い', '卓のカメラ列で cue を作る'
    if not (cue.get('sourceUrl') or ''):
        return 'ng', '素材が未採用', '撮って「卓へ送って採用」'
    if need is not None and adopted_dur is not None and adopted_dur > 0:
        margin = adopted_dur - need
        if margin < 0:
            return ('ng',
                    f'尺が {-margin:.1f}s 足りない（要求 {need:.1f}s / 素材 {adopted_dur:.1f}s）',
                    f'{need:.1f}s より長く撮り直す')
        if margin < SHORT_MARGIN_SEC:
            return 'warn', f'余裕が {margin:.1f}s しかない', '撮り直して余裕を作る'
    return 'ok', '', ''


def _shoot_get(host, port, path, timeout=4.0):
    """配信端末の HTTP エンドポイントを叩いて JSON を返す。(ok, obj or 文字列)。"""
    try:
        r = urllib.request.urlopen(f'http://{host}:{port}{path}', timeout=timeout)
        body = r.read(1 << 20)
        try:
            return True, json.loads(body.decode('utf-8'))
        except Exception:
            return True, {'raw': body[:200].decode('utf-8', 'replace')}
    except Exception as e:
        return False, str(e)[:120]


# 当日の素材撮りの台帳（どのテイクをいつ・どの端末で撮ったか）。
#   show.json とは混ぜない（設定ではなく観測の記録。rev を上げると Quest へ無駄な再適用が飛ぶ）。
#   ⚠ 時刻は **卓の時計** で刻む。端末の時計は現場でずれるので信用しない。
SHOOT_MANIFEST = os.path.join(RECORDINGS, 'approach-manifest.json')
_shoot_lock = threading.Lock()


def _shoot_manifest():
    with _shoot_lock:
        try:
            with open(SHOOT_MANIFEST, encoding='utf-8') as f:
                d = json.load(f)
            return d if isinstance(d, dict) else {}
        except Exception:
            return {}


def _shoot_manifest_put(url, entry):
    with _shoot_lock:
        try:
            with open(SHOOT_MANIFEST, encoding='utf-8') as f:
                d = json.load(f)
            if not isinstance(d, dict):
                d = {}
        except Exception:
            d = {}
        d[url] = entry
        os.makedirs(RECORDINGS, exist_ok=True)
        tmp = SHOOT_MANIFEST + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(d, f, ensure_ascii=False, indent=1)
        os.replace(tmp, SHOOT_MANIFEST)
        return d


# /open-dir?dir= だけで開いてよいフォルダ（保存はしない）
OPEN_DIRS = {'testassets': TESTASSETS, 'archive': ARCHIVE, 'audio': AUDIO, 'masks': MASKS,
             'eyejack': EYEJACK}

# エクスポート時に「ローカル URL → 実ファイル」を解決するディレクトリ対応表。
LOCAL_URL_DIRS = {
    '/masks/': MASKS,
    '/captures/': CAPTURES,
    '/recordings/': RECORDINGS,
    '/static-inputs/': STATIC_INPUTS,
    '/audio/': AUDIO,
    '/testassets/': TESTASSETS,
    '/eyejack/norm/': EYEJACK_NORM,
}
# リポジトリルート（tools/web-compositor から 2 つ上）。エクスポート先の解決に使う。
REPO_ROOT = os.path.dirname(os.path.dirname(ROOT))

# ---- 画像生成ジョブ（🪄 Codex に生成させる）--------------------------------
#
#   卓のボタンから「いまのライブ映像を種にした差し替え素材」を作れるようにする。
#   実体はこの PC の Codex CLI（~/.claude/scripts/codex-run.ps1 -Mode image）で、1 枚 60〜150 秒。
#
#   安全のための約束（現場で本番運用する卓に載せるため）:
#     ・**127.0.0.1 からの要求だけ**受ける（サーバは 0.0.0.0 で listen しているので、
#       これが無いと同一 LAN の誰でもこの PC で外部プロセスを走らせられる）
#     ・Mode / 作業ディレクトリ / 出力ファイル名は**サーバが決める**（UI 入力から作らない）
#     ・作業ディレクトリは**リポジトリの外**の一時フォルダ。Codex は image モードでそこへ
#       書き込み権限を持つので、show.json のある場所を渡さない
#     ・同時 1 ジョブ・上限 300 秒。超えたら子ごと殺す（Windows は powershell を殺しても
#       codex の子が残るので taskkill /T /F）
#     ・生成は show.json を一切書かない（cue にするのは既存の 💾 経路だけ）
# 目の視界ジャックの写真を正規化するときの値（canon/LEDGER.md 0099）。
#   長辺: 全視界に出すので、これ以上あっても見た目が変わらないのに端末の確保だけが増える。
#   明るさ: **暗順応した視界へ出す**ので落とす（そのままだと眩しい。LEDGER 0015「不快にはならないように」）。
#   ⚠ 実機で見て決め直す値。ここを触ったら `menu eyejack` の絵と実機の走行を対で見る。
EYEJACK_LONG_EDGE = 1280
EYEJACK_BRIGHTNESS = 0.72

GEN_WORK = os.path.join(tempfile.gettempdir(), 'fixedcam-gen')
GEN_TIMEOUT_SEC = 300
CODEX_RUNNER = os.path.join(os.path.expanduser('~'), '.claude', 'scripts', 'codex-run.ps1')
_gen_lock = threading.Lock()
_gen_jobs = {}          # id -> {status, url, error, startedAt, finishedAt, cam, prompt, genId}


# ---- 生成の入出力でトーンを往復させる（canon/LEDGER.md 0050）--------------
#
#   種フレームは明るい部屋のままモデルへ渡していたので、**生成モデルは暗い画を一度も描いて
#   いなかった**。暗所の人形は暗いだけでなく陰影の付き方が違う（光源の向きが読める・輪郭が
#   闇へ沈む・顔の一部だけが光を拾う）ので、後段の post では作れない。
#   ⇒ 種へ post を掛けてから渡し、生成物から post を抜いてから captures/ へ置く。
#
#   ⚠ **暗いまま保存しない。** cue の素材は「そのカメラが撮ったならこう写る生映像」の位置に
#   入るので、暗いまま置くと ①実機で post が二重に掛かる ②半分マスクで境目に段差が出る
#   ③`LEDGER` 0020 と `rules/streaming.md`「合成はポスト FX の前」に反する。
#
#   ⚠ **失敗したら素通しで続ける**（生成は 1 枚 60〜150 秒で、トーンで落とすのは高い）。
#   ただし黙って素通しにすると効いていないことに気づけないので、job['tone'] に必ず残す。
GEN_TONE = os.path.join(os.path.dirname(os.path.dirname(ROOT)), 'tools', 'gen-tone.py')
GEN_TONE_SCALE = 1.0    # 1.0 = 実機と同じ暗さ。往復の実測は平均誤差 1.2 / 最大 24 / 潰れ 0.65%
GEN_TONE_TIMEOUT_SEC = 120


def _gen_tone(mode, src, dst, cam_label, scale=None):
    """`tools/gen-tone.py` を呼ぶ。(成否, 一行メモ) を返す。"""
    if not os.path.exists(GEN_TONE):
        return False, 'tools/gen-tone.py が見つからない'
    cmd = [sys.executable, GEN_TONE, mode, src, dst,
           '--scale', str(GEN_TONE_SCALE if scale is None else scale)]
    if cam_label:
        cmd += ['--cam', str(cam_label)]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=GEN_TONE_TIMEOUT_SEC, check=False)
    except Exception as e:                                    # noqa: BLE001 - 何で落ちても素通しへ
        return False, str(e)
    if r.returncode != 0 or not os.path.exists(dst):
        err = (r.stderr or r.stdout or b'').decode('utf-8', 'replace').strip()
        return False, err[-300:] or f'exit {r.returncode}'
    return True, (r.stdout or b'').decode('utf-8', 'replace').strip()[-300:]


def _gen_running():
    return any(j['status'] == 'running' for j in _gen_jobs.values())


def _gen_run(job_id, cam_label, seed_path, prompt, slug):
    """別スレッドで Codex を回し、できた PNG を captures/ へ置く。"""
    job = _gen_jobs[job_id]
    work = os.path.join(GEN_WORK, job_id)
    os.makedirs(work, exist_ok=True)
    proc = None
    try:
        # 種へ post を掛けてから渡す（モデルに実機の暗さを見せる）。落ちたら素の種で続ける。
        job['tone'] = {}
        seed_dim = os.path.join(work, 'seed_dim.png')
        ok, note = _gen_tone('dim', seed_path, seed_dim, cam_label)
        job['tone']['dim'] = ('掛けた: ' + note) if ok else ('素通し: ' + note)
        if ok:
            seed_copy = seed_dim
        else:
            seed_copy = os.path.join(work, 'seed' + os.path.splitext(seed_path)[1])
            shutil.copyfile(seed_path, seed_copy)
        out_png = os.path.join(work, 'out.png')
        prompt_file = os.path.join(work, 'prompt.txt')
        # 種フレームは**作業フォルダへコピーした方**を指す（リポジトリを触らせない）。
        full = (f'{prompt}\n\n--- 入力画像（必ず開いて見てから作業すること）---\n{seed_copy}\n'
                '\n--- 合成に必要な条件（必ず守る）---\n'
                '1. カメラ位置・画角・構図を入力画像とまったく同じにする\n'
                '2. 解像度は入力画像と同じ\n'
                '3. 照明・色温度・露出・ホワイトバランスを入力と揃える\n'
                '4. 入力画像に写っているものを消したり動かしたりしない（足すものだけ足す）\n'
                '5. 写真として自然であること（イラスト調・CG 調にしない）\n')
        with open(prompt_file, 'w', encoding='utf-8', newline='\n') as f:
            f.write(full)

        cmd = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', CODEX_RUNNER,
               '-PromptFile', prompt_file, '-Mode', 'image', '-Cwd', work,
               '-OutImage', out_png, '-OutDir', work]
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                cwd=work, creationflags=getattr(subprocess, 'CREATE_NEW_PROCESS_GROUP', 0))
        job['pid'] = proc.pid
        try:
            out, _ = proc.communicate(timeout=GEN_TIMEOUT_SEC)
        except subprocess.TimeoutExpired:
            subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                           capture_output=True, check=False)
            raise TimeoutError(f'{GEN_TIMEOUT_SEC} 秒を超えました')
        tail = (out or b'').decode('utf-8', 'replace')[-1200:]
        job['log'] = tail
        if not os.path.exists(out_png):
            raise RuntimeError('画像ができませんでした（Codex の応答は log を参照）')

        stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
        name = f'gen_cam{_atelier_slug(cam_label, "X")}_{_atelier_slug(slug, "free")}_{stamp}.png'
        dest = os.path.join(CAPTURES, name)
        # 生成物から post を抜いて素材にする（実機でもう一度 post を浴びるため）。
        ok, note = _gen_tone('undim', out_png, dest, cam_label)
        job.setdefault('tone', {})['undim'] = ('抜いた: ' + note) if ok else ('素通し: ' + note)
        if not ok:
            shutil.copyfile(out_png, dest)
        url = '/captures/' + name
        job.update(status='done', url=url, finishedAt=time.time())
        _gen_record(job.get('genId'), {'status': 'done', 'outputUrl': url,
                                       'tone': job.get('tone')})
    except Exception as e:                                    # noqa: BLE001 - 何で落ちても UI へ返す
        job.update(status='failed', error=str(e), finishedAt=time.time())
        _gen_record(job.get('genId'), {'status': 'failed', 'note': str(e)})
    finally:
        if proc is not None and proc.poll() is None:
            subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                           capture_output=True, check=False)


def _gen_record(gen_id, patch):
    """素性（種フレーム・プロンプト・結果）を atelier.json の生成記録へ残す。"""
    if not gen_id:
        return
    with _atelier_lock:
        st = _load_atelier()
        rec = next((g for g in st['generations'] if g.get('id') == gen_id), None)
        if rec is None:
            return
        rec.update(patch)
        _save_atelier(st)


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
        # role: 'zone'（既定・周回ゾーンに割り当てる）/ 'fx'（演出専用＝カメラ D）。
        #   fx はスタッフの A ボタン巡回にもゾーン自動切替にも出ない。演出のカットからだけ映せる。
        # pose: CG 人形を立てるための実カメラ姿勢（course 空間）。著作するまでキー自体を置かない
        #   （Unity は pose の有無で hasPose を決める）。フロアマップの 📐 カメラ姿勢モードで編集する。
        'cameras': [
            {'id': 'A', 'sourceId': 'Phone01', 'host': '', 'port': 8080, 'auth': '', 'pinned': False, 'role': 'zone'},
            {'id': 'B', 'sourceId': 'Phone02', 'host': '', 'port': 8080, 'auth': '', 'pinned': False, 'role': 'zone'},
            {'id': 'C', 'sourceId': 'Phone03', 'host': '', 'port': 8080, 'auth': '', 'pinned': False, 'role': 'zone'},
            {'id': 'D', 'sourceId': 'Phone04', 'host': '', 'port': 8080, 'auth': '', 'pinned': False, 'role': 'fx'},
        ],
        'cues': [],
        # 全体グレーディングの出荷値 =「暗めの監視カメラ」（common.js FX_CCTV / Unity ScreenMjpeg.mat と同じ値）。
        # 現場が明るくて見えない時に動かすのは露出だけでよい。
        # ⚠ grain / scanline は 0（2026-08-12・canon/LEDGER.md 0018）。走査線はこの経路に発生源が無く、
        #    粒は実機のセンサ段（feel）へ一本化した。3 者（ここ / common.js / ScreenMjpeg.mat）で揃える。
        'post': {'exposure': -1.05, 'contrast': 1.12, 'saturation': 0.52, 'temperature': 0.48,
                 'tint': 0.0, 'lift': 0.02, 'vignette': 0.38, 'grain': 0.0, 'scanline': 0.0},
        # BGM ライブラリ（audio/ の音源から作る）と、ランの既定 BGM。
        # timeline.segments[].bgm が区間ごとに切替・停止を指示する。既定 bgm が無ければ
        # Unity は APK 同梱の既定クリップ（従来の固定ループ）を鳴らす。
        'bgmTracks': [],
        'bgm': {'action': 'continue', 'trackId': '', 'loop': True, 'startSec': 0,
                'loopStartSec': -1, 'loopEndSec': -1, 'volume': -1,
                'fadeInSec': 1.0, 'fadeOutSec': 1.0, 'restart': False},
        # autoFollow=True: discovery で発見したカメラ IP を cameras[i].host へ自動反映する
        # （pinned カメラは除外）。UI トグルで切替。欠落は ON 扱い（後方互換）。
        # runEpoch: 体験者 1 人分の「ラン」世代。Web の ▶ ラン開始が +1 して postState
        # （control を shallow 置換で送り直す）。値が変わると Unity は周回カウントと
        # once 発火済みフラグをリセット（既定 0・欠落は 0 扱い）。
        # takeAbortEpoch: 走行中の演出（Take）の中止世代。Web の ■ 画面を取り返す が +1 する。
        # activeCue と違い「空にする」形では伝わらない（自動発火の演出は activeCue が空のまま走るため）。
        # glitchEpoch / introAdvanceEpoch / runEndEpoch も同じ「値の変化で伝える」流儀。
        # 「空を空にする」形の指示は long-poll では原理的に届かないので、単発の合図は必ず世代カウンタにする。
        'control': {'activeCue': None, 'cameraOverride': None, 'autoFollow': True, 'runEpoch': 0,
                    'takeAbortEpoch': 0,
                    # Quest 内の発見プロトコルのキルスイッチ。欠落は ON 扱い（後方互換）。
                    'discoveryEnabled': True,
                    # 素材スロットの束縛（slot://name → 実 URL）。ラン中に差し替える素材はここ。
                    'slots': [],
                    # ゾーン切替そのものに重ねる「映像の乱れ」の強さ（0 = 重ねない）。
                    'switchGlitch': 0,
                    # 卓からの手動の乱れ（⚡ ボタン）。
                    'glitchEpoch': 0, 'glitchLevel': 0.8, 'glitchSec': 0.3,
                    # 導入を終える / 体験を終える の合図。
                    'introAdvanceEpoch': 0, 'runEndEpoch': 0},
        # 体験 1 回の骨格（企画書 3 章「3 区間を 3 周・導入を含め 3 分以内・各周およそ 30 秒」）。
        # totalLaps 周を回り、元の位置（course.order[0]）へ戻ったところで Unity は暗転して終了する
        # （lap = totalLaps + 1 の order[0] ＝「もどり」の区間だけは必ず踏む）。
        # targetSec は表示専用（超過しても止めない）。hardLimitSec は動かない体験者への保険（0 で無効）。
        # endGraceSec = 終わる条件が揃ってから演出の開始を待つ秒数（もどりの区間の演出はこの中でしか始まれない）。
        # endHoldMaxSec = そのとき走っている演出を見せ切る上限。どちらも 0 は「未指定」で Unity 既定へ。
        # ⚠ この 2 つは tools/web-compositor/run-model.js の RUN_DEFAULT と一致していること
        #   （run-wiring.test.mjs が両者を突き合わせる。片方だけ直すと沈黙して食い違う）。
        # run.intro = 導入の遷移演出（現実 → 固定カメラの映像）。段ごとの秒と on/off だけを持つ。
        # 導入は 1 種類でよく、演出（takes）として著作可能にしない（自由度を持たせると
        # 「導入が壊れている show.json」を作れてしまう）。
        'run': {'totalLaps': 3, 'introEnabled': True, 'introMinSec': 20, 'introAutoAdvance': True,
                'targetSec': 180, 'hardLimitSec': 300, 'endFadeSec': 1.5,
                'endGraceSec': 3.0, 'endHoldMaxSec': 60.0,
                # 尺は 2026-08-15 に旧構成へ戻した（段 3 は段 2 と重なるので実尺 12.7s。
                # 段 4 は 2026-09-15 に 2.5 → 5.0 秒・canon/LEDGER.md 0221）。
                # ⚠ この値は tools/web-compositor/intro-model.js の INTRO_DEFAULT と一致していること
                #   （intro-model.test.mjs が両者を突き合わせる。片方だけ直すと沈黙して食い違う）。
                'intro': {'enabled': True, 'maxSec': 20,
                          'realSec': 1.5, 'degradeSec': 3.5, 'structureSec': 2.5,
                          'frameSec': 5.0, 'swapSec': 1.6,
                          # 構造の線は既定で出さない（2026-08-01。細い線が現実に重なると計測器に見える）。
                          # Unity の ShowIntroDef / 卓の INTRO_DEFAULT と 3 者で揃えること。
                          'edgeColor': '#ffcf9e', 'showCameraMarks': False, 'showRoomWire': False,
                          'glitchOnSwap': 0.0}},
        # 撮像の質（装置らしさ）。post 12 項目と違って**時間で動く**ので別系統。
        # ここの既定は C# ShowFeelDef / 卓 FEEL_DEFAULT と 3 者で揃えること。
        'feel': {'noiseDark': 0.10, 'noiseFixed': 0.035, 'agc': 0.7,
                 'targetLuma': 0.34, 'followSec': 1.1},
        # 端末内録画（前の周を録って 3 周目の演出で流す）。既定は無効。
        # 残るのは切り替えの tailSec 秒前 〜 postSec 秒後（頭からではない）。既定 3 / 2 は
        # C# SegmentRecordWriter.DefaultTailSec / DefaultPostSec / 卓 REC_DEFAULT_* と対。
        'record': {'enabled': False, 'laps': [1], 'tailSec': 3, 'postSec': 2,
                   # 録り始めの線（空 = 末尾方式）。Unity 側 ShowRecordDef.startLineId と対。
                   'startLineId': '',
                   'maxTotalMB': 200, 'fpsCap': 15},
        # CG レイヤに立てる人形の定義（cameras[i].pose が著作済みのカメラでのみ出る）。
        'actors': [],
        # 目の視界ジャックに流す当日写真（canon/LEDGER.md 0099）。
        # 卓の「👁 目の写真」パネルが eyejack/norm/ の一覧をここへ焼く（ファイル名順 = 決定的）。
        # 空 = ジャックは出ない（目は従来どおり）。
        'eyejack': {'photos': []},
        # レンズ（内部パラメータ）。cameras[i].lensRef が参照する。
        # 較正で「画角を固定して解く」ときの供給源で、**同型機で共有できる**のが要点
        # （旧: 供給源が「そのカメラの前回の解」だけで、4 点で雑に解いた f が翌日
        #  「正確な内部パラメータ」として再利用されていた。f を固定すると rms は下がるので
        #  誤りが良い数字に化けて発見できない）。測定条件を一緒に焼いて素性を追えるようにする。
        # ⚠ Unity は lenses を読まない（cameras[i].calib.fxPx に解決済みの値が入る）。
        'lenses': [],
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
        # lines = 通過ライン（演出の発火点となる床の線分）。各要素 {id, camera, x1, z1, x2, z2, dir, label}。
        # timeline の演出が at:"line" + lineId で参照する（TakeRunner が読む）。未著作なら省略。
        # camera = 担当カメラ index。**その区間のカメラと一致しないラインは踏んでも発火しない**。
        # regPoints = HMD 位置合わせのタッチ基準点（course space・順序=タッチ順・2〜5 点）。
        # 各要素 {x, z, label?}。フロアマップ UI が編集。未設定（下記のように省略）なら Unity は
        # 既定 2 点 (-0.5,0.5)/(0.5,0.5) へフォールバックする。フィールドを足さなくても layout は
        # shallow 置換で丸ごと通るため、UI が保存すれば自動で乗る（focus: 後方互換維持）。
        # regTouchHeightM = 位置合わせでコントローラを構える高さ（床から m）。0 = 床に着ける。
        # 実機はここから床の高さを測るので、実際の構え方と食い違うとゾーンが上下にずれる。
        # startSpot = 導入演出を始める床の 1 点 {x, z, radiusM, label}。**既定を書かない**
        # （未設定＝スタッフが手で始める運用が既定の姿。勝手に (0,0) へ置くと「立っても
        #  始まらない」を現場で初めて知ることになる）。フロアマップの 🎬 開始位置 で置く。
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
            'regTouchHeightM': 0,
        },
        # schedule = 旧・事前オーサリング（何周目 lap のどのゾーン camera で cueId を発火するか）。
        # timeline（下記）が存在すれば supersede される（後方互換のため残す）。
        'schedule': {'rev': 1, 'entries': []},
        # timeline = 事前オーサリングの正面（周回×ゾーン区間 = セグメント）。
        # segments[i] = {lap, camera, cues:[{cueId,delaySec,once,override,hasOverride}],
        #                post, hasPost, insert:{anchor,camera,delaySec,durationSec,cueId,once,post,hasPost}, hasInsert}。
        # lap は 1 始まり、camera はカメラ index。cueId は cues[].id。Web 卓が編集し export-build で焼き込む。
        # timeline.rev > 0 && segments 非空 で schedule.entries を Unity 側が無視する。
        'timeline': {'rev': 1, 'segments': []},
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
# 端末ごとの直近 heartbeat（deviceId → body）。2 台が 1 スロットを交互に上書きする問題
# （memory/onsite_day_ops.md §3）の受け皿。deviceId を名乗らない旧 APK はここに入らない。
# ⚠ タブレットの設定（言語・軽減）は卓を通らない — Quest 自身の口（:8090）へ直接繋ぐ（0187）。
#   ここに載るのは「口が開いているか」と実値だけ（スタッフが眺める用）。
_unity_devices = {}
# _unity_status は heartbeat スレッドが書き、/unity/status と /diag が読む（ThreadingHTTPServer =
# リクエストごとに別スレッド）。clear()+update() の隙間で読むと KeyError / dict changed size で 500 になり、
# 卓が「サーバ断」を誤表示する（2026-07-26 監査 MED）。読み書きを必ずこのロックで囲む。
_unity_status_lock = threading.Lock()


def _unity_status_snapshot():
    with _unity_status_lock:
        return dict(_unity_status)


def _unity_devices_snapshot():
    with _unity_status_lock:
        return {k: dict(v) for k, v in _unity_devices.items()}


def _mutate_show(fn):
    """_show を fn で変更し rev++ → long-poll を起こして永続化。

    書き込みは「1 世代バックアップ → tmp へ書く → os.replace」の順。
    UI 側に undo が無い破壊的編集（周回削除・タイル塗り潰し等）から手で戻せるようにする。
    復旧は show.json.bak を show.json にリネームしてサーバ再起動。
    """
    with _show_cond:
        fn(_show)
        _show['rev'] = int(_show.get('rev', 0)) + 1
        try:
            if os.path.isfile(SHOW_FILE):
                shutil.copyfile(SHOW_FILE, SHOW_FILE + '.bak')
        except OSError:
            pass  # バックアップ失敗で本書き込みを止めない
        tmp = SHOW_FILE + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(_show, f, ensure_ascii=False, indent=2)
        os.replace(tmp, SHOW_FILE)  # 書き込み途中で落ちても show.json は壊れない
        _show_cond.notify_all()
        return _show['rev']

# 実測滞在時間（区間 (lap, camera) に体験者が居た秒数）の集計。
#   Unity が heartbeat の dwell[] で「確定した滞在」を送ってくる（SegmentDwellLog）。
#   ここで平均・最短・最長・回数へ畳み、GET /dwell/stats で卓 UI（リボン）へ返す。
#   リボンは「進入 +20s の演出が実測平均 8s の区間に置かれている」を作者に見せるために使う。
#   show.json とは混ぜない（設定ではなく観測データ。rev を上げると Quest へ無駄な再適用が飛ぶ）。
DWELL_FILE = os.path.join(ROOT, 'dwell_stats.json')
_dwell_lock = threading.Lock()


def _load_dwell():
    try:
        with open(DWELL_FILE, 'r', encoding='utf-8') as f:
            d = json.load(f)
        if isinstance(d, dict) and isinstance(d.get('items'), dict):
            return d
    except Exception:
        pass
    return {'items': {}, 'updatedAt': 0}


_dwell_stats = _load_dwell()


def _save_dwell():
    tmp = DWELL_FILE + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(_dwell_stats, f, ensure_ascii=False, indent=2)
        f.write('\n')
    os.replace(tmp, DWELL_FILE)


def _merge_dwell(samples):
    """heartbeat の dwell[]（{lap,camera,sec}）を集計へ畳む。壊れた要素は黙って捨てる。"""
    if not isinstance(samples, list) or not samples:
        return 0
    added = 0
    with _dwell_lock:
        items = _dwell_stats.setdefault('items', {})
        for s in samples:
            if not isinstance(s, dict):
                continue
            try:
                lap = int(s.get('lap', -1))
                cam = int(s.get('camera', -1))
                sec = float(s.get('sec', 0))
            except (TypeError, ValueError):
                continue
            # 上限は watchdog より十分大きい値。異常値（時計飛び）で平均を壊さない。
            if lap < 1 or cam < 0 or not (0.05 <= sec <= 600):
                continue
            key = f'{lap}:{cam}'
            e = items.get(key)
            if not e:
                e = {'n': 0, 'sumSec': 0.0, 'minSec': sec, 'maxSec': sec, 'lastSec': sec}
                items[key] = e
            e['n'] = int(e.get('n', 0)) + 1
            e['sumSec'] = float(e.get('sumSec', 0.0)) + sec
            e['minSec'] = min(float(e.get('minSec', sec)), sec)
            e['maxSec'] = max(float(e.get('maxSec', sec)), sec)
            e['lastSec'] = sec
            added += 1
        if added:
            _dwell_stats['updatedAt'] = time.time()
            try:
                _save_dwell()
            except OSError:
                pass  # 保存失敗でメモリ上の集計は捨てない
    return added


def _dwell_payload():
    """UI 用に平均を付けて返す（items は key='lap:camera'）。"""
    with _dwell_lock:
        out = {}
        for k, e in _dwell_stats.get('items', {}).items():
            n = int(e.get('n', 0))
            if n <= 0:
                continue
            out[k] = {
                'n': n,
                'meanSec': round(float(e.get('sumSec', 0.0)) / n, 2),
                'minSec': round(float(e.get('minSec', 0.0)), 2),
                'maxSec': round(float(e.get('maxSec', 0.0)), 2),
                'lastSec': round(float(e.get('lastSec', 0.0)), 2),
            }
        return {'items': out, 'updatedAt': _dwell_stats.get('updatedAt', 0)}


# 旧「生成プロンプト」ストア。**読み出し専用の移行元**（API は 2026-07-30 に撤去した）。
#   プロンプト本文しか持たず「それで何が出たのか」が分からないので実質使えず、UI は 2026-07-28 に
#   撤去済みだった。中身は起動時に一度だけ素材台帳へ移す（_migrate_prompts_into_atelier）。
#   ファイル自体は消さない — 移行が失敗しても手で拾えるように残す。
PROMPTS_FILE = os.path.join(ROOT, 'prompts.json')


def _load_prompts():
    try:
        with open(PROMPTS_FILE, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return []


# ---- 素材工房ストア（atelier.json）--------------------------------------------
# 「プロンプトだけ貯めても、使った結果が分からないので役に立たない」への答え。
# 保存する単位を **プロンプト → 生成（入力フレーム + 指示 + 出力 + 採否）** に変える。
#
#   recipes[]     場所に依存しない演出テンプレ。{{スロット}} を持つ。カメラをまたいで再利用する
#   generations[] 1 回の生成。入力フレーム・使ったレシピ・束縛値・全文プロンプト・出力・採否を全部持つ
#
# 意図的な冗長: cameraLabel / recipeName を各レコードに焼き込む。1 レコードだけ読んで
# 意味が分かる状態にしておく（人間にとっても、次のセッションのシュビーにとっても）。
# 同じ理由で保存のたびに atelier-index.md を書き出す（Read 1 回で全体が読める索引）。
ATELIER_FILE = os.path.join(ROOT, 'atelier.json')
ATELIER_INDEX = os.path.join(ROOT, 'atelier-index.md')
_atelier_lock = threading.Lock()

ATELIER_EMPTY = {'rev': 0, 'recipes': [], 'generations': []}


def _load_atelier():
    try:
        with open(ATELIER_FILE, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception:
        return json.loads(json.dumps(ATELIER_EMPTY))
    if not isinstance(data, dict):
        return json.loads(json.dumps(ATELIER_EMPTY))
    data.setdefault('rev', 0)
    data.setdefault('recipes', [])
    data.setdefault('generations', [])
    return data


def _norm_asset_url(u):
    """素材 URL の突合キー。percent-encode の有無で別物にしない。"""
    return unquote(str(u or '')).strip()


def _migrate_prompts_into_atelier(st):
    """
    旧 `/prompts`（prompts.json）の中身を素材台帳へ **1 回だけ**移す。

    UI は 2026-07-28 に撤去されたが、そこには 700〜1000 字の作り込まれたプロンプトが 7 件
    残っていて、**作者からは見えないのに消すと二度と戻らない**状態だった。撤去の前に移す。
    元ファイルは消さない（移行が失敗しても手で拾えるように）。
    """
    if st.get('promptsMigratedAt'):
        return st
    items = _load_prompts()
    if isinstance(items, list) and items:
        have = {(r.get('body') or '').strip() for r in st.get('recipes', [])}
        taken = {r.get('id') for r in st.get('recipes', [])}
        for it in items:
            body = (it.get('text') or '').strip()
            if not body or body in have:
                continue
            rid = _atelier_new_id('r_', taken)
            taken.add(rid)
            st.setdefault('recipes', []).append({
                'id': rid,
                'name': (it.get('title') or '（無題）').strip(),
                'kind': it.get('kind') if it.get('kind') in ('image', 'video') else 'video',
                'intent': '旧「生成プロンプト」から移行',
                'body': body,
                'slug': '',
            })
    st['promptsMigratedAt'] = datetime.datetime.now().isoformat(timespec='seconds')
    return st


def _atelier_derive(data):
    """
    **採用状態は保存しない。show.json から導出する**（2026-07-30）。

    「この生成物を採用したか」の答えは show.json が既に持っている
    — `cues[].sourceUrl` がその出力ファイルを指していれば、それは採用されたということ。
    台帳に別の札（`verdict` の 3 択ボタン）を置いていたので、押し忘れた瞬間に食い違った。
    実際、本番に載っていた 2 枚は台帳では「未評価」で、台帳が持つ唯一の完成品はどの cue からも
    参照されていなかった。**押されないボタンは「無い」のではなく、嘘をつく分だけ有害**。

    突合キーは出力ファイルの URL。新しい id を作らないので、cue を消しても孤児が出ない。

    返すのは**表示用のコピー**。`atelier.json` に導出値は書かない（原則: 状態を二重に持たない）。
    """
    view = copy.deepcopy(data)
    cues = (_show or {}).get('cues') or []
    by_src = {}
    for c in cues:
        key = _norm_asset_url(c.get('sourceUrl'))
        if key:
            by_src.setdefault(key, []).append({'id': c.get('id'), 'name': c.get('name') or ''})

    used, kept = {}, {}
    for g in view.get('generations', []):
        g.pop('verdict', None)          # 旧・手動の採否札は読まない（残っていても無視する）
        hits = by_src.get(_norm_asset_url(g.get('outputUrl')), [])
        g['usedByCues'] = hits          # 導出（レスポンス限り）
        rid = g.get('recipeId') or ''
        if rid:
            used[rid] = used.get(rid, 0) + 1
            if hits:
                kept[rid] = kept.get(rid, 0) + 1
    for r in view.get('recipes', []):
        rid = r.get('id')
        r['usedCount'] = used.get(rid, 0)
        r['keptCount'] = kept.get(rid, 0)

    # 台帳に記録の無い生成物（先に作ってから記録する順序・過去分）も、cue が使っているなら見せる。
    #   testassets/ は動作確認用のダミー（「演出 A」の文字だけ）なので除く — 作り方を記録する対象ではなく、
    #   混ぜると本物の取りこぼしがノイズに埋もれる。
    known = {_norm_asset_url(g.get('outputUrl')) for g in view.get('generations', []) if g.get('outputUrl')}
    view['unlogged'] = [
        {'sourceUrl': src, 'cues': hits}
        for src, hits in sorted(by_src.items())
        if src and src not in known and not src.startswith('/testassets/')
    ]
    return view


STATUS_MARK = {'draft': '📋 送信待ち', 'pending': '⏳ 生成中', 'done': '🎬 取り込み済み', 'failed': '⚠ 失敗'}


def _atelier_index_md(data):
    """カメラ別の素材索引を Markdown で書き出す（自動生成・手編集しない）。"""
    gens = data.get('generations', [])
    recipes = {r.get('id'): r for r in data.get('recipes', [])}
    stamp = datetime.datetime.now().strftime('%Y-%m-%d %H:%M')
    out = ['# 素材インデックス（自動生成 — 手で編集しない）', '',
           f'更新 {stamp} / rev {data.get("rev", 0)} / 生成 {len(gens)} 件 / レシピ {len(recipes)} 件',
           '', '生成の正は `atelier.json`。このファイルはそれを人が読める形に落としたもの。', '']

    if recipes:
        out += ['## レシピ（場所に依存しない演出テンプレ）', '']
        for r in data.get('recipes', []):
            used, kept = r.get('usedCount', 0), r.get('keptCount', 0)
            rate = f'{kept}/{used} 採用' if used else '未使用'
            out.append(f'### {r.get("name") or "(無題)"} `{r.get("id")}` — {rate}')
            if r.get('intent'):
                out.append(f'狙い: {r["intent"]}')
            slots = r.get('slots') or []
            if slots:
                out.append(f'スロット: {" / ".join(slots)}')
            out += ['', '```', (r.get('body') or '').strip(), '```', '']

    by_cam = {}
    for g in gens:
        by_cam.setdefault(g.get('cameraLabel') or '?', []).append(g)

    for label in sorted(by_cam.keys()):
        items = sorted(by_cam[label], key=lambda x: x.get('createdAt') or '', reverse=True)
        keeps = sum(1 for x in items if x.get('usedByCues'))
        out += ['', f'## カメラ {label}（{len(items)} 件 / 本番で使用 {keeps}）', '']
        for g in items:
            hits = g.get('usedByCues') or []
            mark = ('✅ 使用: ' + ', '.join(str(c.get('id')) for c in hits)) if hits else '― 未使用'
            head = f'{mark}  {STATUS_MARK.get(g.get("status"), "")}'
            out.append(f'### {head} `{g.get("id")}`')
            if g.get('outputUrl'):
                out.append(f'- 出力: `{g["outputUrl"]}`')
            else:
                out.append('- 出力: **まだ無い**（生成して取り込む）')
            out.append(f'- 入力フレーム: `{g.get("sourceFrame") or "(未選択)"}`')
            rn = g.get('recipeName') or '(レシピなし)'
            bind = g.get('bind') or {}
            bs = ' / '.join(f'{k}={v}' for k, v in bind.items() if str(v).strip())
            out.append(f'- レシピ: {rn}' + (f'（{bs}）' if bs else ''))
            p = g.get('params') or {}
            parts = [p.get('tool'), p.get('model'), p.get('aspect'),
                     f'{p["sec"]}s' if p.get('sec') else None]
            tool = ' / '.join(str(x) for x in parts if x)
            if tool:
                out.append(f'- 生成条件: {tool}' + (f' / seed {p["seed"]}' if p.get('seed') else ''))
            if g.get('note'):
                out.append(f'- メモ: {g["note"]}')
            if g.get('parentId'):
                out.append(f'- 派生元: `{g["parentId"]}`')
            out += ['', '```', (g.get('prompt') or '').strip(), '```', '']

    # 台帳に記録が無いのに本番で使われている素材。**これが出ていたら台帳が現実を映していない**。
    # 2026-07-30 の時点で実際に 2 件あり（工房は「素材なし」と表示していた）、それが台帳を
    # 導出方式へ変える決め手になった。ここに出しておけば次に同じ状態になったとき気づける。
    unlogged = data.get('unlogged') or []
    if unlogged:
        out += ['', '## ⚠ 台帳に無いまま本番で使われている素材', '',
                '（外部ツールで作って直接 cue にしたもの。工房の棚から「プロンプトを書き足す」で記録できる）', '']
        for u in unlogged:
            ids = ', '.join(str(c.get('id')) for c in (u.get('cues') or []))
            out.append(f'- `{u.get("sourceUrl")}` — {ids}')
        out.append('')

    if not gens:
        out += ['', '## まだ生成がありません', '',
                '卓の「🧪 素材」からカメラを選び、入力フレームとレシピを決めて 📋 でプロンプトをコピーする。', '']
    return '\n'.join(out) + '\n'


def _save_atelier(data):
    """
    atelier.json には**素のデータだけ**を書く（採用状態・採用率は導出値なので保存しない）。
    人間向けの `atelier-index.md` は導出を掛けた view から書く。
    """
    data['rev'] = int(data.get('rev', 0)) + 1
    for g in data.get('generations', []):
        g.pop('verdict', None)          # 旧・手動の採否札を保存し続けない
        g.pop('usedByCues', None)       # 導出値が混ざって保存されるのを防ぐ
    for r in data.get('recipes', []):
        r.pop('usedCount', None)
        r.pop('keptCount', None)
    tmp = ATELIER_FILE + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, ATELIER_FILE)
    try:
        with open(ATELIER_INDEX, 'w', encoding='utf-8', newline='\n') as f:
            f.write(_atelier_index_md(_atelier_derive(data)))
    except OSError:
        pass
    return data


def _atelier_slug(s, fallback='x'):
    s = re.sub(r'[^A-Za-z0-9]+', '', str(s or ''))[:16]
    return s or fallback


def _atelier_new_id(prefix, taken):
    """衝突しない id を作る。

    時刻文字列だけだと、定番レシピの一括投入のような**同一秒内の連続作成で id が重複**する。
    重複すると recipeById が常に先頭を返し、UI の選択と中身が食い違い、採用率も混ざる（実際に踏んだ）。
    既存 id 集合と突き合わせ、衝突したら連番を足して必ず一意にする。
    """
    base = prefix + datetime.datetime.now().strftime('%Y%m%d_%H%M%S_%f')[:-3]
    if base not in taken:
        return base
    for n in range(1, 1000):
        cand = f'{base}_{n}'
        if cand not in taken:
            return cand
    return base + '_x'


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
    """PC → カメラ /info へ実 HTTP GET。(ok, detail, meta) を返す。

    meta は /info の JSON（cameraId / uuid / show / deviceName）。JSON でない・
    /info 非対応（iPhone の IP Camera Lite 等）なら None。
    """
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
            return True, ('/info OK ' + str(nm)).strip(), j
        except Exception:
            return True, f'/info HTTP {getattr(r, "status", 200)}', None
    except Exception as e:
        return False, str(e)[:100], None


# ---- 接続先の同一性照合（stale IP で別スロットの端末を掴む事故の検出）-----------
# DHCP でリースが移る / 端末側で cameraId を付け替える と、slot の host が
# 「別の端末」を指したまま HTTP は 200 で MJPEG も流れる（＝ LIVE 判定は通る）。
# Quest 側は DiscoveryLogic.IsIdentityMismatch で受信中フィードを継続照合しているが、
# PC 卓には同等の防御が無く、別ゾーンに同じ映像が出ていても気づけなかった。
#   方針: 定期的に /info を引いて cameraId / show を slot と突き合わせ、**警告だけ**出す。
#         自動での host 書き換えは beacon（_auto_follow）に限定し、ここではやらない
#         （物理的な置き間違いを「直った」ように見せない / ID 入替時のフラッピング防止）。
#         映像も止めない（設営中に「何も見えない」方が困る）。
# キルスイッチ: FIXEDCAM_IDCHECK=0
IDCHECK_ENABLED = os.environ.get('FIXEDCAM_IDCHECK', '1') != '0'
IDCHECK_INTERVAL = 10.0
_ident_lock = threading.Lock()
_ident = {}   # camId -> {state, detail, metaId, metaShow, uuid, name, host, port, at}

# ---- 受信の鮮度（/cam プロキシが実際に upstream から読めているか）----
# ⚠ ブラウザの <img> の load / error は生存判定に使えない:
#   ・upstream 断（スマホの熱落ち・Wi-Fi 切断）はこのプロキシが握り潰して正常終了するので error が飛ばない
#   ・multipart の各フレームで load が発火するかはブラウザ実装依存（実測で発火しない環境があった）
#   その結果、旧実装は「最後のフレームを表示したまま ● LIVE / ✅ 全台 LIVE 受信中」と言い続けていた。
#   プロキシは実際に upstream からバイトを読んでいる唯一の場所なので、ここを鮮度の一次情報にする。
_live_lock = threading.Lock()
_live = {}   # "host:port" -> {'at': monotonic, 'bytes': int, 'since': monotonic, 'clients': int}


def _live_key(host, port):
    return f'{host}:{port}'


def _live_open(key):
    now = time.monotonic()
    with _live_lock:
        e = _live.setdefault(key, {'at': 0.0, 'bytes': 0, 'since': now, 'clients': 0})
        e['clients'] += 1
        e['since'] = now


def _live_touch(key, n):
    now = time.monotonic()
    with _live_lock:
        e = _live.get(key)
        if e is not None:
            e['at'] = now
            e['bytes'] += n


def _live_close(key):
    with _live_lock:
        e = _live.get(key)
        if e is not None:
            e['clients'] = max(0, e['clients'] - 1)


def _live_snapshot():
    """{"host:port": {ageMs, bytes, clients}}。ageMs は最後にバイトが流れてからの経過。
    一度もバイトが来ていない接続は ageMs=None（＝未受信。停止とは区別する）。"""
    now = time.monotonic()
    with _live_lock:
        # 誰も見ておらず 5 分以上動きの無い配信元は落とす（現場で host を変えるたびに
        # 古い "host:port" が積み残るのを防ぐ。表示にも出したくない）。
        for k in [k for k, v in _live.items()
                  if v['clients'] <= 0 and now - max(v['at'], v['since']) > 300]:
            del _live[k]
        return {
            k: {
                'ageMs': None if not v['at'] else int((now - v['at']) * 1000),
                'openMs': int((now - v['since']) * 1000),
                'bytes': v['bytes'],
                'clients': v['clients'],
            }
            for k, v in _live.items()
        }


def _ident_check_once():
    with _show_cond:
        cams = json.loads(json.dumps(_show.get('cameras', [])))
    out = {}
    for cam in cams:
        cid = cam.get('id')
        host = (cam.get('host') or '').strip()
        port = int(cam.get('port') or 8080)
        if not host:
            out[cid] = {'state': 'nohost', 'detail': 'host 未設定', 'host': '', 'port': port,
                        'at': time.time()}
            continue
        ok, detail, meta = _probe_info(host, port, cam.get('auth') or '', timeout=2.0)
        rec = {'host': host, 'port': port, 'at': time.time(),
               'metaId': (meta or {}).get('cameraId'), 'metaShow': (meta or {}).get('show'),
               'uuid': (meta or {}).get('uuid'), 'name': (meta or {}).get('deviceName')}
        if not ok:
            rec.update(state='unreachable', detail=detail)
        elif not meta or not rec['metaId']:
            # /info 非対応 or cameraId 未設定（iPhone・旧 streamer）。照合不能＝不一致にしない。
            rec.update(state='unverifiable', detail='cameraId を名乗らない端末（照合不可）')
        elif rec['metaId'] != cid:
            rec.update(state='mismatch',
                       detail=f'この host は {rec["metaId"]} の端末（{rec["name"] or ""}）')
        elif rec['metaShow'] and SHOW_TOKEN and rec['metaShow'] != SHOW_TOKEN:
            rec.update(state='mismatch',
                       detail=f'別のショー "{rec["metaShow"]}" の端末（隣ブース混線の疑い）')
        else:
            rec.update(state='ok', detail=f'{rec["name"] or ""} / cameraId={cid}')
        out[cid] = rec
    with _ident_lock:
        _ident.clear()
        _ident.update(out)


def _ident_loop():
    while True:
        try:
            _ident_check_once()
        except Exception:
            pass  # 照合は best-effort。失敗しても配信・保存は止めない
        time.sleep(IDCHECK_INTERVAL)


def _start_idcheck():
    if not IDCHECK_ENABLED:
        print('  接続先の同一性照合           : 無効（FIXEDCAM_IDCHECK=0）')
        return
    threading.Thread(target=_ident_loop, daemon=True).start()
    print(f'  接続先の同一性照合           : /info を {int(IDCHECK_INTERVAL)}s 毎に照合（警告のみ）')


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
        if path == '/atelier':
            with _atelier_lock:
                return self._json(_atelier_derive(_load_atelier()))
        if path == '/atelier/frames':
            return self._json(self._list_source_frames())
        if path == '/generate/status':
            return self._get_generate_status(parse_qs(urlparse(self.path).query))
        if path == '/audio/list':
            return self._json(self._list_audio())
        if path == '/eyejack/list':
            return self._get_eyejack()
        if path == '/reveal':
            q = parse_qs(urlparse(self.path).query)
            return self._reveal(q.get('name', [''])[0])
        if path == '/open-dir':
            q = parse_qs(urlparse(self.path).query)
            return self._open_dir(q.get('dir', ['recordings'])[0])
        if path == '/state':
            return self._get_state()
        if path == '/unity/devices':
            # 機ごとの heartbeat（スタッフが眺める用。タブレットはここを読まない・0187）。
            return self._json(_udev.device_rows(_unity_devices_snapshot(), time.time()))
        if path == '/unity/status':
            snap = _unity_status_snapshot()
            age = (time.time() - snap['at']) if snap.get('at') else None
            return self._json({'status': snap, 'ageSec': age,
                               'alive': age is not None and age < 6.0})
        if path == '/cam':
            return self._proxy_cam(parse_qs(urlparse(self.path).query))
        if path == '/caminfo':
            return self._get_cam_info(parse_qs(urlparse(self.path).query))
        if path == '/shoot/devices':
            q = parse_qs(urlparse(self.path).query)
            return self._json(self._shoot_devices((q.get('host', [''])[0] or '').strip()))
        if path == '/shoot/takes':
            return self._json(self._shoot_takes(parse_qs(urlparse(self.path).query)))
        if path == '/shoot/manifest':
            return self._json({'ok': True, 'items': _shoot_manifest()})
        if path == '/shoot/plan':
            # 配信スマホの撮影パネルが読む「今日撮るもの」。cam= は端末の cameraId。
            pq = parse_qs(urlparse(self.path).query)
            return self._json(self._shoot_plan((pq.get('cam', [''])[0] or '').strip()))
        if path == '/cam/liveness':
            # 卓が 2 秒ごとに読む「各配信元から実際にバイトが来ているか」。
            return self._json({'ok': True, 'cams': _live_snapshot()})
        if path == '/scenarios/list':
            return self._json({'items': self._list_scenarios()})
        if path == '/dwell/stats':
            return self._json(_dwell_payload())
        if path == '/discovery':
            return self._get_discovery()
        if path == '/diag':
            return self._get_diag()
        if path == '/onsite/check':
            # 直前の点検結果（走らせ直さない）。当日パネルを開いた瞬間に出す用。
            try:
                with open(os.path.join(REPO_ROOT, 'logs', 'onsite', 'check-latest.json'),
                          'r', encoding='utf-8') as f:
                    return self._json({'ok': True, **json.load(f)})
            except Exception:
                return self._json({'ok': False, 'error': 'まだ 1 度も点検していません'}, 404)
        return super().do_GET()

    # 🕹 記録済みシナリオ（scenarios/*.json）の一覧。本体は静的配信（/scenarios/<name>.json）で読む。
    def _list_scenarios(self):
        items = []
        if not os.path.isdir(SCENARIOS):
            return items
        for n in os.listdir(SCENARIOS):
            if not n.endswith('.json'):
                continue
            fp = os.path.join(SCENARIOS, n)
            if not os.path.isfile(fp):
                continue
            items.append({'name': n[:-5], 'file': n, 'url': '/scenarios/' + n,
                          'mtime': os.stat(fp).st_mtime})
        items.sort(key=lambda x: x['mtime'], reverse=True)
        return items

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
        with _ident_lock:
            identity = json.loads(json.dumps(_ident))
        return self._json({'devices': devices, 'conflicts': conflicts,
                           'autoFollow': auto_follow,
                           'discoveryEnabled': bool(_show.get('control', {}).get('discoveryEnabled', True)),
                           'lastFollow': _last_follow,
                           'enabled': DISCOVERY_ENABLED, 'showToken': SHOW_TOKEN,
                           'identity': identity, 'idCheckEnabled': IDCHECK_ENABLED})

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
                ok, detail, meta = _probe_info(host, port, auth)
                row['http'] = ok
                row['httpDetail'] = detail
                mid = (meta or {}).get('cameraId')
                if mid and mid != cid:
                    row['httpDetail'] = f'{detail} ⚠ 名乗った ID={mid}'
            else:
                row['http'] = None
                row['httpDetail'] = 'host 未設定'
            e = disc_by_id.get(cid)
            row['beacon'] = bool(e) and (now - e['lastSeen'] < 12.0)
            row['beaconAgeSec'] = round(now - e['lastSeen'], 1) if e else None
            row['beaconIp'] = e['ip'] if e else None
            results.append(row)
        snap = _unity_status_snapshot()
        age = (now - snap['at']) if snap.get('at') else None
        quest = {'alive': age is not None and age < 6.0,
                 'ageSec': round(age, 1) if age is not None else None,
                 'activeCamera': snap.get('activeCamera'),
                 'cameraCount': snap.get('cameraCount')}
        return self._json({'cameras': results, 'quest': quest,
                           'discoveryEnabled': DISCOVERY_ENABLED})

    # 端末が配る /info を卓のブラウザへ中継する。位置合わせ面が「端末の傾き」を読むのに使う。
    #
    # ブラウザから端末を直接引かない理由: 端末ごとに CORS も到達性も違う（iPhone の
    # IP Camera Lite は /info 自体が無く 404 を返す）。失敗したときブラウザ側では
    # 「ネットワークが届かない」と「その端末は /info を持たない」を区別できない。
    # PC → カメラの HTTP は /diag が既に同じ経路で引いていて実績がある。
    #
    # ⚠ タイムアウトは短く。位置合わせ面が数秒おきに叩くので、届かない端末で待たせない。
    # ================= 当日の素材撮り（2 周目 B・C の接近） =================
    #
    # 卓 → 配信端末（streamer v0.10.0 の /record/*）の中継と、回収・検分・採用。
    # 判定は shoot-model.js（ブラウザ側・node テストあり）が持ち、ここは I/O だけ。

    def _shoot_cams(self):
        """show.json の cameras のうち host が入っているもの（撮影に使える端末）。"""
        out = []
        with _show_cond:
            cams = list(_show.get('cameras') or [])
        for i, c in enumerate(cams):
            host = (c.get('host') or '').strip()
            if not host:
                continue
            out.append({'index': i, 'id': c.get('id') or '', 'host': host,
                        'port': int(c.get('port') or 8080)})
        return out

    def _shoot_devices(self, only_host=''):
        """
        各端末の録画状態。**録りっぱなしの検出**にも使う（本番前チェック ②）。

        ⚠ `only_host` で 1 台に絞れる。録画中のポーリングで全台（3 台 × 2 リクエスト）を
          叩くと 1 巡に数秒かかり、**カウントダウンや自動停止の瞬間を見逃す**（実測で
          2 秒のカウントダウンが 1 度も観測できなかった）。
        """
        items = []
        for c in self._shoot_cams():
            if only_host and c['host'] != only_host:
                continue
            ok, st = _shoot_get(c['host'], c['port'], '/record/status', timeout=3.0)
            ok2, hl = _shoot_get(c['host'], c['port'], '/health', timeout=3.0)
            items.append({
                **c,
                'reachable': bool(ok),
                'recording': bool(ok and isinstance(st, dict) and st.get('recording')),
                'counting': bool(ok and isinstance(st, dict) and st.get('counting')),
                'countdownLeft': (st or {}).get('countdownLeft', 0) if isinstance(st, dict) else 0,
                'shot': (st or {}).get('shot', '') if isinstance(st, dict) else '',
                'takeCount': (st or {}).get('takeCount', 0) if isinstance(st, dict) else 0,
                # v0.9.0 以前は /record/list を持たない。卓はこれで新旧を切り分ける。
                'canList': bool(ok and isinstance(st, dict) and 'takeCount' in st),
                'throttleStage': (hl or {}).get('throttleStage', 0) if isinstance(hl, dict) else 0,
                'thermalStatus': (hl or {}).get('thermalStatus', 0) if isinstance(hl, dict) else 0,
                'status': st if isinstance(st, dict) else {},
            })
        return {'ok': True, 'items': items}

    def _shoot_takes(self, q):
        """端末に残っているテイクと、卓が回収済みのテイクを合わせて返す。"""
        host = (q.get('host', [''])[0] or '').strip()
        port = int((q.get('port', ['8080'])[0] or '8080'))
        device = []
        if host:
            ok, lst = _shoot_get(host, port, '/record/list', timeout=5.0)
            if ok and isinstance(lst, dict):
                device = lst.get('items') or []
        man = _shoot_manifest()
        # 回収済み（卓のディスクに在るもの）。素材一覧と同じ /recordings/ URL で返す。
        local = []
        for url, e in man.items():
            p = self._resolve_local_asset(url)
            if p and os.path.isfile(p):
                local.append({'url': url, **e})
        local.sort(key=lambda x: x.get('capturedAt') or '', reverse=True)
        return {'ok': True, 'device': device, 'local': local}

    def _shoot_plan(self, cam_id):
        """
        配信スマホの撮影パネルへ配る「今日撮るもの」。

        **指示文・尺・要求秒・採用状況を、卓が唯一の正として配る。** スマホ側に写しを
        持たせない — 現場で著作を変えたとき、手元の紙とスマホの表示が食い違う経路を作らない。

        `cam_id` はその端末の cameraId（A/B/C/D/?）。`dev:"cam"` のショット（そのカメラ自身で
        撮るもの）は当該端末にだけ「あなたの担当」と出す。手持ち（`dev:"pov"`）はどの端末でも撮れる。
        """
        shots = _shots_def()
        if not shots:
            return {'ok': False, 'detail': 'shots.json が読めない（卓の設置ミス）'}
        with _show_cond:
            show = json.loads(json.dumps(_show))
        segs = ((show.get('timeline') or {}).get('segments')) or []
        cues = {c.get('id'): c for c in (show.get('cues') or []) if c.get('id')}
        man = _shoot_manifest()

        # 回収済みテイクを cue ごとに束ねる（url → 台帳の entry）。
        by_shot = {}
        for url, e in man.items():
            key = e.get('shot') or ''
            if not key:
                continue
            by_shot.setdefault(key, []).append({'url': url, **e})
        for lst in by_shot.values():
            lst.sort(key=lambda x: x.get('capturedAt') or '', reverse=True)

        items = []
        for sh in shots:
            cue_id = sh.get('cueId')
            cue = cues.get(cue_id)
            need = needed_head_sec(segs, cue_id)
            src = (cue or {}).get('sourceUrl') or ''
            takes = by_shot.get(cue_id, [])
            adopted = next((t for t in takes if t.get('url') == src), None)
            level, reason, fix = shot_status(cue, need, (adopted or {}).get('durSec'))
            dev = sh.get('dev') or 'pov'
            mine = True if dev != 'cam' else (cam_id == ((cue or {}).get('camera') or ''))
            items.append({
                'cueId': cue_id,
                'label': sh.get('label') or cue_id,
                'hint': sh.get('hint') or '',
                'recSec': sh.get('recSec') or 3,
                'countdownSec': (sh.get('countdownSec')
                                 if isinstance(sh.get('countdownSec'), int)
                                 else (3 if dev == 'pov' else 0)),
                'dev': dev,
                'forThisDevice': bool(mine),
                'needSec': need,
                'cuts': cut_count(segs, cue_id),
                'status': level, 'reason': reason, 'fix': fix,
                # スマホが「この端末のこのファイルはもう送った」を印すための対応表。
                'adoptedName': os.path.basename(src) if src else '',
                'collected': [t.get('name') or '' for t in takes],
            })
        return {'ok': True, 'desk': socket.gethostname(), 'rev': int(show.get('rev') or 0),
                'nowIso': datetime.datetime.now().isoformat(timespec='seconds'),
                'shots': items}

    def _shoot_collect(self, body, host, port):
        """
        **スマホから**「このテイクを卓へ」。回収 → 検分 → （`adopt` なら）採用まで 1 往復。

        既存の `_shoot_pull` / `_shoot_adopt` をそのまま使う（検分・一意名・台帳・頭送りの
        作法を 2 つに割らない）。返す `short` は「要求尺に足りているか」の答えで、
        **撮った本人がその場で撮り直しを決められる**ようにここまで返す。
        """
        name = str(body.get('name') or '')
        shot = str(body.get('shot') or '')
        adopt = bool(body.get('adopt'))
        payload, code = self._do_pull(name, shot, host, port)
        if not payload.get('ok'):
            return self._json(payload, code)

        entry = payload.get('entry') or {}
        url = payload.get('url') or ''
        # 要求尺（この素材の頭が何秒画に出るか）を添えて返す。**撮った本人が撮り直しを決められる**
        # ようにここまで返す — 卓の画面を見に行かないと分からない形にしない。
        with _show_cond:
            segs = (((_show.get('timeline') or {}).get('segments')) or [])[:]
        need = needed_head_sec(segs, shot)
        dur = entry.get('durSec')
        out = {'ok': True, 'url': url, 'entry': entry, 'need': need, 'adopted': False,
               'short': bool(need is not None and isinstance(dur, (int, float))
                             and dur > 0 and dur < need)}
        if adopt:
            a_payload, a_code = self._do_adopt(shot, url, body.get('inPointSec'))
            out['adopted'] = bool(a_payload.get('ok'))
            out['adoptNote'] = a_payload.get('note') or a_payload.get('detail') or ''
            if not a_payload.get('ok'):
                # 回収は済んでいる（素材は卓にある）。採用だけ失敗した、と分かる形で返す。
                out['ok'] = True
                out['detail'] = f"回収したが採用できなかった: {a_payload.get('detail') or ''}"
        return self._json(out)

    def _shoot_post(self, path):
        try:
            n = int(self.headers.get('Content-Length') or 0)
            body = json.loads(self.rfile.read(n).decode('utf-8')) if n else {}
        except Exception as e:
            return self._json({'ok': False, 'detail': f'body が読めない: {e}'}, 400)

        host = str(body.get('host') or '').strip()
        port = int(body.get('port') or 8080)

        if path == '/shoot/start':
            if not host:
                return self._json({'ok': False, 'detail': 'host が空です'}, 400)
            shot = str(body.get('shot') or '')
            qs = urlencode({'shot': shot, 'maxSec': int(body.get('maxSec') or 0),
                            'countdownSec': int(body.get('countdownSec') or 0)})
            ok, r = _shoot_get(host, port, f'/record/start?{qs}', timeout=6.0)
            return self._json({'ok': bool(ok), 'result': r})

        if path == '/shoot/stop':
            if not host:
                return self._json({'ok': False, 'detail': 'host が空です'}, 400)
            ok, r = _shoot_get(host, port, '/record/stop', timeout=15.0)
            return self._json({'ok': bool(ok), 'result': r})

        if path == '/shoot/pull':
            return self._shoot_pull(body, host, port)

        if path == '/shoot/collect':
            # **配信スマホから**呼ばれる口。回収 → 検分 → （任意で）採用までを 1 往復で終える。
            # ⚠ host は body ではなく **接続元** を既定にする。端末が自分の IP をどう見ているかは
            #   卓から届くアドレスと一致するとは限らない（テザリング・複数 NIC）。
            return self._shoot_collect(body, host or self.client_address[0], port)

        if path == '/shoot/adopt':
            return self._shoot_adopt(body)

        if path == '/shoot/delete':
            name = str(body.get('name') or '')
            if not host or not name:
                return self._json({'ok': False, 'detail': 'host / name が必要です'}, 400)
            ok, r = _shoot_get(host, port, f'/record/delete?{urlencode({"name": name})}', timeout=6.0)
            return self._json({'ok': bool(ok), 'result': r})

        return self._json({'ok': False, 'detail': 'unknown'}, 404)

    def _shoot_pull(self, body, host, port):
        payload, code = self._do_pull(str(body.get('name') or ''),
                                      str(body.get('shot') or ''), host, port)
        return self._json(payload, code)

    def _do_pull(self, name, shot, host, port):
        """
        端末から 1 本回収して recordings/ へ置き、検分して台帳へ載せる。返すのは (payload, code)。

        ⚠ 保存名は**毎回一意**（テイク番号 + 時刻）。同名で差し替えると Quest の
          ローカルキャッシュが古い版を再生し続ける（撮り直したのに変わらない）。

        ⚠ **`_json` を呼ばない**（辞書を返す）。卓の UI（`/shoot/pull`）とスマホ
          （`/shoot/collect` — 回収と採用を続けて行う）の 2 経路が同じ中核を使うため。
        """
        if not host or not name:
            return {'ok': False, 'detail': 'host / name が必要です'}, 400
        if not re.fullmatch(r'[A-Za-z0-9_.\-]{1,128}', name) or '..' in name:
            return {'ok': False, 'detail': 'name が不正です'}, 400

        os.makedirs(RECORDINGS, exist_ok=True)
        dst = os.path.join(RECORDINGS, name)
        # 同名が既にあれば連番を足す（端末を初期化して番号が戻っても上書きしない）。
        base, ext = os.path.splitext(name)
        k = 1
        while os.path.exists(dst):
            k += 1
            dst = os.path.join(RECORDINGS, f'{base}-{k}{ext}')
        try:
            url = f'http://{host}:{port}/record/file?{urlencode({"name": name})}'
            with urllib.request.urlopen(url, timeout=120) as r, open(dst, 'wb') as f:
                shutil.copyfileobj(r, f)
        except Exception as e:
            try:
                os.remove(dst)
            except OSError:
                pass
            return {'ok': False, 'detail': f'回収に失敗: {e}'}, 502

        meta = _probe_media(dst)
        # 撮影時の熱段（本番と画質が違うかの判定に使う）。
        okh, hl = _shoot_get(host, port, '/health', timeout=3.0)
        rel = '/recordings/' + os.path.basename(dst)
        entry = {
            'name': os.path.basename(dst), 'shot': shot,
            # ⚠ 卓の時計で刻む（端末の時計は現場でずれる）。
            'capturedAt': datetime.datetime.now().isoformat(timespec='seconds'),
            'host': host, 'bytes': os.path.getsize(dst),
            'throttleStage': (hl or {}).get('throttleStage', 0) if okh and isinstance(hl, dict) else None,
            **meta,
        }
        _shoot_manifest_put(rel, entry)
        return {'ok': True, 'url': rel, 'entry': entry}, 200

    def _shoot_adopt(self, body):
        payload, code = self._do_adopt(str(body.get('cueId') or ''),
                                       str(body.get('url') or ''), body.get('inPointSec'))
        return self._json(payload, code)

    def _do_adopt(self, cue_id, url, in_point_raw):
        """
        撮ったテイクを cue へ採用する（`cues[].sourceUrl` を書く）。返すのは (payload, code)。

        ⚠ **`POST /state` を使わない**。あれは cues 配列を丸ごと差し替えるので、
          卓の別タブが編集中の cue を消す。ここは 1 件だけ触る。
        """
        if not cue_id:
            return {'ok': False, 'detail': 'cueId が必要です'}, 400

        # url が空 = **採用の取り消し**（間違って採用したときの唯一の戻り道）。
        # 現場では普通に起きるので、卓の cue エディタへ回らせない。
        if not url:
            cleared = {'hit': False}

            def clear(show):
                for c in (show.get('cues') or []):
                    if c.get('id') == cue_id:
                        c['sourceUrl'] = ''
                        cleared['hit'] = True

            rev = _mutate_show(clear)
            if not cleared['hit']:
                return {'ok': False, 'detail': f'cue が無い: {cue_id}'}, 404
            return {'ok': True, 'rev': rev, 'cueId': cue_id, 'url': '',
                    'note': '採用を取り消した（素材は残っている）'}, 200

        src = self._resolve_local_asset(url)
        if not src:
            return {'ok': False, 'detail': f'素材が見つからない: {url}'}, 400

        # 頭を送るなら、切り出した**別ファイル**を採用する（元のテイクは残す）。
        # ⚠ カットの trimStartSec（偽ライブの 0 / 1.9 / 3.6 / 5.0）は著作の値なので触らない。
        #   撮影の都合をそこへ書くと、素材を差し替えたとき古い頭合わせが別素材に効く。
        try:
            in_point = float(in_point_raw or 0)
        except (TypeError, ValueError):
            in_point = 0.0
        note = ''
        if in_point > 0.01:
            cut, note = _trim_head(src, in_point)
            if cut:
                url = '/recordings/' + os.path.basename(cut)
                base = _shoot_manifest().get('/recordings/' + os.path.basename(src), {})
                _shoot_manifest_put(url, {
                    **base, 'name': os.path.basename(cut), 'trimmedFrom': os.path.basename(src),
                    'inPointSec': in_point, **_probe_media(cut),
                })
            elif note:
                return {'ok': False, 'detail': note}, 500

        found = {'hit': False}

        def apply(show):
            for c in (show.get('cues') or []):
                if c.get('id') == cue_id:
                    c['sourceUrl'] = url
                    found['hit'] = True

        rev = _mutate_show(apply)
        if not found['hit']:
            return {'ok': False, 'detail': f'cue が無い: {cue_id}'}, 404
        return {'ok': True, 'rev': rev, 'cueId': cue_id, 'url': url, 'note': note}, 200

    def _get_cam_info(self, q):
        host = (q.get('host', [''])[0] or '').strip()
        auth = (q.get('auth', [''])[0] or '').strip()
        if not host:
            return self._json({'ok': False, 'detail': 'host が空です'}, 400)
        try:
            port = int((q.get('port', ['8080'])[0] or '8080').strip())
        except ValueError:
            return self._json({'ok': False, 'detail': 'port が数値ではありません'}, 400)
        ok, detail, meta = _probe_info(host, port, auth, timeout=2.0)
        # ok は「JSON として読めた」まで。HTTP は通ったが /info を持たない端末は False。
        return self._json({'ok': bool(ok and meta), 'detail': detail, 'info': meta})

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
        key = _live_key(host, port)
        _live_open(key)
        try:
            self.send_response(200)
            ctype = upstream.headers.get('Content-Type', 'multipart/x-mixed-replace')
            self.send_header('Content-Type', ctype)
            self.end_headers()
            while True:
                # ⚠ read() ではなく read1(): read は「64KB 溜まるまで」ブロックするので、
                #   ビットレートが低い配信では中継そのものが遅れ、鮮度の分解能も 64KB 単位になる
                #   （実測: 161B/frame の配信では 2 秒経っても 1 バイトも返らなかった）。
                #   read1 は「いま来ている分」を即返すので、低遅延中継と正確な鮮度が同時に成立する。
                chunk = upstream.read1(64 * 1024)
                if not chunk:
                    break
                # 「いま upstream からバイトが来ている」ことを記録する。卓の ● LIVE はこれを読む
                # （<img> の load/error は当てにならない — 上の _live のコメント参照）。
                _live_touch(key, len(chunk))
                self.wfile.write(chunk)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass  # クライアント側がタブを閉じた等。正常系
        finally:
            _live_close(key)
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
            # 送信はロック外で行う（スリープした Quest 等の TCP backpressure が
            # 卓の全保存操作をブロックしないように、ロック内ではスナップだけ取る）。
            snapshot = json.loads(json.dumps(_show))
        return self._json(snapshot)

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
        # 保存先ホワイトリスト + 読み取り専用フォルダ（BGM 音源・テスト素材・旧素材の退避先）
        target = SAVE_DIRS.get(key) or OPEN_DIRS.get(key)
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
            # 実測滞在（dwell[]）は集計側へ渡し、status には残さない（毎回のスナップに混ぜない）。
            merged = _merge_dwell(body.pop('dwell', None))
            did = (body.get('deviceId') or '').strip()
            with _unity_status_lock:
                _unity_status.clear()
                _unity_status.update(body)
                if did:
                    _unity_devices[did] = dict(body)
            return self._json({'ok': True, 'dwellMerged': merged})
        if parsed.path in ('/shoot/start', '/shoot/stop', '/shoot/pull',
                           '/shoot/adopt', '/shoot/delete', '/shoot/collect'):
            return self._shoot_post(parsed.path)
        if parsed.path == '/dwell/reset':
            with _dwell_lock:
                _dwell_stats['items'] = {}
                _dwell_stats['updatedAt'] = time.time()
                try:
                    _save_dwell()
                except OSError:
                    pass
            return self._json({'ok': True})
        if parsed.path == '/generate':
            return self._post_generate()
        if parsed.path == '/scenarios/save':
            return self._save_scenario()
        if parsed.path == '/export-build':
            return self._export_build()

        # 参照している素材が実際にディスクにあるか。本番前チェックが使う。
        #   卓は「cue の id が定義されているか」しか見ておらず、**ファイルを消しても ✅ が出た**
        #   （気づけるのは 📦 エクスポートの時か、卓を使わない現場なら実機で映像が出ない瞬間）。
        #   masks / captures / recordings / testassets / audio を 1 つの口でまとめて解決する
        #   （種類ごとの一覧 API を増やすと、増えた種類を照合し忘れる）。
        if parsed.path == '/assets/check':
            body = self._read_json_body()
            urls = body.get('urls') if isinstance(body.get('urls'), list) else []
            missing, external = [], []
            for u in urls[:400]:
                u = str(u or '')
                if not u:
                    continue
                if not u.startswith('/'):
                    external.append(u)        # http(s):// や sa:// は卓からは確かめられない
                elif not self._resolve_local_asset(u):
                    missing.append(u)
            return self._json({'ok': True, 'missing': missing, 'external': external})
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
            path = os.path.join(dest, name)
            with open(path, 'wb') as f:
                f.write(data)
            note = ''
            if typ == 'video':
                # ⚠⚠ **Quest へ配るのは mp4(H.264)。** ブラウザの MediaRecorder は webm(VP9) しか
                #    吐かないが、Unity の VideoPlayer が Android で VP9 を再生できるかは端末依存で、
                #    **当日に「動画だけ出ない」で詰む**（既に動いている cue はすべて mp4）。
                #    保存の瞬間に変換して不確実性を消す。手数は増えない（押すだけ）。
                mp4, note = _transcode_to_mp4(path)
                if mp4:
                    name = os.path.basename(mp4)
            url = ('/recordings/' if dest is RECORDINGS else '/captures/') + name
            size = os.path.getsize(os.path.join(dest, name))
            return self._json({'ok': True, 'name': name, 'url': url, 'dir': to,
                               'type': typ, 'size': size, 'note': note})

        if parsed.path.startswith('/atelier'):
            return self._atelier_post(parsed)

        # 目の視界ジャックの当日写真を show.json へ焼く（canon/LEDGER.md 0099）。
        if parsed.path == '/eyejack/apply':
            return self._post_eyejack_apply()
        if parsed.path == '/eyejack/upload':
            return self._post_eyejack_upload()
        if parsed.path == '/eyejack/clear':
            return self._post_eyejack_clear()
        if parsed.path == '/onsite/check':
            return self._post_onsite_check()
        if parsed.path == '/onsite/fix':
            return self._post_onsite_fix()

        return self._json({'ok': False, 'error': 'unknown endpoint'}, 404)

    # ================= 当日パネル（onsite.html）=================
    #
    # **PC を触らずに当日を回すための口。** スマホのブラウザから同じ LAN で叩く。
    # ⚠ ここは 127.0.0.1 に絞らない（絞ると目的が消える）。代わりに
    #   **引数を一切受け取らない**形にしてある — 走らせるコマンドは固定で、
    #   外から文字列が渡る経路が無い（/generate の Codex 起動とはそこが違う）。

    @staticmethod
    def _split_multipart(body: bytes, boundary: bytes):
        """multipart/form-data を最小限だけ解く。返すのは [(filename, bytes)]。

        ⚠ `cgi` は 3.13 で消えるので使わない。ここが要るのは
        「ファイル名と中身」だけなので、境界で割って CRLFCRLF で頭を落とす。
        """
        out = []
        sep = b'--' + boundary
        for part in body.split(sep):
            if not part or part[:2] == b'--':
                continue
            head, _, data = part.partition(b'\r\n\r\n')
            if not data:
                continue
            m = re.search(rb'filename="([^"]*)"', head)
            if not m or not m.group(1):
                continue
            name = m.group(1).decode('utf-8', 'replace')
            out.append((os.path.basename(name), data.rstrip(b'\r\n')))
        return out

    def _post_eyejack_upload(self):
        """スマホから写真を直接置く。**取り込みまではやらない**（押した順を確定させてから）。

        ⚠ **並ぶ順はファイル名順**（`_post_eyejack_apply`）。撮った順を保つため、
          いま入っている枚数の続きから連番を打ち直す。
        """
        ctype = self.headers.get('Content-Type', '')
        m = re.search(r'boundary=([^;]+)', ctype)
        if 'multipart/form-data' not in ctype or not m:
            return self._json({'ok': False, 'error': 'multipart/form-data で送ってください'}, 400)
        length = int(self.headers.get('Content-Length', 0))
        if length <= 0 or length > 220 * 1024 * 1024:
            return self._json({'ok': False, 'error': f'大きさが扱えません ({length} bytes)'}, 400)
        body = self.rfile.read(length)
        parts = self._split_multipart(body, m.group(1).strip('"').encode())
        if not parts:
            return self._json({'ok': False, 'error': '写真が入っていません'}, 400)

        os.makedirs(EYEJACK, exist_ok=True)
        ok = [n for n in sorted(os.listdir(EYEJACK))
              if n.lower().endswith(('.jpg', '.jpeg', '.png', '.heic', '.webp'))]
        base = len(ok)
        saved = []
        for i, (name, data) in enumerate(parts, 1):
            ext = os.path.splitext(name)[1].lower() or '.jpg'
            if ext not in ('.jpg', '.jpeg', '.png', '.heic', '.webp'):
                continue
            fn = f'{base + i:02d}_{re.sub(r"[^A-Za-z0-9._-]", "_", name)}'
            with open(os.path.join(EYEJACK, fn), 'wb') as f:
                f.write(data)
            saved.append(fn)
        return self._json({'ok': True, 'saved': saved, 'total': base + len(saved)})

    def _post_eyejack_clear(self):
        """写真を全部消す（差し替え・取りやめ）。**norm/ も掃除する** — 残っていると
        次の取り込みで前の写真が混ざる。"""
        n = 0
        for d in (EYEJACK, EYEJACK_NORM):
            if not os.path.isdir(d):
                continue
            for name in os.listdir(d):
                fp = os.path.join(d, name)
                if os.path.isfile(fp) and name.lower().endswith(
                        ('.jpg', '.jpeg', '.png', '.heic', '.webp')):
                    os.remove(fp)
                    n += 1
        return self._json({'ok': True, 'removed': n})

    def _post_onsite_check(self):
        """`tools/onsite.py check` を走らせて、結果の JSON をそのまま返す。

        ⚠ 引数は受け取らない（固定コマンド）。1 回 30〜60 秒かかるので、
          呼ぶ側は待つか、直前の結果（`logs/onsite/check-latest.json`）を読む。
        """
        script = os.path.join(REPO_ROOT, 'tools', 'onsite.py')
        latest = os.path.join(REPO_ROOT, 'logs', 'onsite', 'check-latest.json')
        try:
            subprocess.run(['py', '-3.11', script, 'check'], cwd=REPO_ROOT,
                           capture_output=True, timeout=240)
        except Exception as e:
            return self._json({'ok': False, 'error': str(e)}, 500)
        try:
            with open(latest, 'r', encoding='utf-8') as f:
                return self._json({'ok': True, **json.load(f)})
        except Exception as e:
            return self._json({'ok': False, 'error': f'結果を読めません: {e}'}, 500)

    # 決まった復旧だけを名前で呼べるようにする。**名前は白名簿**（外から文字列が渡らない）。
    _ONSITE_FIXES = {
        'cameras': '配信端末を起こし直す',
        'panel': 'Quest の設定パネルを閉じる',
    }

    def _post_onsite_fix(self):
        name = (self._read_json_body().get('name') or '').strip()
        if name not in self._ONSITE_FIXES:
            return self._json({'ok': False, 'error': '知らない復旧名です'}, 400)
        script = os.path.join(REPO_ROOT, 'tools', 'onsite.py')
        try:
            p = subprocess.run(['py', '-3.11', script, 'fix', name], cwd=REPO_ROOT,
                               capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=180)
        except Exception as e:
            return self._json({'ok': False, 'error': str(e)}, 500)
        return self._json({'ok': True, 'what': self._ONSITE_FIXES[name],
                           'log': (p.stdout or '').strip()[-1500:]})

    # 🕹 記録した歩き（scenario JSON）を scenarios/<name>.json へ保存する。
    #   show.json には一切触らない（検証入力とショー設定を混ぜない）。
    #   名前は英数 / 日本語可・パス区切りと拡張子は弾く（パストラバーサル防止）。
    def _save_scenario(self):
        body = self._read_json_body()
        name = (body.get('name') or '').strip()
        scenario = body.get('scenario')
        if not name or name != os.path.basename(name) or name.startswith('.'):
            return self._json({'ok': False, 'error': 'bad name'}, 400)
        if re.search(r'[\\/:*?"<>|]', name):
            return self._json({'ok': False, 'error': 'bad name'}, 400)
        if not isinstance(scenario, dict) or not isinstance(scenario.get('samples'), list):
            return self._json({'ok': False, 'error': 'bad scenario'}, 400)
        os.makedirs(SCENARIOS, exist_ok=True)
        fp = os.path.join(SCENARIOS, name + '.json')
        with open(fp, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(scenario, f, ensure_ascii=False, indent=2)
            f.write('\n')
        return self._json({'ok': True, 'name': name, 'url': '/scenarios/' + name + '.json',
                           'path': os.path.relpath(fp, ROOT).replace('\\', '/')})

    # show.json の部分更新。トップレベルの許可キーのみ shallow に置換する。
    _STATE_KEYS = ('cameras', 'cues', 'post', 'control', 'layout', 'schedule', 'timeline',
                   'bgmTracks', 'bgm', 'actors', 'record', 'run', 'lenses', 'feel', 'eyejack')

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
            elif typ == 'abortTake':
                # タイムラインが自動発火した演出（Take）を中止させる。
                # ⚠ stopCue では止まらない: 自動発火の演出は activeCue を使わず空のまま走るので、
                #    「空を空にする」書き換えは Unity から見て状態変化ゼロ＝ no-op になる
                #    （ShowControlClient は control の差分でしか動けない long-poll モデル）。
                #    そこで runEpoch と同じ「世代カウンタの増分」で確実に伝える（2026-07-28）。
                ctrl['takeAbortEpoch'] = int(ctrl.get('takeAbortEpoch') or 0) + 1
            elif typ == 'setCameraOverride':
                ctrl['cameraOverride'] = body.get('camera')  # None = ゾーン自律へ戻す
            elif typ == 'setPost':
                show.setdefault('post', {}).update(body.get('post') or {})
            elif typ == 'setAutoFollow':
                ctrl['autoFollow'] = bool(body.get('on'))
            elif typ == 'bindSlot':
                # 素材スロット（slot://name）の束縛。**timeline は触らない**のが要点 —
                # timeline を保存し直すと発火済み（once）の演出が再武装されるため、
                # 「入口で撮って生成した人形動画をラン中に差し込む」は必ずこの経路で行う。
                name = (body.get('name') or '').strip()
                if not name:
                    raise ValueError('bindSlot: name が空')
                slots = [x for x in (ctrl.get('slots') or []) if x.get('name') != name]
                url = (body.get('url') or '').strip()
                if url:
                    slots.append({'name': name, 'url': url})
                ctrl['slots'] = slots
            elif typ == 'glitch':
                # 「映像の乱れ」を 1 回走らせる（企画書 2.3 の「体験者の注意・移動の誘導」）。
                # abortTake と同じ世代カウンタ方式。強さ・秒も一緒に書いてから増分する。
                lv = body.get('level')
                sec = body.get('sec')
                if lv is not None: ctrl['glitchLevel'] = max(0.0, min(1.0, float(lv)))
                if sec is not None: ctrl['glitchSec'] = max(0.05, min(5.0, float(sec)))
                ctrl['glitchEpoch'] = int(ctrl.get('glitchEpoch') or 0) + 1
            elif typ == 'advanceIntro':
                # 導入を終えて本編へ（企画書「固定視点による移動に慣れた後、追跡体験を開始する」）。
                ctrl['introAdvanceEpoch'] = int(ctrl.get('introAdvanceEpoch') or 0) + 1
            elif typ == 'endRun':
                # 体験を終える（暗転）。走行中の演出は待たない。
                ctrl['runEndEpoch'] = int(ctrl.get('runEndEpoch') or 0) + 1
            elif typ == 'setDiscoveryEnabled':
                # Quest 内の発見プロトコル（fixedcam-discovery/1）のキルスイッチ。
                # autoFollow は卓側の host 書き換えを止めるだけで、Quest 内の張替は止まらない。
                # 現地で発見が暴走したとき静的 IP 運用へ縮退させる唯一の手段（2026-07-26 監査 MED）。
                ctrl['discoveryEnabled'] = bool(body.get('on'))
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
        return _export.resolve_local_asset(url, LOCAL_URL_DIRS)

    # 現 show.json + 参照アセットを Assets/StreamingAssets/show/ へ焼き込む。
    # 中身は export_build.py（卓の 📦 と `tools/export-show-build.py` が同じ関数を通る）。
    def _export_build(self):
        with _show_cond:  # _mutate_show / _auto_follow と同時に走ると dumps が iteration 中変更で落ちる
            show = json.loads(json.dumps(_show))  # deep copy（現物 _show は不変）
        return self._json(_export.export_build(show, REPO_ROOT, LOCAL_URL_DIRS))

    # 焼き込みの走査ヘルパ（実体は export_build.py）。卓の本番前チェックと
    # test_export_refs.py が Handler 越しに呼ぶので、委譲だけ残してある。
    @staticmethod
    def _step_asset_slots(show):
        return _export.step_asset_slots(show)

    @staticmethod
    def _referenced_cue_ids(show):
        return _export.referenced_cue_ids(show)

    # BGM 音源一覧（audio/ に置いたファイル。ここから bgmTracks を作る）。
    def _list_audio(self):
        exts = ('.mp3', '.ogg', '.wav', '.m4a', '.aac')
        items = []
        for n in sorted(os.listdir(AUDIO)):
            fp = os.path.join(AUDIO, n)
            if not os.path.isfile(fp) or not n.lower().endswith(exts):
                continue
            items.append({'name': n, 'url': '/audio/' + n, 'size': os.path.getsize(fp),
                          'mtime': os.path.getmtime(fp)})
        return items

    def _list_captures(self):
        """演出のカット素材に使えるファイル一覧。

        **captures/ と recordings/ の両方**を返す（📷 いま撮る / ⏺ 録画の保存先は recordings/ なので、
        captures/ だけ見ていると「撮ったのに素材の一覧に出ない」= 導線が切れる。2026-07-26 監査 HIGH）。
        同名衝突はディレクトリ違いで別物として並ぶ（url が違うので実害なし）。

        testassets/（動作確認用のダミー素材）も返すが `kind='test'` を付け、UI は実素材の後ろへ
        まとめて並べる。archive/（旧環境の素材）は**返さない** — もう使えないものを選ばせないため。
        """
        items = []
        for base, prefix in ((CAPTURES, '/captures/'), (RECORDINGS, '/recordings/'),
                             (TESTASSETS, '/testassets/')):
            if not os.path.isdir(base):
                continue
            for n in os.listdir(base):
                fp = os.path.join(base, n)
                if not os.path.isfile(fp):
                    continue
                ext = n.rsplit('.', 1)[-1].lower() if '.' in n else ''
                if ext not in ('webm', 'mp4', 'mov', 'jpg', 'jpeg', 'png'):
                    continue
                typ = 'video' if ext in ('webm', 'mp4', 'mov') else 'image'
                st = os.stat(fp)
                items.append({'name': n, 'url': prefix + n, 'dir': prefix.strip('/'),
                              'kind': 'test' if base is TESTASSETS else 'real',
                              'type': typ, 'size': st.st_size, 'mtime': st.st_mtime})
        # 実素材が新しい順 → テスト素材が名前順（テストは常に末尾でよい）
        items.sort(key=lambda x: (x['kind'] == 'test',
                                  x['name'] if x['kind'] == 'test' else '',
                                  -x['mtime']))
        return items

    # ---- 目の視界ジャックの当日写真（canon/LEDGER.md 0099）----------------------

    def _sync_eyejack(self):
        """`eyejack/` に置かれた写真を正規化して `eyejack/norm/` へ揃え、一覧を返す。

        当日の手順を「フォルダへ写真を放り込む」1 つに畳むための層。
        **正規化はここ（PC）で完結させる** — 端末でやると失敗が実機でしか出ず、当日直せない。

        やること 4 つ:
          ・EXIF の回転を**画素へ焼き込む**（Unity は EXIF を読まないので、やらないと縦写真が横を向く）
          ・長辺 1280 へ縮小（全視界に出すので、これ以上は見た目が変わらないのに 50MB 級の確保が走る）
          ・**暗順応した視界へ出すので減光する**（そのままだと眩しい。LEDGER 0015「不快にはならないように」）
          ・JPEG q85 で書き直す

        元ファイルは触らない（撮り直しの原本なので）。元より新しい正規化物があれば作り直さない。
        """
        items, errors = [], []
        if not os.path.isdir(EYEJACK):
            return {'items': items, 'errors': errors, 'dir': EYEJACK}
        try:
            from PIL import Image, ImageEnhance, ImageOps
        except ImportError:
            return {'items': items, 'dir': EYEJACK,
                    'errors': ['Pillow が入っていないので写真を正規化できません'
                               '（py -3.11 -m pip install pillow）']}

        for n in sorted(os.listdir(EYEJACK), key=str.lower):
            src = os.path.join(EYEJACK, n)
            if not os.path.isfile(src):
                continue
            ext = n.rsplit('.', 1)[-1].lower() if '.' in n else ''
            if ext not in ('jpg', 'jpeg', 'png', 'webp', 'bmp'):
                continue
            stem = os.path.splitext(n)[0]
            dest = os.path.join(EYEJACK_NORM, stem + '.jpg')
            try:
                if (not os.path.exists(dest)
                        or os.path.getmtime(dest) < os.path.getmtime(src)):
                    with Image.open(src) as im:
                        im = ImageOps.exif_transpose(im).convert('RGB')
                        im.thumbnail((EYEJACK_LONG_EDGE, EYEJACK_LONG_EDGE), Image.LANCZOS)
                        im = ImageEnhance.Brightness(im).enhance(EYEJACK_BRIGHTNESS)
                        im.save(dest, 'JPEG', quality=85, optimize=True)
                st = os.stat(dest)
                with Image.open(dest) as im2:
                    wpx, hpx = im2.size
                items.append({'name': stem + '.jpg', 'url': '/eyejack/norm/' + quote(stem + '.jpg'),
                              'src': n, 'w': wpx, 'h': hpx,
                              'size': st.st_size, 'mtime': st.st_mtime})
            except Exception as e:      # 1 枚の失敗で全部を落とさない（当日、他の写真は使える）
                errors.append(f'{n}: {e}')

        # 元が消えた写真の正規化物を掃除する（消したのに配り続けない）。
        keep = {it['name'] for it in items}
        for n in os.listdir(EYEJACK_NORM):
            if n not in keep and os.path.isfile(os.path.join(EYEJACK_NORM, n)):
                try:
                    os.remove(os.path.join(EYEJACK_NORM, n))
                except OSError:
                    pass
        return {'items': items, 'errors': errors, 'dir': EYEJACK}

    def _get_eyejack(self):
        """写真を正規化してから一覧を返す（卓の「👁 目の写真」パネルが 1 回だけ叩く）。"""
        data = self._sync_eyejack()
        with _show_cond:
            data['applied'] = list((_show.get('eyejack') or {}).get('photos') or [])
        return self._json(data)

    def _post_eyejack_apply(self):
        """いま `eyejack/norm/` にある写真を show.json の `eyejack.photos[]` へ焼く。

        ⚠ **URL の並びはファイル名順**（決定的 — 同じ版なら同じ順で出る）。
        """
        data = self._sync_eyejack()
        urls = [it['url'] for it in data['items']]

        def apply(show):
            show['eyejack'] = {'photos': urls}

        _mutate_show(apply)
        return self._json({'ok': True, 'count': len(urls), 'photos': urls,
                           'errors': data['errors']})

    # ---- 素材工房 -------------------------------------------------------------

    def _list_source_frames(self):
        """i2v の 1 枚目に使える静止画。recordings/ の camX_*.jpg をカメラ別に束ねる。"""
        by_cam = {}
        for n in os.listdir(RECORDINGS):
            fp = os.path.join(RECORDINGS, n)
            if not os.path.isfile(fp):
                continue
            ext = n.rsplit('.', 1)[-1].lower() if '.' in n else ''
            if ext not in ('jpg', 'jpeg', 'png'):
                continue
            m = re.match(r'cam([A-Za-z0-9]+?)(?:_quest)?_\d', n)
            label = m.group(1) if m else '?'
            st = os.stat(fp)
            by_cam.setdefault(label, []).append(
                {'name': n, 'url': '/recordings/' + n, 'mtime': st.st_mtime, 'size': st.st_size})
        for v in by_cam.values():
            v.sort(key=lambda x: x['mtime'], reverse=True)
        return by_cam

    # ---- 🪄 画像生成（Codex CLI）------------------------------------------------
    def _post_generate(self):
        # 卓は LAN 全体へ出ているので、外部プロセスを起こす口だけはこの PC からに限る。
        if self.client_address[0] not in ('127.0.0.1', '::1'):
            return self._json({'ok': False, 'error': 'この操作は卓を動かしている PC からだけ実行できます'}, 403)
        if not os.path.exists(CODEX_RUNNER):
            return self._json({'ok': False, 'error': f'Codex のラッパが見つかりません: {CODEX_RUNNER}'}, 500)
        body = self._read_json_body()
        prompt = (body.get('prompt') or '').strip()
        seed_url = (body.get('seedUrl') or '').strip()
        cam_label = (body.get('cam') or 'X').strip()
        slug = (body.get('slug') or 'free').strip()
        if len(prompt) < 8:
            return self._json({'ok': False, 'error': '何を足すのかを書いてください'}, 400)
        seed_path = self._resolve_local_asset(seed_url)
        if not seed_path:
            return self._json({'ok': False, 'error': '種フレームが見つかりません（先に 📷 で 1 枚撮る）'}, 400)

        with _gen_lock:
            if _gen_running():
                return self._json({'ok': False, 'error': '生成はいま 1 件だけ走っています（終わってから）'}, 409)
            job_id = _atelier_new_id('j_', set(_gen_jobs))
            # 素性は**先に**記録する。ジョブ表はメモリなので、サーバが落ちても
            # 「何を頼んだか」は atelier.json に残る（後から手で attach できる）。
            gen_id = None
            with _atelier_lock:
                st = _load_atelier()
                gen_id = _atelier_new_id('g_', {g.get('id') for g in st['generations']})
                st['generations'].append({
                    'id': gen_id,
                    'createdAt': datetime.datetime.now().isoformat(timespec='seconds'),
                    'status': 'running', 'verdict': 'unrated',
                    'cameraLabel': cam_label, 'sourceFrame': seed_url,
                    'recipeSlug': slug, 'prompt': prompt,
                    'params': {'tool': 'codex', 'mode': 'image'},
                })
                _save_atelier(st)
            _gen_jobs[job_id] = {'status': 'running', 'url': '', 'error': '', 'log': '',
                                 'startedAt': time.time(), 'finishedAt': 0,
                                 'cam': cam_label, 'genId': gen_id}
            threading.Thread(target=_gen_run, name='gen-' + job_id, daemon=True,
                             args=(job_id, cam_label, seed_path, prompt, slug)).start()
        return self._json({'ok': True, 'id': job_id, 'genId': gen_id})

    def _get_generate_status(self, q):
        job_id = (q.get('id', [''])[0] or '').strip()
        job = _gen_jobs.get(job_id)
        if not job:
            return self._json({'ok': False, 'error': 'unknown job'}, 404)
        return self._json({
            'ok': True, 'status': job['status'], 'url': job['url'], 'error': job['error'],
            'log': job.get('log', '')[-400:],
            # トーンの往復が効いたか（素通しに黙って落ちるのを卓から見えるようにする）
            'tone': job.get('tone') or {},
            'elapsedSec': round((job['finishedAt'] or time.time()) - job['startedAt'], 1),
        })

    def _atelier_post(self, parsed):
        path = parsed.path

        # 生成物の取り込み: バイナリ本文を captures/ へ規約名で保存し、レコードへ結びつける。
        if path == '/atelier/attach':
            q = parse_qs(parsed.query)
            gid = (q.get('id', [''])[0] or '').strip()
            ext = (q.get('ext', ['mp4'])[0] or 'mp4').lower()
            if ext not in ('mp4', 'webm', 'mov', 'gif', 'jpg', 'png'):
                ext = 'mp4'
            length = int(self.headers.get('Content-Length', 0))
            data = self.rfile.read(length) if length else b''
            if not data:
                return self._json({'ok': False, 'error': 'empty body'}, 400)
            with _atelier_lock:
                st = _load_atelier()
                rec = next((g for g in st['generations'] if g.get('id') == gid), None)
                if rec is None:
                    return self._json({'ok': False, 'error': 'unknown generation id'}, 404)
                # ファイル名だけで由来が読めるようにする（gen_camB_standing_20260726_1432.mp4）。
                # recipeSlug はレシピの ASCII 短縮名。レシピ id は時刻由来で意味を持たないので使わない。
                stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
                name = (f'gen_cam{_atelier_slug(rec.get("cameraLabel"), "X")}'
                        f'_{_atelier_slug(rec.get("recipeSlug"), "free")}'
                        f'_{stamp}.{ext}')
                with open(os.path.join(CAPTURES, name), 'wb') as f:
                    f.write(data)
                rec['outputUrl'] = '/captures/' + name
                rec['status'] = 'done'
                rec['sizeBytes'] = len(data)
                _save_atelier(st)
                return self._json({'ok': True, 'url': rec['outputUrl'], 'state': _atelier_derive(st)})

        # 壊れた本文（UTF-8 でない / JSON でない）で例外を投げると、応答を返せず接続が切れて
        # クライアントには「サーバが死んだ」ようにしか見えない。400 を返して原因を伝える。
        try:
            body = self._read_json_body()
        except (UnicodeDecodeError, ValueError) as e:
            return self._json({'ok': False, 'error': f'body must be UTF-8 JSON: {e}'}, 400)

        if path == '/atelier/recipe':
            with _atelier_lock:
                st = _load_atelier()
                rid = (body.get('id') or '').strip()
                rec = next((r for r in st['recipes'] if r.get('id') == rid), None) if rid else None
                if rec is None:
                    rid = rid or _atelier_new_id('r_', {r.get('id') for r in st['recipes']})
                    rec = {'id': rid}
                    st['recipes'].append(rec)
                for k in ('name', 'kind', 'intent', 'body', 'slug'):
                    if k in body:
                        rec[k] = body[k]
                if 'slots' in body and isinstance(body['slots'], list):
                    rec['slots'] = [str(s) for s in body['slots']]
                rec.setdefault('kind', 'video')
                _save_atelier(st)
                return self._json({'ok': True, 'id': rid, 'state': _atelier_derive(st)})

        if path == '/atelier/recipe/delete':
            with _atelier_lock:
                st = _load_atelier()
                st['recipes'] = [r for r in st['recipes'] if r.get('id') != body.get('id')]
                _save_atelier(st)
                return self._json({'ok': True, 'state': _atelier_derive(st)})

        if path == '/atelier/gen':
            with _atelier_lock:
                st = _load_atelier()
                gid = (body.get('id') or '').strip()
                rec = next((g for g in st['generations'] if g.get('id') == gid), None) if gid else None
                if rec is None:
                    gid = gid or _atelier_new_id('g_', {g.get('id') for g in st['generations']})
                    rec = {'id': gid,
                           'createdAt': datetime.datetime.now().isoformat(timespec='seconds'),
                           'status': 'draft'}
                    st['generations'].append(rec)
                # `verdict` は受け取らない（採用は show.json から導出する・_atelier_derive 参照）。
                for k in ('camera', 'cameraLabel', 'sourceFrame', 'recipeId', 'recipeName', 'recipeSlug',
                          'prompt', 'status', 'note', 'parentId', 'outputUrl', 'costUsd'):
                    if k in body:
                        rec[k] = body[k]
                for k in ('bind', 'params'):
                    if isinstance(body.get(k), dict):
                        rec[k] = body[k]
                _save_atelier(st)
                return self._json({'ok': True, 'id': gid, 'state': _atelier_derive(st)})

        if path == '/atelier/gen/delete':
            with _atelier_lock:
                st = _load_atelier()
                st['generations'] = [g for g in st['generations'] if g.get('id') != body.get('id')]
                _save_atelier(st)
                return self._json({'ok': True, 'state': _atelier_derive(st)})

        return self._json({'ok': False, 'error': 'unknown atelier path'}, 404)

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
    _start_idcheck()
    # 素材索引は導出値（採用状態）を含むので、show.json 側が変わっただけでも古くなる。
    #   起動のたびに書き直す — 人間と次セッションのシュビーが読むファイルが嘘をつく状態を残さない。
    #   旧 prompts.json の取り込みも同じ機会に 1 回だけ行う。
    try:
        with _atelier_lock:
            st = _migrate_prompts_into_atelier(_load_atelier())
            _save_atelier(st)
    except Exception as e:
        print(f'  (素材索引の再生成に失敗: {e})')
    ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever()
