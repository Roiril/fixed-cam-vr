"""焼き込み（show.json + 参照アセット → `Assets/StreamingAssets/show/`）の中身。

**入口は 2 つあり、どちらもこの関数を通る。**

1. 卓の「📦 ビルド用エクスポート」（`capture-server.py` の `POST /export-build`）
2. APK を焼く前の自動実行（`tools/export-show-build.py` ← `tools/unity.ps1 build fixedcam`）

⚠⚠ **2 を足したのは、1 が手で押す 1 手だったから**（2026-09-03）。押し忘れた焼き込みは
`timeline.rev 33` のまま 2 週間以上残り、卓は 44 まで進んでいた。普段これが表に出ないのは
端末キャッシュが焼き込みより上だからだが、**APK を焼き直すとキャッシュは捨てられる**
（`CachedConfig.buildGuid`）。焼き直した機を卓なしで起動すると、古い著作がそのまま体験になる。

読む show は**呼び手が渡す**。卓は自分がメモリに持っている現物の複製を渡し、CLI はディスクの
`show.json` を読んで渡す。`_mutate_show` が変更のたびに書き出すので、どちらも同じ中身になる。

⚠ 書き出し先は `Assets/StreamingAssets/show/` だけ。**`show.json`（卓の正本）は 1 バイトも触らない**
（動いている卓はメモリに show を持っているので、ディスクを直接書くと黙って巻き戻る）。
"""
import json
import os
import shutil
import time
from urllib.parse import quote, unquote


def local_url_dirs(root):
    """ローカル URL → 実ディレクトリの対応表（`root` = tools/web-compositor）。

    卓（`capture-server.py`）はこれと同じ表を自分の定数から組んでおり、`LOCAL_URL_DIRS` として
    渡してくる。CLI は卓の定数を読まずに済むよう、ここで組む。
    """
    return {
        '/masks/': os.path.join(root, 'masks'),
        '/captures/': os.path.join(root, 'captures'),
        '/recordings/': os.path.join(root, 'recordings'),
        '/static-inputs/': os.path.join(root, 'static-inputs'),
        '/audio/': os.path.join(root, 'audio'),
        '/testassets/': os.path.join(root, 'testassets'),
        '/eyejack/norm/': os.path.join(root, 'eyejack', 'norm'),
    }


def resolve_local_asset(url, dirs):
    """ローカル URL（/masks/... /captures/... /static-inputs/... /recordings/...）を実ファイルへ解決。

    外部 http URL・空・`sa://` は None（＝焼き込み対象外）。パストラバーサルは拒否。
    """
    if not url or not url.startswith('/'):
        return None
    clean = url.split('?', 1)[0]
    for prefix, base in dirs.items():
        if clean.startswith(prefix):
            name = unquote(clean[len(prefix):])
            fp = os.path.abspath(os.path.join(base, name))
            # base の外へ出る参照は拒否。
            # ⚠ 別ドライブ（"/captures/G:/x"）だと commonpath が ValueError を投げるので、
            #    ここで握って「解決できない」へ倒す（安全側）。
            try:
                if os.path.commonpath([fp, os.path.abspath(base)]) != os.path.abspath(base):
                    return None
            except ValueError:
                return None
            return fp if os.path.isfile(fp) else None
    return None


def step_asset_slots(show):
    """v3 演出のカットのうち assetUrl（事前映像 / 静止画）を持つものを列挙する。

    焼き込みが `sa://` 化のために書き換える対象そのもの（走査漏れをテストで固定できるよう分離）。
    """
    slots = []
    for seg in ((show.get('timeline') or {}).get('segments') or []):
        for take in (seg.get('takes') or []):
            for step in (take.get('steps') or []):
                if step.get('assetUrl'):
                    slots.append(step)
    return slots


def referenced_cue_ids(show):
    """timeline / schedule / ライブ control が参照する cueId の集合（dangling 参照の検出に使う）。

    v3: `segments[].takes[].steps[].cueId` / v2: `segments[].cues[].cueId` + `insert.cueId`。
    """
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


