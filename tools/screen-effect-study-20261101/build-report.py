"""Assemble a portable report from explicitly selected, independently reviewed outputs."""
from __future__ import annotations

import argparse
import base64
import hashlib
import html
import json
import os
from pathlib import Path
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
OUT = Path("G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study")
REFERENCE_IMAGE = OUT / "sources/Screenshot_20261101-004612.png"
REFERENCE_VIDEO = OUT / "sources/screen-20261101-004528.mp4"
CSS = Path("C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def uri(path: Path, mime: str) -> str:
    if not path.is_file():
        raise FileNotFoundError(path)
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode("ascii")


def atomic_text(path: Path, value: str) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(value, encoding="utf-8", newline="\n")
    os.replace(temporary, path)


def score(record: dict) -> float:
    value = record["overall_similarity"] if "overall_similarity" in record else record["similarity_percent"]
    if isinstance(value, str):
        value = value.rstrip("%")
    value = float(value)
    if not 0 <= value <= 100:
        raise ValueError(value)
    return value


def reviewed_hash(record: dict, selected: Path) -> str:
    value = record.get("candidate_sha256")
    if isinstance(value, dict):
        value = value.get(str(selected)) or value.get(selected.name)
    if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdefABCDEF" for c in value):
        raise ValueError(f"Review must identify the selected file with SHA-256: {selected}")
    return value.lower()


def run(args: list[str]) -> dict:
    result = subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8")
    return json.loads(result.stdout) if result.stdout.strip().startswith("{") else {}


def reference_preview() -> Path:
    preview = OUT / "reference-preview.mp4"
    if not preview.exists():
        temporary = OUT / "reference-preview.tmp.mp4"
        run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(REFERENCE_VIDEO),
             "-vf", "scale=1170:540", "-r", "30", "-c:v", "libx264", "-crf", "21",
             "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", str(temporary)])
        run(["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(temporary)])
        os.replace(temporary, preview)
    return preview


def closure_sheet(motion_directory: Path) -> Path:
    times = [11.8, 12.4, 12.8, 13.6]
    reference_directory = OUT / "report-reference-fps10"
    reference_directory.mkdir(exist_ok=True)
    select = "+".join(f"eq(n\\,{round(time * 10)})" for time in times)
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(REFERENCE_VIDEO),
         "-vf", "fps=10,select=" + select, "-fps_mode", "vfr", "-frames:v", "4",
         str(reference_directory / ".temporary-%02d.png")])
    reference_files = []
    for index, time in enumerate(times, 1):
        temporary = reference_directory / f".temporary-{index:02d}.png"
        with Image.open(temporary) as frame:
            if frame.size != (2340, 1080):
                raise ValueError(f"Unexpected reference frame dimensions: {frame.size}")
        target = reference_directory / f"ref-{time:06.3f}.png"
        os.replace(temporary, target)
        reference_files.append({"path": str(target.relative_to(OUT)), "grid_time": time, "sha256": sha(target)})
    atomic_text(OUT / "report-reference-manifest.json", json.dumps({"source_sha256": sha(REFERENCE_VIDEO),
                "filter": "fps=10; full decode from the beginning; nearest VFR frame on the 0.1s grid",
                "files": reference_files}, ensure_ascii=False, indent=2))
    canvas = Image.new("RGB", (1170, 4 * 306), "white")
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 18)
    for row, time in enumerate(times):
        for column, (prefix, label) in enumerate((("ref", "参照"), ("prototype", "試作"))):
            directory = reference_directory if prefix == "ref" else motion_directory
            source = directory / f"{prefix}-{time:06.3f}.png"
            if not source.exists():
                raise FileNotFoundError(source)
            picture = Image.open(source).convert("RGB")
            picture.thumbnail((585, 270))
            x, y = column * 585, row * 306
            draw.text((x + 8, y + 4), f"{label} {time:.1f}秒 / {source.relative_to(OUT).as_posix()}", font=font, fill=(26, 26, 26))
            canvas.paste(picture, (x, y + 36))
    target = OUT / "closure-comparison.jpg"
    canvas.save(target, quality=94)
    return target


