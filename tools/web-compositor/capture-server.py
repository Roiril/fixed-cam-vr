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
import datetime
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.request
import uuid as _uuidlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, quote, unquote

ROOT = os.path.dirname(os.path.abspath(__file__))
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
os.makedirs(CAPTURES, exist_ok=True)
os.makedirs(MASKS, exist_ok=True)
os.makedirs(RECORDINGS, exist_ok=True)
os.makedirs(AUDIO, exist_ok=True)
os.makedirs(TESTASSETS, exist_ok=True)

# /save?to= と /open-dir?dir= の保存先ホワイトリスト（パストラバーサル防止）
SAVE_DIRS = {'captures': CAPTURES, 'recordings': RECORDINGS}
# /open-dir?dir= だけで開いてよいフォルダ（保存はしない）
OPEN_DIRS = {'testassets': TESTASSETS, 'archive': ARCHIVE, 'audio': AUDIO, 'masks': MASKS}

# エクスポート時に「ローカル URL → 実ファイル」を解決するディレクトリ対応表。
LOCAL_URL_DIRS = {
    '/masks/': MASKS,
    '/captures/': CAPTURES,
    '/recordings/': RECORDINGS,
    '/static-inputs/': STATIC_INPUTS,
    '/audio/': AUDIO,
    '/testassets/': TESTASSETS,
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
        'post': {'exposure': -0.72, 'contrast': 1.28, 'saturation': 0.35, 'temperature': -0.18,
                 'tint': 0.16, 'lift': 0.065, 'vignette': 0.45, 'grain': 0.065, 'scanline': 0.20},
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
        'control': {'activeCue': None, 'cameraOverride': None, 'autoFollow': True, 'runEpoch': 0,
                    # Quest 内の発見プロトコルのキルスイッチ。欠落は ON 扱い（後方互換）。
                    'discoveryEnabled': True,
                    # 素材スロットの束縛（slot://name → 実 URL）。ラン中に差し替える素材はここ。
                    'slots': []},
        # 端末内録画（1 周目を録って 3 周目の演出で流す）。既定は無効。
        'record': {'enabled': False, 'laps': [1], 'maxSegmentSec': 60, 'maxTotalMB': 200, 'fpsCap': 15},
        # CG レイヤに立てる人形の定義（cameras[i].pose が著作済みのカメラでのみ出る）。
        'actors': [],
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
# _unity_status は heartbeat スレッドが書き、/unity/status と /diag が読む（ThreadingHTTPServer =
# リクエストごとに別スレッド）。clear()+update() の隙間で読むと KeyError / dict changed size で 500 になり、
# 卓が「サーバ断」を誤表示する（2026-07-26 監査 MED）。読み書きを必ずこのロックで囲む。
_unity_status_lock = threading.Lock()


def _unity_status_snapshot():
    with _unity_status_lock:
        return dict(_unity_status)


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


def _atelier_stats(data):
    """レシピごとの使用回数・採用数を毎回サーバで数え直す（クライアントに持たせない）。"""
    used, kept = {}, {}
    for g in data.get('generations', []):
        rid = g.get('recipeId') or ''
        if not rid:
            continue
        used[rid] = used.get(rid, 0) + 1
        if g.get('verdict') == 'keep':
            kept[rid] = kept.get(rid, 0) + 1
    for r in data.get('recipes', []):
        rid = r.get('id')
        r['usedCount'] = used.get(rid, 0)
        r['keptCount'] = kept.get(rid, 0)
    return data


VERDICT_MARK = {'keep': '✅ 採用', 'reject': '✕ 不採用', 'unrated': '― 未評価'}
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
        keeps = sum(1 for x in items if x.get('verdict') == 'keep')
        out += ['', f'## カメラ {label}（{len(items)} 件 / 採用 {keeps}）', '']
        for g in items:
            head = f'{VERDICT_MARK.get(g.get("verdict"), "― 未評価")}  {STATUS_MARK.get(g.get("status"), "")}'
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

    if not gens:
        out += ['', '## まだ生成がありません', '',
                '卓の「🧪 素材」からカメラを選び、入力フレームとレシピを決めて 📋 でプロンプトをコピーする。', '']
    return '\n'.join(out) + '\n'


def _save_atelier(data):
    data['rev'] = int(data.get('rev', 0)) + 1
    _atelier_stats(data)
    tmp = ATELIER_FILE + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, ATELIER_FILE)
    try:
        with open(ATELIER_INDEX, 'w', encoding='utf-8', newline='\n') as f:
            f.write(_atelier_index_md(data))
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
        if path == '/prompts':
            return self._json(_load_prompts())
        if path == '/atelier':
            with _atelier_lock:
                return self._json(_atelier_stats(_load_atelier()))
        if path == '/atelier/frames':
            return self._json(self._list_source_frames())
        if path == '/audio/list':
            return self._json(self._list_audio())
        if path == '/reveal':
            q = parse_qs(urlparse(self.path).query)
            return self._reveal(q.get('name', [''])[0])
        if path == '/open-dir':
            q = parse_qs(urlparse(self.path).query)
            return self._open_dir(q.get('dir', ['recordings'])[0])
        if path == '/state':
            return self._get_state()
        if path == '/unity/status':
            snap = _unity_status_snapshot()
            age = (time.time() - snap['at']) if snap.get('at') else None
            return self._json({'status': snap, 'ageSec': age,
                               'alive': age is not None and age < 6.0})
        if path == '/masks/list':
            return self._json(self._list_masks())
        if path == '/cam':
            return self._proxy_cam(parse_qs(urlparse(self.path).query))
        if path == '/scenarios/list':
            return self._json({'items': self._list_scenarios()})
        if path == '/dwell/stats':
            return self._json(_dwell_payload())
        if path == '/discovery':
            return self._get_discovery()
        if path == '/diag':
            return self._get_diag()
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
            # 送信はロック外で行う（スリープした Quest 等の TCP backpressure が
            # 卓の全保存操作をブロックしないように、ロック内ではスナップだけ取る）。
            snapshot = json.loads(json.dumps(_show))
        return self._json(snapshot)

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
            with _unity_status_lock:
                _unity_status.clear()
                _unity_status.update(body)
            return self._json({'ok': True, 'dwellMerged': merged})
        if parsed.path == '/dwell/reset':
            with _dwell_lock:
                _dwell_stats['items'] = {}
                _dwell_stats['updatedAt'] = time.time()
                try:
                    _save_dwell()
                except OSError:
                    pass
            return self._json({'ok': True})
        if parsed.path == '/scenarios/save':
            return self._save_scenario()
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

        if parsed.path.startswith('/atelier'):
            return self._atelier_post(parsed)

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
                   'bgmTracks', 'bgm', 'actors', 'record')

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
        if not url or not url.startswith('/'):
            return None
        clean = url.split('?', 1)[0]
        for prefix, base in LOCAL_URL_DIRS.items():
            if clean.startswith(prefix):
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
        with _show_cond:  # _mutate_show / _auto_follow と同時に走ると dumps が iteration 中変更で落ちる
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
        unresolved = []        # ローカル URL なのに実ファイルが無いもの（APK に入らない = 現地で無映像）

        def bake(url):
            fp = self._resolve_local_asset(url)
            if not fp:
                # ローカル URL（/captures/... 等）なのに解決できない＝実ファイルが無い。
                # 黙って素通しすると APK に素材が入らず、現地 PC 不在でそのカットだけ無映像になる
                # （卓には ✓ としか出ないので気づけない。2026-07-26 監査 HIGH）。
                if url.startswith('/') and url not in unresolved:
                    unresolved.append(url)
                return url  # 外部 URL / 空 / 解決不能はそのまま
            if fp in src_to_dest:
                return 'sa://assets/' + quote(src_to_dest[fp])
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
            return 'sa://assets/' + quote(name)

        for cue in show.get('cues', []):
            if cue.get('maskUrl'):
                cue['maskUrl'] = bake(cue['maskUrl'])
            if cue.get('sourceUrl'):
                cue['sourceUrl'] = bake(cue['sourceUrl'])

        # BGM 音源も焼き込む（cue と同じ経路。sa://assets/ 化して APK 単体で鳴らせるように）。
        for tr in show.get('bgmTracks', []):
            if tr.get('url'):
                tr['url'] = bake(tr['url'])

        # v3 演出（takes[].steps[].assetUrl）の素材も焼き込む。cue と違いカット自身が素材 URL を
        # 直接持つので、ここを歩かないと「現地 PC 不在の APK で演出の映像だけ出ない」事故になる。
        for step in self._step_asset_slots(show):
            step['assetUrl'] = bake(step['assetUrl'])

        # cue 参照走査: timeline / schedule / ライブ control が指す cueId が cues[] に実在するか検証。
        # 全 cue のアセットは上のループで焼き込み済み（cueId 参照は新規アセットを持たない）ため
        # 追加コピーは不要。ここでは dangling 参照を missingCues として返し UI で気づけるようにする。
        cue_ids = {c.get('id') for c in show.get('cues', [])}
        referenced = self._referenced_cue_ids(show)
        missing = sorted(r for r in referenced if r and r not in cue_ids)

        # BGM: 区間 / ラン既定が指すトラック id が bgmTracks に実在するか（dangling 検出）。
        track_ids = {t.get('id') for t in show.get('bgmTracks', [])}
        ref_tracks = set()
        root_bgm = show.get('bgm') or {}
        if root_bgm.get('action') == 'play' and root_bgm.get('trackId'):
            ref_tracks.add(root_bgm['trackId'])
        for seg in ((show.get('timeline') or {}).get('segments') or []):
            b = seg.get('bgm') or {}
            if seg.get('hasBgm') and b.get('action') == 'play' and b.get('trackId'):
                ref_tracks.add(b['trackId'])
        missing_tracks = sorted(t for t in ref_tracks if t not in track_ids)

        show_path = os.path.join(out_dir, 'show.json')
        # UTF-8 / LF 固定（Unity JsonUtility が読む契約）。
        with open(show_path, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(show, f, ensure_ascii=False, indent=2)
        total = sum(c['size'] for c in copied)
        # 焼き込んだカメラ接続先を返す（現地 DHCP の IP が verbatim に入るため、
        # 「いつ・どの IP で焼いたか」が APK の唯一の手がかりになる。UI が必ず表示する）。
        hosts = [{'id': c.get('id', '?'), 'host': c.get('host', ''),
                  'port': c.get('port', 8080), 'pinned': bool(c.get('pinned'))}
                 for c in show.get('cameras', [])]
        return self._json({'ok': True, 'outDir': out_dir, 'showJson': show_path,
                           'copied': copied, 'count': len(copied), 'totalBytes': total,
                           'referencedCues': len(referenced), 'missingCues': missing,
                           'missingTracks': missing_tracks, 'unresolvedAssets': unresolved,
                           'exportedAt': time.strftime('%Y-%m-%d %H:%M:%S'),
                           'showRev': show.get('rev', 0), 'hosts': hosts})

    # v3 演出のカットのうち assetUrl（事前映像 / 静止画）を持つものを列挙する。
    # export-build が sa:// 化のために書き換える対象そのもの（走査漏れをテストで固定できるよう分離）。
    @staticmethod
    def _step_asset_slots(show):
        slots = []
        for seg in ((show.get('timeline') or {}).get('segments') or []):
            for take in (seg.get('takes') or []):
                for step in (take.get('steps') or []):
                    if step.get('assetUrl'):
                        slots.append(step)
        return slots

    # timeline（v3: segments[].takes[].steps[].cueId / v2: segments[].cues[].cueId + insert.cueId）/
    # schedule.entries / control.activeCue が参照する cueId の集合。export-build の dangling 参照検出に使う。
    @staticmethod
    def _referenced_cue_ids(show):
        ids = set()
        for e in ((show.get('schedule') or {}).get('entries') or []):
            if e.get('cueId'):
                ids.add(e['cueId'])
        for seg in ((show.get('timeline') or {}).get('segments') or []):
            # v3: 演出のカットが参照する cue（マスク素材）
            for take in (seg.get('takes') or []):
                for step in (take.get('steps') or []):
                    if step.get('cueId'):
                        ids.add(step['cueId'])
            # v2: 区間 cue / インサート（読み取り互換の間は残す）
            for a in (seg.get('cues') or []):
                if a.get('cueId'):
                    ids.add(a['cueId'])
            ins = seg.get('insert') or {}
            if seg.get('hasInsert') and ins.get('cueId'):
                ids.add(ins['cueId'])
        ac = (show.get('control') or {}).get('activeCue')
        if ac:
            ids.add(ac)
        return ids

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
                return self._json({'ok': True, 'url': rec['outputUrl'], 'state': st})

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
                return self._json({'ok': True, 'id': rid, 'state': st})

        if path == '/atelier/recipe/delete':
            with _atelier_lock:
                st = _load_atelier()
                st['recipes'] = [r for r in st['recipes'] if r.get('id') != body.get('id')]
                _save_atelier(st)
                return self._json({'ok': True, 'state': st})

        if path == '/atelier/gen':
            with _atelier_lock:
                st = _load_atelier()
                gid = (body.get('id') or '').strip()
                rec = next((g for g in st['generations'] if g.get('id') == gid), None) if gid else None
                if rec is None:
                    gid = gid or _atelier_new_id('g_', {g.get('id') for g in st['generations']})
                    rec = {'id': gid,
                           'createdAt': datetime.datetime.now().isoformat(timespec='seconds'),
                           'status': 'draft', 'verdict': 'unrated'}
                    st['generations'].append(rec)
                for k in ('camera', 'cameraLabel', 'sourceFrame', 'recipeId', 'recipeName', 'recipeSlug',
                          'prompt', 'status', 'verdict', 'note', 'parentId', 'outputUrl', 'costUsd'):
                    if k in body:
                        rec[k] = body[k]
                for k in ('bind', 'params'):
                    if isinstance(body.get(k), dict):
                        rec[k] = body[k]
                _save_atelier(st)
                return self._json({'ok': True, 'id': gid, 'state': st})

        if path == '/atelier/gen/delete':
            with _atelier_lock:
                st = _load_atelier()
                st['generations'] = [g for g in st['generations'] if g.get('id') != body.get('id')]
                _save_atelier(st)
                return self._json({'ok': True, 'state': st})

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
    ThreadingHTTPServer(('0.0.0.0', port), Handler).serve_forever()