def export_build(show, repo_root, dirs, out_dir=None):
    """`show`（辞書）と参照アセットを `Assets/StreamingAssets/show/` へ焼き込む。

    cues の maskUrl / sourceUrl・bgmTracks の url・演出カットの assetUrl・目のジャックの写真の
    実ファイルを `assets/` へコピーし、URL を `sa://assets/<file>` に書き換える。

    引数の `show` は**書き換える**ので、呼び手が複製を渡すこと（卓は現物を守るため deep copy を渡す）。
    戻り値は卓 UI がそのまま出す JSON。
    """
    out_dir = out_dir or os.path.join(repo_root, 'Assets', 'StreamingAssets', 'show')
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
    # 卓の URL → sa:// URL の対応表。**端末キャッシュを焼き込みへ読み替えるために実機が使う。**
    # ⚠ `copied` では代用できない。同じ実ファイルを複数の URL が指すとき、2 本目以降は
    #   `src_to_dest` で早期 return するので `copied` に積まれない（対応表からは漏れる）。
    url_map = {}

    def bake(url):
        fp = resolve_local_asset(url, dirs)
        if not fp:
            # ローカル URL（/captures/... 等）なのに解決できない＝実ファイルが無い。
            # 黙って素通しすると APK に素材が入らず、現地 PC 不在でそのカットだけ無映像になる
            # （卓には ✓ としか出ないので気づけない。2026-07-26 監査 HIGH）。
            if url.startswith('/') and url not in unresolved:
                unresolved.append(url)
            return url  # 外部 URL / 空 / 解決不能はそのまま
        if fp in src_to_dest:
            out = 'sa://assets/' + quote(src_to_dest[fp])
            url_map[url] = out
            return out
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
        out = 'sa://assets/' + quote(name)
        url_map[url] = out
        return out

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
    for step in step_asset_slots(show):
        step['assetUrl'] = bake(step['assetUrl'])

    # 目の視界ジャックの当日写真（canon/LEDGER.md 0099）。焼き込んでおけば、
    # **卓が居ない機でも写真が出る**（当日の保険。写真は撮った直後に焼き直すのが本筋だが、
    # 焼けなかった機は long-poll で受け取る → どちらの経路でも成立する）。
    ej = show.get('eyejack') or {}
    if ej.get('photos'):
        ej['photos'] = [bake(u) for u in ej['photos'] if u]
        show['eyejack'] = ej

    # cue 参照走査: timeline / schedule / ライブ control が指す cueId が cues[] に実在するか検証。
    # 全 cue のアセットは上のループで焼き込み済み（cueId 参照は新規アセットを持たない）ため
    # 追加コピーは不要。ここでは dangling 参照を missingCues として返し UI で気づけるようにする。
    cue_ids = {c.get('id') for c in show.get('cues', [])}
    referenced = referenced_cue_ids(show)
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

    # 卓の URL → sa:// URL の対応表を焼き込みへ載せる（2026-09-05）。
    #
    # ⚠⚠ **端末キャッシュは焼き込みより優先される**（`ShowControlClient` の 焼き込み < キャッシュ <
    #   ライブ）。キャッシュには卓の相対 URL がそのまま入るので、一度でも卓に繋いだ機は
    #   「卓を指す URL」を持ったまま再起動する。卓が落ちていると素材が 1 つも解決できない。
    #   実機（Quest α）のキャッシュを実測したところ、素材 URL 39 本すべてが相対だった。
    #   実機はこの表で読み替える（`ShowControlClient.RemapCachedAssetsToBaked`）。
    #
    # ⚠ JsonUtility は Dictionary を読めないので配列で持つ。
    show['assetMap'] = [{'from': k, 'to': v} for k, v in sorted(url_map.items())]

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
    return {'ok': True, 'outDir': out_dir, 'showJson': show_path,
            'copied': copied, 'count': len(copied), 'totalBytes': total,
            'referencedCues': len(referenced), 'missingCues': missing,
            'missingTracks': missing_tracks, 'unresolvedAssets': unresolved,
            'exportedAt': time.strftime('%Y-%m-%d %H:%M:%S'),
            'showRev': show.get('rev', 0),
            'timelineRev': (show.get('timeline') or {}).get('rev', 0),
            'hosts': hosts}