def effect_sheet(motion_directory: Path, key: str, times: list[float]) -> Path:
    asset_directory = motion_directory / "assets"
    metadata = json.loads((asset_directory / "effect-assets.json").read_text(encoding="utf-8"))["sequences"][key]
    atlas_path = asset_directory / metadata["file"]
    atlas = Image.open(atlas_path).convert("RGBA")
    width, height = metadata["cell"]
    columns = min(len(times), 3 if key == "foil" else 2)
    rows = (len(times) + columns - 1) // columns
    canvas = Image.new("RGB", (width * columns, (height + 42) * rows), (75, 75, 75))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.truetype("C:/Windows/Fonts/meiryo.ttc", 16)
    for column, time in enumerate(times):
        index = min(range(metadata["count"]), key=lambda n: abs(metadata["times"][n] - time))
        x, y = index % metadata["columns"] * width, index // metadata["columns"] * height
        tile = atlas.crop((x, y, x + width, y + height))
        background = Image.new("RGBA", (width, height), (75, 75, 75, 255))
        background.alpha_composite(tile)
        offset_x, offset_y = column % columns * width, column // columns * (height + 42)
        canvas.paste(background.convert("RGB"), (offset_x, offset_y + 42))
        draw.text((offset_x + 4, offset_y + 1), f'#{index} / {metadata["times"][index]:.1f}秒 / {width}×{height}', font=font, fill="white")
        draw.text((offset_x + 4, offset_y + 21), atlas_path.name, font=font, fill="white")
    target = OUT / f"report-{key}-inspection.png"
    canvas.save(target)
    return target


