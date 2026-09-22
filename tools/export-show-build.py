#!/usr/bin/env python3
"""卓の著作（show.json + 参照アセット）を `Assets/StreamingAssets/show/` へ焼き込む。

    py -3.11 tools/export-show-build.py          # 焼き込む
    py -3.11 tools/export-show-build.py --check  # 本文と素材の内容一致を検証する

`tools/unity.ps1 build fixedcam` が APK を焼く前に自動で呼ぶ。**手で押す必要は無い。**

Player は同梱された演出のみを使う（baked-only-v1）。ライブ更新と旧設定キャッシュは使わない。
必要素材を検証してから書き出し、manifest に本文と素材の SHA256 を記録する。
欠損した参照や外部素材は書き出しを失敗させる。--check は rev だけでなく内容を照合する。

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
import release_inventory as _inventory  # noqa: E402


def _load(path):
    with io.open(path, encoding='utf-8') as f:
        return json.load(f)


def _revs(show):
    return int(show.get('rev', 0) or 0), int((show.get('timeline') or {}).get('rev', 0) or 0)


def main(argv=None):
    sys.stdout.reconfigure(encoding='utf-8')  # cp932 端末で日本語が化けないように
    ap = argparse.ArgumentParser(description='卓の著作を APK の焼き込みへ反映する')
    ap.add_argument('--check', action='store_true',
                    help='書き込まずに本文と素材の内容一致を検証する')
    ap.add_argument('--show', default=SHOW_FILE, help='読む show.json（既定: 卓のもの）')
    ap.add_argument('--out', default=BAKED_DIR, help='焼き込み先（既定: StreamingAssets/show）')
    args = ap.parse_args(argv)

    if not os.path.isfile(args.show):
        print(f'✗ 卓の show.json がありません: {args.show}')
        return 4

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
        try:
            expected, _ = _inventory._planned(live, _export.local_url_dirs(COMPOSITOR))
        except (OSError, ValueError) as exc:
            print(f'✗ 演出素材を準備できません: {exc}')
            return 4
        actual = _inventory.verify_bundle(args.out)
        if actual == expected:
            print(f'✓ 焼き込みは卓と同じ  contentId={expected}')
            return 0
        print(f'✗ 焼き込みが卓と違う  焼き込み contentId={actual or "未検証"}'
              f'  →  卓 contentId={expected}')
        print('  直す: py -3.11 tools/export-show-build.py')
        return 4

    try:
        res = _export.export_build(live, REPO_ROOT, _export.local_url_dirs(COMPOSITOR), out_dir=args.out)
    except (OSError, ValueError) as exc:
        print(f'✗ 焼き込みに失敗: {exc}')
        return 4

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
