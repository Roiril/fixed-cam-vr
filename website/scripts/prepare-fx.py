"""演出一覧の素材と一覧の HTML を、演出ライブラリー（tools/fx-library）の出力から作る。

    py -3.11 website/scripts/prepare-fx.py            # Drive の最新の版から
    py -3.11 website/scripts/prepare-fx.py --stamp 20261112_1537

入力は Drive `reports/fx-library/<時刻>/`（`latest.json` が最新）。体験者の目線で撮ったツインの走行から
演出ごとに切った動画（clip.mp4）と山場の 1 枚（hero.jpg）を、サイト用に縮めて `assets/fx/` へ置く。
一覧（周ごとの並び・名前・1 行の説明）は下の LIST が正。訪問者向けの言葉で書く（実装の呼び名を出さない）。
`index.html` の `<!-- fx:begin -->` から `<!-- fx:end -->` までを書き換える。文言を変えたら `npm run fonts`。
"""

from pathlib import Path
import argparse
import html
import json
import os
import shutil
import subprocess
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SITE = ROOT / "website"
OUT = SITE / "assets" / "fx"
LIBRARY = Path("G:/マイドライブ/shubie/reports/fx-library")
TWIN_META = "PC上の再現映像 / 音声なし"

# (周の札, [(ライブラリーの id, 名前, 説明)])。id が None の行は既にある実機録画（assets/footage-*）
LIST = [
    ("導入", [
        ("tv-approach", "テレビが寄ってくる", "暗闇の奥に黒いテレビが現れて、目の前まで寄ってくる。"),
        ("tv-power", "電源が入る", "テレビが点いて、監視カメラの映像が映る。"),
    ]),
    ("一周目", [
        ("handprint", "血の手形", "壁に、血の手形が滲む。"),
        ("handprint-clear", "報告する", "異変を報告すると、手形は消える。"),
    ]),
    ("二周目", [
        ("stain", "幕のシミ", "幕に、顔のようなシミが滲んで広がる。"),
        ("stain-face", "笑う顔", "幕が膨らんで、笑う顔がせり出す。幕の縁から人形が覗く。"),
        ("backrooms", "子供部屋", "画面ごと、古い子供部屋へ飛ばされる。人形が畳に落ちて、首だけがこちらを向く。"),
        ("pov-chase", "人形の目", "人形の目線が割り込む。自分の背中が、近づいてくる。"),
        ("takeover", "乗っ取り", "画面が砂嵐になって、警告が出る。案内役の顔が人形に変わる。"),
    ]),
    ("三周目", [
        ("warp", "歪む部屋", "部屋が伸び縮みして、傾く。"),
        ("doll-a", "人形の自分", "自分が立っているはずの場所に、人形が立っている。"),
        ("doll-b", "録画の中の人形", "一周目の録画が流れる。そこに居たのは、人形だった。"),
        ("screens", "増えるスクリーン", "暗闇の奥から、テレビが次々に寄ってきて周りを埋める。"),
    ]),
    ("最後", [
        ("final-enter", "人形の群れ", "画面の半分を、人形の群れが埋める。"),
        ("halt", "止まってください！", "案内役が、歩くのを止める。"),
        ("return", "取り除く", "最後の報告で人形が消えて、現実に戻る。"),
        ("farewell", "人形との別れ", "人形がこちらを向く。背を向けて、奥へ消える。"),
    ]),
    ("終幕", [
        ("outro-return", "帰還End", "テレビの電源が落ちて、結果が出る。"),
        ("outro-doll", "人形End", "報告しないまま歩き続けると、画面は黒に落ちる。"),
    ]),
    ("以前の版", [
        (None, "fracture", "現実がスクリーンになる", "目の前の現実が割れて、映像を映すスクリーンへ変わる。", "Meta Quest 3 / 片目の録画 / 14秒 / 音声なし"),
        (None, "error", "エラーの演出", "視界に広がる警告。遮断しようとする表示も壊れていく。", "Meta Quest 3 / 右目の録画 / 11秒 / 音声なし"),
        (None, "eye", "目の演出", "スクリーンの外に現れる目。暗闇から、こちらを見ている。", "Meta Quest 3 / 左目の録画 / 11秒 / 音声なし"),
    ]),
]

THUMB_W = 320


def run(*args):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *map(str, args)], check=True)


def write_atomic(path: Path, text: str):
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    os.replace(tmp, path)