def build(still_base: Path, still_image: Path, still_preview: Path, still_process: Path, still_audit: Path, motion_directory: Path, motion_audit: Path, motion_input: str, still_atlas_directory: Path | None, draft: bool = False) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    still = json.loads(still_audit.read_text(encoding="utf-8"))
    processing = json.loads(still_process.read_text(encoding="utf-8"))
    image_edit = processing.get("kind") == "image_edit_mockup"
    if image_edit and still.get("geometry_preserved") is not True:
        raise ValueError("Image-edit still must pass the independent geometry check")
    if processing.get("round") in (32, 33, 34, 36) and (still.get("geometry_preserved") is not True or still.get("content_preserved") is not True):
        raise ValueError("Selected still must pass independent geometry and content checks")
    motion = json.loads(motion_audit.read_text(encoding="utf-8"))
    scores = [score(still), score(motion)]
    complete = min(scores) >= 85
    if not complete and not draft:
        raise ValueError(f"Completion threshold not met: {scores}")
    report_path = OUT / ("report-progress.html" if draft else "report.html")
    completion_text = "指定された85%以上の条件を見た目の試作で満たしました。" if complete else "静止画は指定された85%に達していないため、作業途中の記録です。"
    if still.get("diagnostic_only") or still.get("pass_candidate") is False:
        raise ValueError("Exploratory target evaluation cannot satisfy the completion condition")
    if still.get("selected_candidate_path") and Path(still["selected_candidate_path"]).resolve() != still_image.resolve():
        raise ValueError("Selected still differs from independently evaluated still")
    if still.get("selected_image") and Path(still["selected_image"]).resolve() != still_image.resolve():
        raise ValueError("Selected still differs from independently evaluated image")
    if reviewed_hash(still, still_image) != sha(still_image):
        raise ValueError("Selected still hash differs from independently evaluated still")
    if not still_preview.is_file():
        raise FileNotFoundError(still_preview)
    still_preview_link = html.escape(still_preview.relative_to(OUT).as_posix(), quote=True)
    movie_directory = motion_directory if motion_input == "a" else motion_directory / "alternate-b"
    movie = movie_directory / "motion-prototype.mp4"
    motion_preview = motion_directory / "motion-prototype.html"
    if not motion_preview.is_file():
        raise FileNotFoundError(motion_preview)
    motion_preview_link = html.escape(motion_preview.relative_to(OUT).as_posix(), quote=True)
    if reviewed_hash(motion, movie) != sha(movie):
        raise ValueError("Selected video differs from independently evaluated video")
    movie_probe = run(["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(movie)])
    motion_release_path = motion_directory / "release-manifest.json"
    motion_release = json.loads(motion_release_path.read_text(encoding="utf-8"))
    effect_figures = "".join(f'<figure><img src="{uri(effect_sheet(motion_directory, key, times),"image/png")}" alt="{label}"><figcaption>{label}。選択した版の素材から切り出した検品用画像。</figcaption></figure>'
                             for key, times, label in (("foil", [3.2, 6, 11.9], "銀色の薄片"), ("glyph", [6.3, 10, 12.4], "重なって変わる文字")))
    baseline_source = still_base
    baseline = Image.open(baseline_source).convert("RGBA")
    if baseline.size != Image.open(still_image).size:
        raise ValueError("Selected still and baseline dimensions differ")
    background = Image.new("RGBA", baseline.size, (18, 18, 18, 255))
    background.alpha_composite(baseline)
    before = OUT / "selected-base-on-black.png"
    background.convert("RGB").save(before)
    material_files = []
    material_manifest = None
    static_material_block = ""
    static_method = "輪郭付近の行を不揃いに横へずらします。明暗の境界へ短い複線と欠けを置き、暗部と明部がまとまるように階調を調整しました。広い面を一律にざわつかせず、横方向の荒れが輪郭で目立つようにしています。"
    static_reference_note = "青い色相とキャラクターは再現対象から外しました。"
    static_scope = "選択した加工は現在の入力に応答するコードで計算しています。方向を探すために作った生成画像は、別の探索記録として残しています。"
    static_check = "静止加工では、OFF時のRGBA一致と開口のalpha不変を検査しました。計算はPythonで行い、切替画面は計算済みの画像を表示します。ライブ映像へ適用したときのちらつきや時間的な安定性は未検証です。最終候補の計算方法と検証値は保存した処理記録に記載しています。"
    static_kind = "追加加工"
    static_scope_lead = "静止画は計算による加工です。動画版はブラウザで実行する試作です。"
    executable_block = ""
    generation_files = []
    if image_edit:
        static_scope_lead = "静止画の最終版は画像編集による見た目の試作です。実行できるフィルタ版の最高評価は83%です。動画版はブラウザで実行する試作です。"
        if still_atlas_directory is not None:
            raise ValueError("Image-edit still cannot be presented as an executed atlas filter")
        static_kind = "画像編集による見た目の試作"
        static_method = "実行フィルタの出力を下地にし、内蔵image_genで静止画の見た目を編集しました。部屋と物の配置を保ったまま、細い白黒線の重なりと輪郭の横裂けを参照へ近づけています。仕上げに元の開口のalphaと外周の画素を戻しました。"
        static_reference_note += "画像編集には参照画像を渡しています。参照から影響を受けた見た目を試すための静止画です。"
        static_scope = "この静止画は画像編集の成果物です。任意の映像へ同じ処理を適用できる実行フィルタを表すものではありません。実行フィルタも試作しましたが、その最高評価は83%で、85%には達していません。"
        static_check = "画像編集版は、現行入力と比べた部屋の配置・物体の輪郭の保持を独立担当が判定した後に加工感を採点しました。開口のalphaと透明な部分のRGBは元画像と一致します。内側2pxの画素も元へ戻しています。生成前の指示文と参照の出所、生成直後の画像、整形後の画像のSHA-256を保存しました。画像生成自体は決定論的な再実行を保証しません。"
        executable = OUT / "still/round-23/B-standard-on-black.png"
        executable_audit = OUT / "audits/still-23.json"
        previous = json.loads(executable_audit.read_text(encoding="utf-8"))
        if score(previous) != 83:
            raise ValueError("Executable still baseline score changed")
        expected = processing["filter_underlay"]
        if sha(executable) != expected["sha256"]:
            raise ValueError("Executable filter underlay differs from the recorded input")
        generation_files = [still_process.parent / "prompt.txt", still_process.parent / "generated-raw.png", executable, executable_audit]
        request_path = still_process.parent / "generation-request.json"
        if request_path.is_file():
            generation_files.append(request_path)
            request = json.loads(request_path.read_text(encoding="utf-8"))
            generation_files.extend(Path(p) for p in request["referenced_image_paths"])
        executable_block = f'''<div class="wide"><details><summary>実行できる加工の到達点（83%）を見る</summary><figure><img src="{uri(executable,'image/png')}" alt="実行フィルタ版の最高評価83%の出力"><figcaption>第23版 standard。明暗の整理に加え、参照から得た輪郭の明暗残差を現在の輪郭へ合わせて合成しています。参照の形や陰影と加工の成分を完全には分離できません。画像編集版の下地として使いました。</figcaption></figure><p>27回の実行フィルタ試作の最高値は83%でした。画像編集版の点数と混ぜずに記録しています。現時点でライブ映像へ同じ見た目を出せるという検証にはなりません。</p></details></div>'''
    if still_atlas_directory is not None:
        material_path = still_atlas_directory / "atlas-manifest.json"
        material_manifest = json.loads(material_path.read_text(encoding="utf-8"))
        if material_manifest["reference_sha256"] != sha(REFERENCE_IMAGE):
            raise ValueError("Still material reference differs from the frozen original")
        if processing.get("atlas_manifest_sha256") and processing["atlas_manifest_sha256"] != sha(material_path):
            raise ValueError("Still material differs from the processing record")
        material_files = [material_path, still_atlas_directory / "atlas-numbered-overview.png"]
        for item in material_manifest["items"]:
            asset = still_atlas_directory / item["file"]
            if sha(asset) != item["sha256"]:
                raise ValueError(f"Still material hash differs: {asset}")
            material_files.append(asset)
        static_method = "明暗を整理した後、参照の窓とモニターの縁から得た明暗の残差を使います。現在の画像の輪郭位置と傾き、明暗差へ合わせ、横裂けと細い複線が輪郭に沿って現れるようにしました。広い面の内部には同じ乱れを散らしていません。"
        static_reference_note += "参照由来の輪郭残差を素材として使っています。元の部屋やキャラクターのRGBを直接貼り付ける処理はありません。"
        static_material_block = f'''<div class="wide"><figure><img src="{uri(material_files[1],'image/png')}" alt="番号と寸法を添えた参照由来の輪郭残差3種"><figcaption>参照の輪郭から得た素材。灰色は差分ゼロです。単一画像から加工と元の形、反射、陰影を完全には分離できません。素材の単独表示と、向き・明暗を変えた合成入力で検査しました。</figcaption></figure></div>'''
    if processing.get("round") == 28:
        static_method = "参照の輪郭から得た明暗の残差を、横方向の差を計算する短いフィルタへ変換しました。現在の画像の各行へ作用させ、物の明暗差に応じた細線と横へのにじみを作ります。大きな面を同じ濃さの粒で埋めず、細部の多い領域で強く現れるようにしました。"
        static_reference_note += "参照由来の明暗残差を加工素材として使います。元の部屋やキャラクターのRGBを貼り付ける処理はありません。"
    if processing.get("round") in (30, 31):
        static_method = "元画像の境界を細く整理した後、1画素高の行ごとに横へ少しずらします。遅れた明暗を重ね、輪郭から細い明暗の対が出るようにしました。既存の矩形の欠けを使う下地は外し、現在の画像から計算しています。"
    if processing.get("round") in (32, 33, 34):
        static_kind = "参照の微細模様を転写する見た目試作"
        static_scope_lead = "静止画は参照の細かな明暗模様を移す見た目試作です。元の映像で使われた加工方法を特定・再現したという結果ではありません。動画版も参照由来の素材を使うブラウザの試作です。"
        static_method = "参照の窓と机と床から、細かな明暗の模様を小さな素材として取り出しました。現在の画像の輪郭の向きと明暗差が近い部分へ対応させ、高周波の成分を置き換えます。広い無地面への転写は抑え、物の大きな配置が変わらないよう8画素単位の平均を元画像へ合わせました。"
        static_reference_note = "青い色相とキャラクターは再現対象から外しました。参照の人物を含まない部分から得た明暗模様を素材として使います。細かな成分にも元の配線などの形が残り得るため、加工だけを分離できたとは言えません。"
        static_scope = "この方式は参照の微細模様を転写する試作です。辞書素材を単独で並べて確認し、元のB画像と比べて物体の追加や消失を検査しました。8画素単位の平均が一致することだけで内容の保持を認定していません。実際の加工原因や、他の映像にも同じ見た目が得られる汎用性は未検証です。"
        if processing.get("round") == 33:
            static_method = "現在の画像から明暗の基調を作り、太い矩形の欠けは加えません。その輪郭の向きと明暗差が近い参照の微細模様を対応させます。細線を平均して薄める方式と、境目でつなぐ方式を比較しました。8画素単位の平均は、追加の明暗処理を済ませた下地へ合わせています。元の未追加画像と同じ明るさを保つ方式ではありません。"
        if processing.get("round") == 34:
            static_kind = "固定したB画像への合成試作"
            static_scope_lead = "静止画は固定したB画像への合成試作です。83%の処理出力を下地にし、参照から得た細線の差分を重ねました。任意の映像へ適用するフィルタとしての検証は行っていません。動画版は参照由来の素材を使うブラウザの試作です。"
            static_method = "輪郭の横ほつれが残る第23版を下地にしました。第33版で作った細線の出力から、その版の下地を引き、明暗の基調を含まない差分を作ります。この差分からさらに大きい変化を取り除き、細い成分だけを重ねました。広い面を静かに保ちながら、床や輪郭の近くへ細い横線を加える構成です。"
            static_scope = "保存したB画像の組み合わせを使う合成試作です。使った下地と差分の出所をSHA-256付きで記録しました。現在の同じB画像から作った素材を使っていますが、別の映像でも同じ処理が成り立つ汎用性や、ライブ映像での安定性は未検証です。参照の微細模様には元の物体の細線が残り得ます。素材単独と元Bの両方で内容を照合しています。"
            static_check = "追加した細線をOFFにすると、83%版の下地とRGBAの全画素が一致します。差分を0にした計算でも同じ結果です。最終画像の開口のalphaと透明な部分のRGB、内側2pxは元の現行Bと一致します。元の現行Bへの切替も別に用意しました。Pythonで計算した固定画像の比較であり、時間変化やQuestの実時間性能は未検証です。"
        dictionary_directory = still_process.parent if processing.get("round") == 32 else OUT / "still/round-32"
        dictionary_path = dictionary_directory / "dictionary-manifest.json"
        review_path = dictionary_directory / "dictionary-review.json"
        material_manifest = json.loads(dictionary_path.read_text(encoding="utf-8"))
        material_review = json.loads(review_path.read_text(encoding="utf-8"))
        if material_manifest["source_sha256"] != sha(REFERENCE_IMAGE):
            raise ValueError("Texture material reference differs from the frozen original")
        material_files = [dictionary_path, review_path, *sorted(dictionary_directory.glob("dictionary*.npz"))]
        material_figures = []
        for filename, expected_hash in material_manifest["sheets"].items():
            page = dictionary_directory / filename
            if sha(page) != expected_hash:
                raise ValueError(f"Dictionary inspection sheet changed: {page}")
            material_files.append(page)
            material_figures.append(f'<figure><div class="pixel-box"><img src="{uri(page,"image/png")}" alt="{html.escape(filename)}"></div><figcaption>{html.escape(filename)}。番号と出所を添えた検品表。</figcaption></figure>')
        material_files.extend(sorted(dictionary_directory.glob("reference-ROI-*.png")))
        if processing.get("round") == 34:
            material_files.extend([OUT / "still/round-23/B-standard.png", OUT / "still/round-33/B-dense.png",
                                   OUT / "still/round-33/B-baseline-protected.png", OUT / "still/round-33/manifest.json"])
        excluded = material_review["excluded_ids"]
        excluded_text = " / ".join(str(value) for value in excluded)
        static_material_block = f'''<div class="wide"><details class="target-study"><summary>参照から得た微細模様の検品表を開く</summary><p>候補は{len(material_manifest['items'])}片。この表には除外した例も含まれます。ID {excluded_text} の{len(excluded)}片は元の物体の角や接続部を連想するため使用しませんでした。人物と文字を含まないROIから採取していますが、残る微細な形にも元の物体の影響はあり得ます。</p>{''.join(material_figures)}</details></div>'''
    sheet = closure_sheet(movie_directory)
    preview = reference_preview()
    target_image = OUT / "target-study/imagegen-target.png"
    target_block = f'''<div class="wide"><details class="target-study"><summary>生成画像で加工の方向を探した記録を開く</summary><figure><img src="{uri(target_image,'image/png')}" alt="生成AIによる加工方向の探索画像。合格対象外"><figcaption>カメラAを使った探索用の生成画像。内蔵image_genで生成。実フィルタ出力とは分けて保存し、合格対象にしていません。<a href="target-study/prompt.txt">生成に使った指示文</a>。</figcaption></figure></details></div>'''
    audit_rows = []
    for path in sorted((OUT / "audits").glob("*.json")):
        item = json.loads(path.read_text(encoding="utf-8"))
        if "overall_similarity" in item or "similarity_percent" in item:
            audit_rows.append(f"<tr><td>{html.escape(path.stem)}</td><td>{score(item):g}%</td></tr>")
    audit_details = "".join(f"<h3>{name}</h3><pre>{html.escape(json.dumps(record, ensure_ascii=False, indent=2))}</pre>"
                            for name, record in (("静止画の最終評価原文", still), ("動画の最終評価原文", motion)))
    document = f'''<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>スクリーンの追加加工・動画の動きと閉じ方</title>
<style>{CSS.read_text(encoding="utf-8")}
.media-grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:24px}}
.media-grid img,.media-grid video,.wide>figure>img{{display:block;width:100%;height:auto}}
.target-study img{{display:block;width:100%;height:auto}}
.media-grid figure{{min-width:0;margin:0}}pre{{white-space:pre-wrap;overflow-wrap:anywhere;font-size:var(--fs-sm);background:var(--surface);padding:16px}}
.pixel-box{{max-width:100%;overflow:auto}}.pixel-box.native-pixels img{{width:auto;max-width:none}}
.actions{{display:flex;gap:16px;flex-wrap:wrap;margin:16px 0}}button{{font:inherit;color:var(--ink);background:var(--surface);border:0;padding:8px 16px;cursor:pointer}}
button:focus-visible{{outline:2px solid var(--link);outline-offset:4px}}td,th{{vertical-align:top}}.paths{{overflow-wrap:anywhere}}
@media(max-width:40rem){{.media-grid{{grid-template-columns:1fr}}}}
</style></head><body><main>
<div class="wrap"><h1>スクリーンの追加加工<br>動画の動きと閉じ方</h1><p class="meta">2026-11-01 ／ 本編に組み込まない独立試作</p>
<p class="lead">静止画の加工感は独立評価で{scores[0]:g}%。動画の動きと閉じ方は{scores[1]:g}%でした。{completion_text}本編への組み込みは行っていません。</p>
<p>{static_scope_lead}</p>
<p>いまの曲面スクリーンとCRT加工を含むUnityの描画結果を固定し、その画素の上に加工を加えました。別スレッドで進んでいるスクリーンの実装とは、編集するファイルを分けています。</p>
<p>点数は比較担当のサブエージェントが画像と連続コマから判断した主観値です。画素の一致率ではありません。実装担当と評価担当を分け、評価担当へ目標点や前の採点を伝えずに比較しています。</p>
<div class="actions"><a href="{still_preview_link}">静止画の加工前後を切り替える</a><a href="{motion_preview_link}">動画を再生・シークする</a></div>
<h2>静止画は輪郭の横裂けと明暗のまとまりが特徴</h2>
<p>縦の輪郭が細かな横線にほどけています。行ごとに左右へ短く突き出し、一本の線が不揃いな明暗の複線になります。床や机の細い線にも白と黒が接して現れます。</p>
<p>ざらつきは横へ短く続く筋に見えます。物の境界と細部に集まり、大きい面の内部では弱くなります。ぼけた大きな形と、鋭く荒れた輪郭が同居しています。</p>
<p>暗い面は黒くまとまります。強い明部は内部の差が減って白い面になります。中間の明るさには連続した階調が残るので、全面を二値化した状態とは異なります。</p>
<p>撮像の観点では、輪郭の強調と横方向の信号乱れが重なったように見えます。版画の観点では、白黒の境界が削れたようにも見えます。どちらも見え方の説明であり、元の映像の処理方法や機材を特定したものではありません。</p>
<p>壊れているのは物の大きな形より、輪郭と細部のつながりです。短い横線が元の輪郭から離れ、白と黒が細い線片として近接します。この静止画だけでは、粒が更新される速度や点滅は判断できません。</p>
<p>{static_reference_note}</p></div>
<div class="wide"><div class="actions"><button id="pixel-zoom" aria-pressed="false">画像を原寸で見る</button></div><p class="note">原寸表示では画像の枠内を横へ動かして細い線を確認できます。</p>
<figure><div class="pixel-box"><img src="{uri(REFERENCE_IMAGE,'image/png')}" alt="加工感の参照スクリーンショット"></div><figcaption>参照。2340×1080。色相とキャラクターを除いて加工を比較。</figcaption></figure>
<div class="media-grid"><figure><div class="pixel-box"><img src="{uri(before,'image/png')}" alt="現行CRTの描画結果"></div><figcaption>追加前。曲面とガラスと既存CRT加工を含む固定入力。{html.escape(before.relative_to(OUT).as_posix())}</figcaption></figure>
<figure><div class="pixel-box"><img src="{uri(still_image,'image/png')}" alt="独立評価に渡した静止画の最終候補"></div><figcaption>{static_kind}。最終比較で{scores[0]:g}%。{html.escape(still_image.relative_to(OUT).as_posix())}</figcaption></figure></div></div>
<div class="wrap"><h2>追加した静止加工</h2><p>{static_method}</p>
<p>切替画面では現行の描画と追加後の静止画を比べられます。</p>
<p>{static_scope}</p>
</div>{executable_block}{static_material_block}{target_block}<div class="wrap">
<h2>動画は揺れを続けたまま外周から不均等に閉じる</h2>
<p>背景は傾きと位置を変え、画角が出入りします。中央の銀色の薄片は背景とは別に、平たく潰れたり細かい片が広がったりしながら向きを変えます。白い文字は中央の一行の中で左右へずれて重なり、途中から読めない文字へ変わります。</p>
<p>黒い塊は上と左右から先に入ります。床側は少し後まで残り、見えている背景が不規則な島に分かれて消えます。黒の中には赤い細長い断片と白い点が残り、それも消えて黒になります。黒を保持した後、別の景色が暗がりから現れます。</p>
<p>白い点には小さく鋭いものと大きくぼけたものがあります。異なる速度で動くため、手前と奥を感じます。試作では背景の揺れと侵食と粒子を別々に調整できます。</p>
<p>閉じる終盤では白い粒が下側へ流れるように見えます。近くの大きなぼけが細かい点と重なり、背景が消えてからも少し残ります。</p>
<div class="tblwrap"><table><thead><tr><th>元動画の時間</th><th>観察した変化</th></tr></thead><tbody>
<tr><td>0–2.6秒</td><td>微小な背景の揺れ。銀色の薄片の変形。輪郭の短い横乱れ。</td></tr>
<tr><td>2.6–5.3秒</td><td>外周の黒い塊と赤い細片が増え、背景の切り替わりと重なって減る。</td></tr>
<tr><td>5.7–11.3秒</td><td>文字が横へずれて重なる。読み取れる文章から壊れた文字列へ変わる。</td></tr>
<tr><td>11.3–12.7秒</td><td>上と左右が先に覆われる。床と背景の小さい島が遅れて消える。</td></tr>
<tr><td>12.7–13.25秒</td><td>黒地へ白い点と赤い断片が残り、最後にそれも消える。</td></tr>
<tr><td>13.25–13.82秒</td><td>中央の映像領域が黒を保持する。</td></tr>
<tr><td>13.82秒以後</td><td>別の景色が現れる。録画はその場面の途中で終わる。</td></tr>
</tbody></table></div></div>
<div class="wide"><div class="media-grid"><figure><video id="reference-video" controls playsinline preload="metadata" src="{uri(preview,'video/mp4')}"></video><figcaption>元動画の閲覧用コピー。原寸比を保った1170×540。音声あり。</figcaption></figure>
<figure><video id="candidate-video" controls playsinline preload="metadata" src="{uri(movie,'video/mp4')}"></video><figcaption>試作。カメラ{motion_input.upper()}の既存CRT描画へ画面の動きと効果を合成。音声なし。</figcaption></figure></div>
<div class="actions"><button id="play-both">先頭から両方を再生</button><button id="ending-both">11秒から閉じ方を比較</button><button id="pause-both">両方を止める</button></div><p id="play-status" role="status" class="note"></p>
<figure><img src="{uri(sheet,'image/jpeg')}" alt="11.8秒、12.4秒、12.8秒、13.6秒の参照と試作の比較"><figcaption>0.1秒刻みの時刻で抽出した比較。VFRの元動画は時刻の前後に最も近いコマを保持します。ラベルは実在するファイル名から作成。</figcaption></figure></div>
<div class="wrap"><h2>試作の範囲と確認できたこと</h2>
<p>試作は現行の室内スクリーン画像を使います。元動画にある遊具と廃墟と植物の写真、人物は描画しません。場面の内容が同じかは評価対象から外し、動きと閉じる順序を比較しました。</p>
<p>最終版では参照から効果用の素材を分離しています。文字は透明度の連番。銀色の薄片は無彩色の形。侵食は覆う強さの分布。粒は抽出した形を単色で再構成しました。背景の揺れと粒の移動は数値と計算で動かしています。背景の透視変形は見た目を合わせる追加処理です。実測した3D運動ではありません。同じ効果をA/Bの別画像へ合成して確認しました。</p>
<p>全素材を番号と寸法付きのチェック背景へ並べて確認しました。抽出時に混ざった人物の小片と薄片の先端は除外しました。文字の中央下端には一部欠損が残ります。薄片の外形と、効果の裏に隠れる侵食形状は推定です。これらは試作素材としての制約です。</p></div>
<div class="wide"><div class="media-grid">{effect_figures}</div></div><div class="wrap">
<p>{static_check}</p>
<p>動画はブラウザのWebGL描画から任意時刻を指定して書き出しました。同じ時刻へ戻ると同じPNGになり、閉鎖後の中央はRGBすべて0です。単一HTMLのA/B描画と専用サーバーの描画も一致しました。参照由来の素材と計算で作った部分は、選択した版の処理記録に分けて記載しています。</p>
<p>デスクトップのブラウザ実操作確認は、Computer Useが現在のURLを十分に特定できず停止しました。実画像と計算出力の検査は完了しています。Questでの性能や立体感は今回の試作の検証に含めていません。</p>
<h2>比較の履歴</h2><p>初回の静止画は48%でした。全面の細かい粒と灰色が残り、輪郭の横筋が均一すぎると指摘されました。初版を残して修正し、異なる評価担当へ新しい候補を渡しています。各回の点数は評価者が付けた値をそのまま記録しました。</p>
<details><summary>全比較の点数と最終評価の原文を開く</summary><div class="tblwrap"><table><thead><tr><th>評価記録</th><th>加工・演出の類似度</th></tr></thead><tbody>{''.join(audit_rows)}</tbody></table></div>
{audit_details}</details>
<h2>保存先と再現</h2><p class="paths">生成画像・動画・評価原文・単一HTMLは {html.escape(str(OUT))} に保存しています。ソースと再生成手順はリポジトリの tools/screen-effect-study-20261101/README.md にあります。</p>
<p class="note">入力の出所とSHA-256はmanifest.jsonに記録。本編への採用を表すユーザーの判定は作成していません。単一HTMLには比較画像と2本の動画を内蔵し、外部CDNとWebフォントを使いません。</p>
</div></main><script>
const videos=[document.getElementById('reference-video'),document.getElementById('candidate-video')];
document.getElementById('pixel-zoom').onclick=function(){{const native=this.getAttribute('aria-pressed')!=='true';this.setAttribute('aria-pressed',String(native));this.textContent=native?'画像を画面幅へ戻す':'画像を原寸で見る';document.querySelectorAll('.pixel-box').forEach(box=>box.classList.toggle('native-pixels',native));}};
let syncFrame=0,playRequest=0;
function stopSync(){{cancelAnimationFrame(syncFrame);syncFrame=0;}}
function followReference(){{const [reference,candidate]=videos;if(reference.paused||reference.ended){{stopSync();return;}}if(!candidate.paused&&!candidate.ended&&!candidate.seeking&&Math.abs(candidate.currentTime-reference.currentTime)>.08)candidate.currentTime=Math.min(reference.currentTime,candidate.duration);syncFrame=requestAnimationFrame(followReference);}}
async function playAt(time){{const request=++playRequest;stopSync();try{{await Promise.all(videos.map(video=>video.error?Promise.reject(video.error):video.readyState?Promise.resolve():new Promise((resolve,reject)=>{{video.addEventListener('loadedmetadata',resolve,{{once:true}});video.addEventListener('error',reject,{{once:true}});}})));if(request!==playRequest)return;for(const video of videos){{video.pause();video.currentTime=time;}}const results=await Promise.allSettled(videos.map(v=>v.play()));if(request!==playRequest)return;const rejected=results.some(r=>r.status==='rejected');if(!rejected){{videos[1].currentTime=videos[0].currentTime;followReference();}}document.getElementById('play-status').textContent=rejected?'再生できない動画は各プレイヤーの再生ボタンを押してください。':'同じ時刻から再生しています。参照動画に合わせて同期ずれを補正します。';}}catch{{document.getElementById('play-status').textContent='動画を読み込めません。元のMP4を確認してください。';}}}}
document.getElementById('play-both').onclick=()=>playAt(0);document.getElementById('ending-both').onclick=()=>playAt(11);document.getElementById('pause-both').onclick=()=>{{++playRequest;stopSync();videos.forEach(v=>v.pause());}};
</script></body></html>'''
    atomic_text(report_path, document)
    inputs = [REFERENCE_IMAGE, REFERENCE_VIDEO, baseline_source, before, still_image, still_process, movie, still_audit, motion_audit,
              target_image, OUT / "target-study/prompt.txt", motion_release_path, still_preview, motion_preview,
              OUT / "report-reference-manifest.json", sheet, preview, CSS] + material_files + generation_files
    manifest = {"date": "2026-11-01", "completion_threshold": 85, "complete": complete, "draft": draft, "selected_scores": {"still": scores[0], "motion": scores[1]},
                "scores_are_subjective": True, "integrated_into_production": False,
                "selected_motion_input": motion_input,
                "report": {"path": report_path.name, "sha256": sha(report_path), "bytes": report_path.stat().st_size},
                "selected_still_kind": "fixed_input_composite" if processing.get("round") == 34 else "reference_microtexture_transfer" if processing.get("round") in (32, 33) else processing.get("kind", "computed_filter"),
                "legacy_still_filter_highest_similarity": 83 if processing.get("round") in (32, 33, 34) else None,
                "executable_still_filter_highest_similarity": 83 if image_edit or processing.get("round") == 34 else scores[0],
                "image_generation": {"mode": "built-in image_gen", "selected_still_is_image_edit": image_edit,
                                     "selected_prompt": str(still_process.parent / "prompt.txt") if image_edit else None,
                                     "exploratory_target": {"excluded_from_pass_candidate": True, "prompt": "target-study/prompt.txt"}},
                "files": [{"path": str(p), "sha256": sha(p), "bytes": p.stat().st_size} for p in inputs],
                "movie_probe": movie_probe,
                "selected_still_processing": processing,
                "selected_still_material": material_manifest,
                "selected_motion_release": motion_release,
                "sources": [{"path": str(p.relative_to(HERE)), "sha256": sha(p)} for p in sorted(HERE.rglob("*")) if p.is_file() and "__pycache__" not in p.parts]}
    atomic_text(OUT / ("manifest-progress.json" if draft else "manifest.json"), json.dumps(manifest, ensure_ascii=False, indent=2))
    print(json.dumps({"report": str(report_path), "bytes": len(document.encode("utf-8")), "scores": scores, "complete": complete}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--still-base", required=True, type=Path)
    parser.add_argument("--still-image", required=True, type=Path)
    parser.add_argument("--still-preview", required=True, type=Path)
    parser.add_argument("--still-process", required=True, type=Path)
    parser.add_argument("--still-audit", required=True, type=Path)
    parser.add_argument("--motion-audit", required=True, type=Path)
    parser.add_argument("--motion-dir", required=True, type=Path)
    parser.add_argument("--motion-input", choices=["a", "b"], default="a")
    parser.add_argument("--still-atlas-dir", type=Path)
    parser.add_argument("--draft", action="store_true", help="Write an explicitly incomplete progress report without declaring completion")
    args = parser.parse_args()
    build(args.still_base, args.still_image, args.still_preview, args.still_process, args.still_audit, args.motion_dir, args.motion_audit, args.motion_input, args.still_atlas_dir, args.draft)
