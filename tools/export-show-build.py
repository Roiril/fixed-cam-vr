#!/usr/bin/env python3
"""卓の著作（show.json + 参照アセット）を `Assets/StreamingAssets/show/` へ焼き込む。

    py -3.11 tools/export-show-build.py          # 焼き込む
    py -3.11 tools/export-show-build.py --check  # 焼き込まず、古いかどうかだけ言う

`tools/unity.ps1 build fixedcam` が APK を焼く前に自動で呼ぶ。**手で押す必要は無い。**

⚠⚠ **なぜ自動にしたか**（2026-09-03）。焼き込みは卓の「📦 ビルド用エクスポート」でしか
更新されず、押し忘れると古いまま残る。実際に `timeline.rev` が 33（8/17）で止まっていて、
卓は 44 まで進んでいた。普段これが表に出ないのは端末キャッシュが焼き込みより上だからだが、
**APK を焼き直すとキャッシュは捨てられる**（`CachedConfig.buildGuid` の照合）ので、
焼き直した機を卓なしで起動すると 8/17 の著作がそのまま体験になる。

読むのは卓のディスク上の `tools/web-compositor/show.json`。卓が動いていてもこれが正
（`_mutate_show` が変更のたびに書き出す）。**卓の show.json は 1 バイトも書き換えない。**
"""
import argparse
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)
COMPOSITOR = os.path.join(HERE, 'web-compositor')
SHOW_FILE = os.path.join(COMPOSITOR, 'show.json')
BAKED_DIR = os.path.join(REPO_ROOT, 'Assets', 'StreamingAssets', 'show')

sys.path.insert(0, COMPOSITOR)
import export_build as _export  # noqa: E402  （sys.path を整えた後でないと読めない）


def _load(path):
    with io.open(path, encoding='utf-8') as f:
        return json.load(f)


def _revs(show):
    return int(show.get('rev', 0) or 0), int((show.get('timeline') or {}).get('rev', 0) or 0)


def main(argv=None):
    sys.stdout.reconfigure(encoding='utf-8')  # cp932 端末で日本語が化けないように
    ap = argparse.ArgumentParser(description='卓の著作を APK の焼き込みへ反映する')
    ap.add_argument('--check', action='store_true',
                    help='焼き込まずに、焼き込みが卓より古いかどうかだけ報告する')
    ap.add_argument('--show', default=SHOW_FILE, help='読む show.json（既定: 卓のもの）')
    ap.add_argument('--out', default=BAKED_DIR, help='焼き込み先（既定: StreamingAssets/show）')
    args = ap.parse_args(argv)

    if not os.path.isfile(args.show):
        # 卓を一度も動かしていない機（show.json は git 管理外）。焼くものが無いので飛ばす。
        print(f'⚠ 卓の show.json が無いので焼き込みを飛ばす: {args.show}')
        return 0

    live = _load(args.show)
    live_rev, live_tl = _revs(live)

    baked_path = os.path.join(args.out, 'show.json')
    baked_rev, baked_tl = (0, 0)
    if os.path.isfile(baked_path):
        try:
            baked_rev, baked_tl = _revs(_load(baked_path))
        except (ValueError, OSError) as e:
            print(f'⚠ 焼き込みの show.json が読めない（焼き直す）: {e}')

    if args.check:
        if (baked_rev, baked_tl) == (live_rev, live_tl):
            print(f'✓ 焼き込みは卓と同じ  rev={live_rev} timeline.rev={live_tl}')
            return 0
        print(f'✗ 焼き込みが卓と違う  焼き込み rev={baked_rev} timeline.rev={baked_tl}'
              f'  →  卓 rev={live_rev} timeline.rev={live_tl}')
        print('  直す: py -3.11 tools/export-show-build.py')
        return 4

    res = _export.export_build(live, REPO_ROOT, _export.local_url_dirs(COMPOSITOR), out_dir=args.out)

    mb = res['totalBytes'] / (1024 * 1024)
    was = '' if (baked_rev, baked_tl) == (live_rev, live_tl) else f'（焼き込みは rev={baked_rev} timeline.rev={baked_tl} だった）'
    print(f'✓ 焼き込み rev={res["showRev"]} timeline.rev={res["timelineRev"]} '
          f'素材 {res["count"]} 件 {mb:.1f}MB → {res["showJson"]} {was}')

    # ⚠ ここから下は「焼けたが APK で出ないもの」。卓の UI では ✓ の隣に小さく出るだけなので、
    #    CLI では必ず本文として出す（現地 PC 不在でそのカットだけ無映像になる）。
    if res['unresolvedAssets']:
        print(f'⚠ 実ファイルが無い素材 {len(res["unresolvedAssets"])} 件（APK に入らない）:')
        for u in res['unresolvedAssets']:
            print(f'    {u}')
    if res['missingCues']:
        print(f'⚠ 定義の無い cue を指しているカットがある: {", ".join(res["missingCues"])}')
    if res['missingTracks']:
        print(f'⚠ 定義の無い BGM を指している区間がある: {", ".join(res["missingTracks"])}')

    # 焼き込んだカメラの接続先（現地 DHCP の IP がそのまま入る。APK の唯一の手がかり）。
    hosts = ' '.join(f'{h["id"]}={h["host"] or "-"}' for h in res['hosts'])
    if hosts:
        print(f'  カメラ: {hosts}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