def thumb_from(image: Image.Image, dest: Path):
    thumb = image.convert("RGB")
    thumb.thumbnail((THUMB_W, THUMB_W))
    thumb.save(dest, "WEBP", quality=72, method=6)
    return thumb.size


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--stamp", help="ライブラリーの版（既定は latest.json）")
    parser.add_argument("--html-only", action="store_true", help="素材は作り直さず、一覧の HTML だけ書き換える")
    args = parser.parse_args()
    if shutil.which("ffmpeg") is None:
        parser.error("ffmpeg が要る")

    stamp = args.stamp or json.loads((LIBRARY / "latest.json").read_text(encoding="utf-8"))["stamp"]
    base = LIBRARY / stamp
    library = json.loads((base / "library.json").read_text(encoding="utf-8"))
    by_id = {fx["id"]: fx for fx in library["fx"]}
    catalog = json.loads((ROOT / "tools" / "fx-library" / "catalog.json").read_text(encoding="utf-8"))
    crops = catalog["crops"]
    crop_of = {fx["id"]: crops[fx.get("crop", "screen")] for fx in catalog["fx"]}

    missing = [row[0] for _, rows in LIST for row in rows if row[0] is not None and row[0] not in by_id]
    if missing:
        sys.exit(f"ライブラリー {stamp} に無い演出: {', '.join(missing)}（LIST か catalog.json を直す）")

    OUT.mkdir(parents=True, exist_ok=True)
    parts = []
    total = 0
    for label, rows in LIST:
        items = []
        for row in rows:
            if row[0] is None:
                _, fid, name, say, meta = row
                src, poster = f"assets/footage-{fid}.mp4", f"assets/footage-{fid}.webp"
                small = OUT / f"old-{fid}-s.webp"
                if not args.html_only:
                    with Image.open(SITE / poster) as image:
                        size = thumb_from(image, small)
                else:
                    with Image.open(small) as image:
                        size = image.size
                items.append((fid, name, say, meta, src, poster, f"assets/fx/{small.name}", size))
                continue

            fid, name, say = row
            fx = by_id[fid]
            clip, hero = base / fx["files"]["clip"], base / fx["files"]["hero"]
            video, poster, small = OUT / f"{fid}.mp4", OUT / f"{fid}.webp", OUT / f"{fid}-s.webp"
            if not args.html_only:
                # 幅 960 まで。色と速さは変えない
                run("-i", clip, "-an", "-vf", "scale='min(960,iw)':-2:flags=lanczos", "-c:v", "libx264", "-preset", "slow",
                    "-crf", "27", "-pix_fmt", "yuv420p", "-movflags", "+faststart", video)
                x0, y0, x1, y1 = crop_of[fid]
                with Image.open(hero) as image:
                    still = image.convert("RGB")
                    # 山場の 1 枚は、版によって切り出し済みのことがある
                    if still.size == (1920, 1080) and (x1 - x0, y1 - y0) != still.size:
                        still = still.crop((x0, y0, x1, y1))
                    still.thumbnail((960, 960))
                    still.save(poster, "WEBP", quality=78, method=6)
                    size = thumb_from(still, small)
            else:
                with Image.open(small) as image:
                    size = image.size
            seconds = round(float(fx["end"]) - float(fx["start"]))
            total += video.stat().st_size
            items.append((fid, name, say, f"{TWIN_META} / {seconds}秒", f"assets/fx/{video.name}", f"assets/fx/{poster.name}", f"assets/fx/{small.name}", size))
        parts.append((label, items))

    e = html.escape
    lines = []
    for label, items in parts:
        lines.append(f'                <div class="fx-part">')
        lines.append(f'                  <h3 class="fx-part-name">{e(label)}</h3>')
        lines.append(f'                  <ol class="fx-list">')
        for fid, name, say, meta, src, poster, small, (w, h) in items:
            lines.append(
                f'                    <li><button type="button" data-video-select="{e(fid)}" data-src="{e(src)}" data-poster="{e(poster)}" aria-pressed="false">'
                f'<span class="fx-thumb" aria-hidden="true"><img data-src="{e(small)}" width="{w}" height="{h}" alt="" decoding="async"></span>'
                f'<span class="fx-name">{e(name)}</span></button>'
                f'<p class="fx-say" hidden>{e(say)}</p><p class="fx-meta" hidden>{e(meta)}</p></li>'
            )
        lines.append(f'                  </ol>')
        lines.append(f'                </div>')

    index = SITE / "index.html"
    text = index.read_text(encoding="utf-8")
    begin, end = "<!-- fx:begin -->", "<!-- fx:end -->"
    assert begin in text and end in text, "index.html に fx:begin / fx:end の印が無い"
    head, rest = text.split(begin, 1)
    _, tail = rest.split(end, 1)
    write_atomic(index, head + begin + "\n" + "\n".join(lines) + "\n                " + end + tail)
    count = sum(len(items) for _, items in parts)
    print(f"版 {stamp}（組んだ時刻 {library.get('built')}）: {count} 本、ツインの動画 {total / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
