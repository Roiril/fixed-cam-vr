"""Build a self-contained report from the Unity screen-study screenshots."""
from __future__ import annotations

import argparse
import base64
import hashlib
import html
import json
from pathlib import Path
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_screen-study")
DEFAULT_REFERENCE = ROOT / ".codex-remote-attachments/01a18d24-f13f-7a60-944d-465ecdf72b3c/3e8af372-49f7-4a9c-885b-6398589f5d49/1-1000011333.jpg"
CSS = Path("C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css")
NAMES = ["現行", "① 控えめ・枠なし", "② 控えめ・枠あり", "③ 参照寄り・枠なし", "④ 参照寄り・枠あり"]
DESCRIPTIONS = [
    "本編のシェーダと曲面メッシュで描画した基準。映像だけが闇に浮かびます。",
    "「丸いガラス越しに部屋を見る」。輪郭を少しだけ柔らかくし、色ずれと縁の反射を控えめに加えます。",
    "①に暗い筐体を追加。ガラスより外に段差のある縁が見えます。映像の縦横寸法は①の90%。",
    "「古いテレビのガラスに部屋が映る」。色ずれと光のにじみを強め、ガラスの縁を暗くします。",
    "③に暗い筐体を追加。参照の「筐体・内側の段差・ガラス」の順を試した案。映像の縦横寸法は③の90%。",
]


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def data(path: Path) -> str:
    mime = "image/jpeg" if path.suffix.lower() in (".jpg", ".jpeg") else "image/png"
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode("ascii")


def atomic(path: Path, content: str) -> None:
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(content, encoding="utf-8", newline="\n")
    temp.replace(path)


def build(reference: Path) -> None:
    manifest = json.loads((OUT / "manifest.json").read_text(encoding="utf-8-sig"))
    capture_path = ROOT / "tools/web-compositor/recordings/approach-manifest.json"
    capture_log = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    captured = {k: v for k, v in capture_log.items() if v.get("kind") == "onsite-plate"}
    assert reference.is_file(), reference
    assert len(manifest["sources"]) == 3
    expected = {(c, str(v), "front") for c in "ABC" for v in range(5)}
    expected |= {("A", str(v), "oblique") for v in range(5)}
    actual = {(s["camera"], s["variant"], s["view"]) for s in manifest["shots"]}
    assert actual == expected and len(manifest["shots"]) == 20, (expected - actual, actual - expected)
    pictures = {}
    measurements = []
    for source in manifest["sources"]:
        path = OUT / source["copy"]
        assert sha(path) == source["sha256"] == sha(Path(source["path"]))
        record = captured["/captures/" + path.name]
        assert record["sha256"] == source["sha256"] and record["capturedAt"] == source["capturedAt"]
        newest = max((v for v in captured.values() if v["cameraId"] == source["camera"]), key=lambda v: v["capturedAt"])
        assert newest["name"] == path.name, "A newer empty plate exists; update the source set deliberately"
        assert Image.open(path).size == (640, 480)
    for shot in manifest["shots"]:
        key = (shot["camera"], shot["variant"], shot["view"])
        path = OUT / shot["file"]
        assert path.is_file() and sha(path) == shot["sha256"], path
        with Image.open(path) as im:
            assert im.size == (1920, 1080), (path, im.size)
            gray = im.convert("L")
            bright = sum(gray.histogram()[12:])
            assert bright > im.width * im.height * 0.05, f"empty or too dark: {path}"
            pixels = im.convert("RGB")
            magenta = sum(1 for r, g, b in pixels.resize((192, 108)).get_flattened_data() if r > 180 and b > 180 and g < 60)
            assert magenta < 100, f"shader error pink: {path}"
            pictures[key] = pixels.copy()
            baseline = OUT / f"screenshots/{shot['camera']}_0_{shot['view']}.png"
            difference = ImageStat.Stat(ImageChops.difference(pixels, Image.open(baseline).convert("RGB"))).mean
            measurements.append({"file": shot["file"], "mean_luma_8bit": round(ImageStat.Stat(gray).mean[0], 3),
                                 "bright_pixels": bright, "mean_rgb_difference_from_current": [round(x, 3) for x in difference]})
    # Every variant must actually differ; unlike fallback images, missing evidence fails above.
    for camera in "ABC":
        assert len({sha(OUT / f"screenshots/{camera}_{v}_front.png") for v in range(5)}) == 5
    font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 22)
    montage = Image.new("RGB", (1600, 5 * 343), "white")
    draw = ImageDraw.Draw(montage)
    for variant in range(5):
        for camera_index, camera in enumerate("ABC"):
            tile = pictures[camera, str(variant), "front"].resize((528, 297), Image.Resampling.LANCZOS)
            x, y = camera_index * 536, variant * 343
            montage.paste(tile, (x, y))
            draw.text((x + 8, y + 301), f"{camera} / {'CURRENT' if variant == 0 else 'VARIANT ' + str(variant)}", font=font, fill="black")
    montage.save(OUT / "all-patterns.png")
    ref_copy = OUT / "sources/reference.jpg"
    ref_copy.write_bytes(reference.read_bytes())
    validation = {"screenshot_count": 20, "source_count": 3, "source_hashes_match": True,
                  "latest_capture_records_match": True,
                  "all_dimensions": "1920x1080", "all_patterns_distinct": True, "measurements": measurements,
                  "reference_sha256": sha(reference), "render_manifest_sha256": sha(OUT / "manifest.json")}
    validation["render_source_hashes"] = {str(p.relative_to(ROOT)): sha(p) for p in [
        ROOT / "Assets/Art/Shaders/Streaming/ScreenComposite.shader",
        ROOT / "Assets/Scripts/Streaming/CrtScreenMesh.cs",
        ROOT / "Assets/Scripts/Streaming/Editor/ScreenStudyPreview.cs",
        ROOT / "Assets/Scripts/Streaming/Editor/ScreenStudySurface.shader",
    ]}
    atomic(OUT / "validation.json", json.dumps(validation, ensure_ascii=False, indent=2))

    def image_tag(camera: str, variant: int, view: str = "front") -> str:
        shot = next(s for s in manifest["shots"] if (s["camera"], s["variant"], s["view"]) == (camera, str(variant), view))
        key = f"{camera}-{variant}-{view}"
        alt = f"カメラ{camera} {NAMES[variant]} {'正面' if view == 'front' else '斜め'}"
        return f'<img id="img-{key}" data-camera="{camera}" data-variant="{variant}" data-view="{view}" src="{data(OUT / shot["file"])}" width="1920" height="1080" alt="{alt}" loading="lazy" tabindex="0">'

    galleries = ""
    for camera in "ABC":
        galleries += f'<section class="wide" aria-labelledby="camera-{camera}"><h2 id="camera-{camera}">カメラ{camera}：現行と4案</h2><div class="gallery">'
        for variant in range(5):
            galleries += f'<figure class="{"baseline" if variant == 0 else ""}">{image_tag(camera, variant)}<figcaption><b>{NAMES[variant]}</b>　{DESCRIPTIONS[variant]}</figcaption></figure>'
        galleries += "</div></section>"
    oblique = '<section class="wide"><h2>斜めから見るとき</h2><p class="muted">カメラA。視点を右へ0.75m・上へ0.12m移動。曲面の膨らみは全案共通です。</p><div class="gallery">'
    for variant in range(5):
        oblique += f'<figure>{image_tag("A", variant, "oblique")}<figcaption>{NAMES[variant]}</figcaption></figure>'
    oblique += "</div></section>"
    rows = "".join(f'<tr><td>{NAMES[v]}</td><td>{"本編値" if v == 0 else "0.32" if v < 3 else "1.00"}</td><td>{"あり" if v in (2,4) else "なし"}</td><td>{"90% / 81%" if v in (2,4) else "100% / 100%"}</td></tr>' for v in range(5))
    source_rows = "".join(f'<tr><td>{s["camera"]}</td><td>{s["capturedAt"].replace("T", " ")}</td><td><code>{s["copy"].split("/")[-1]}</code></td></tr>' for s in manifest["sources"])
    css = CSS.read_text(encoding="utf-8-sig")
    extra_css = """
    .intro { padding-bottom:var(--sp-8) } .section-copy { padding-top:var(--sp-8);padding-bottom:var(--sp-8) }
    .gallery { display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:var(--sp-8) var(--sp-4) }
    .gallery figure { margin:0;min-width:0 } .gallery .baseline { grid-column:1/-1;max-width:48rem }
    .gallery img { width:100%;cursor:zoom-in } figcaption b { color:var(--ink) }
    .reference { display:grid;grid-template-columns:1fr 1fr;gap:var(--sp-6);align-items:start }
    .controls { display:flex;gap:var(--sp-4);flex-wrap:wrap;align-items:end;margin:var(--sp-4) 0 }
    label { display:flex;flex-direction:column;gap:var(--sp-1);font-size:var(--fs-sm) }
    select,button { font:inherit;color:var(--ink);background:var(--surface);border:1px solid var(--line);border-radius:0;padding:var(--sp-2) var(--sp-3) }
    button { cursor:pointer } select:focus-visible,button:focus-visible,input:focus-visible { outline:2px solid var(--link);outline-offset:2px }
    .comparison { position:relative;aspect-ratio:16/9;background:var(--ink);overflow:hidden;touch-action:pan-y }
    .comparison img { position:absolute;inset:0;width:100%;height:100%;object-fit:contain }
    .comparison .candidate { clip-path:inset(0 0 0 50%) }
    .comparison .divider { position:absolute;left:50%;top:0;bottom:0;border-left:2px solid var(--paper);pointer-events:none }
    input[type=range] { width:100%;accent-color:var(--link);min-height:var(--sp-8) }
    .split-labels { display:flex;justify-content:space-between;font-size:var(--fs-sm);gap:var(--sp-4) }
    dialog { border:0;padding:var(--sp-4);background:var(--paper);color:var(--ink);width:min(98vw,120rem);max-height:96vh }
    dialog::backdrop { background:var(--ink);opacity:.85 } dialog img { display:block;max-width:100%;max-height:80vh;object-fit:contain;margin:auto }
    .dialog-head { display:flex;justify-content:space-between;gap:var(--sp-4);align-items:center;margin-bottom:var(--sp-3) }
    .file { overflow-wrap:anywhere } .two { display:grid;grid-template-columns:1fr 1fr;gap:var(--sp-6) }
    @media(max-width:40rem) { .reference,.two { grid-template-columns:1fr } .gallery { grid-template-columns:1fr } .controls label { flex:1;min-width:8rem } select { max-width:100% } }
    @media print { .controls,.comparison,.split-labels,input,dialog { display:none } .gallery { grid-template-columns:1fr 1fr } }
    """
    page = f'''<!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>メインスクリーン：現行とガラス・筐体の4案</title><style>{css}{extra_css}</style></head><body>
    <header class="wrap intro"><h1>メインスクリーン<br>現行と4案の比較</h1><p class="meta">2026-10-30 ／ 廻リ視 ／ 比較案・未採用</p>
    <p class="lead">参照画像を「丸いガラス越しに部屋が見える画面」と読みました。ガラスの表現を2段階に分け、筐体の有無を掛け合わせた4案です。</p>
    <p>最新の無人写真A・B・Cを使用。現行を含む正面15枚と、斜めからの5枚を同条件で描画しました。時刻表示・文字・CGを除いた映像面の比較です。画像を押すと拡大できます。</p>
    <p><a href="#compare">現行と重ねて比較</a>　<a href="#camera-A">全パターンを見る</a>　<a href="#conditions">再現条件</a></p></header>
    <section class="wide"><h2>参照画像のスクリーンを言葉にする</h2><div class="reference"><figure><img src="{data(reference)}" width="1280" height="1030" alt="ユーザー提示の黒い筐体と丸みのあるガラスのスクリーン"><figcaption>ユーザー提示の参照。室内の内容ではなく、画面の形と見え方を読み取ります。</figcaption></figure>
    <div><h3>画像から見えること</h3><ul>
    <li>黒に近い厚い筐体。外周と映像の間に段差があり、ガラスが奥に収まっています。</li>
    <li>四隅は大きく丸まり、上下の辺は少し弓なりです。縁の明るい反射がガラスの厚みを感じさせます。</li>
    <li>縁ほど映像が暗くなります。中央の明るい襖や照明は明るさを残しています。</li>
    <li>輪郭は柔らかく、明暗の境目に赤・青の色ずれがあります。明部には薄いにじみがあります。</li>
    <li>目立つ走査線や砂嵐は見えません。時刻や操作の表示もありません。</li></ul>
    <p class="muted">参照は静止画です。ちらつき・走査の動きは判断できません。部屋の広角レンズによる歪みと、管の曲面による歪みも分離できません。</p></div></div></section>
    <section class="wrap section-copy"><h2>2つの言語化から4案を作る</h2>
    <p>控えめな版は「丸いガラス越しに部屋を見る」。参照寄りの版は「古いテレビのガラスに部屋が映る」。後者は色ずれと光のにじみを強め、縁の暗さと反射を見えるようにしています。</p>
    <div class="tblwrap"><table><thead><tr><th>案</th><th>ガラス加工の強さ</th><th>筐体</th><th>映像の寸法 / 面積</th></tr></thead><tbody>{rows}</tbody></table></div>
    <p>枠ありは映像を切り抜かず、縦横を同率で90%に縮めて収めます。面積は81%。露出と色調の設定は同じです。色ずれ・にじみ・縁の暗さによる局所的な輝度差は生じます。</p></section>
    <section class="wide" id="compare"><h2>現行と重ねて比較</h2><p>スライダーの左が現行。右が比較案です。同じ場所の細部と、枠による縮み方を見られます。</p>
    <div class="controls"><label>無人写真<select id="camera"><option>A</option><option>B</option><option>C</option></select></label><label>比較案<select id="variant">{''.join(f'<option value="{v}" {"selected" if v == 4 else ""}>{NAMES[v]}</option>' for v in range(1,5))}</select></label><label>視点<select id="view"><option value="front">正面</option><option value="oblique">斜め（Aのみ）</option></select></label></div>
    <div class="split-labels"><span>左：現行</span><span id="candidate-label">右：④ 参照寄り・枠あり</span></div><div class="comparison"><img id="before" alt="現行スクリーン"><img id="after" class="candidate" alt="比較案のスクリーン"><div class="divider" aria-hidden="true"></div></div><label for="split">比較位置<input type="range" id="split" min="0" max="100" value="50"></label></section>
    {galleries}{oblique}
    <section class="wrap section-copy"><h2>今回の比較で見るところ</h2><ul>
    <li>①と③は、筐体を加えずにガラスの印象だけを変える案です。参照の厚い外枠の印象は再現しません。</li>
    <li>②と④は、映像を見る装置の外形が現れます。その分、同じ大きさのスクリーンでは部屋の像が小さくなります。</li>
    <li>③と④ではカーテンやパイプの輪郭の色ずれ、画面上端の反射を確認できます。縁の暗さで室内の情報を失いすぎていないかも比較点です。</li>
    <li>参照の筐体まで含めた候補は④です。枠が体験に合うかは未判定です。</li></ul>
    <p>参照は約4:3の装置です。今回は本編の16:9を共通条件として残しました。4:3の写真は引き伸ばさず、左右に黒い余白を残しています。そのため筐体の横長さと黒帯は参照と異なります。</p>
    <p>強弱の差は、枠の有無の差より小さくなりました。一覧の縮小画像では差が読みにくいため、①と③、②と④は拡大して見比べる必要があります。採否が決まるまでは現行を維持します。</p>
    <h2 id="conditions">撮影元と再現条件</h2><p>最新の撮影記録は2026年9月24日17:22のセット。記録された撮影日時で選び、ファイル更新日時は使っていません。無人画像は3枚とも640×480です。</p>
    <div class="tblwrap"><table><thead><tr><th>写真</th><th>記録された撮影日時</th><th>ファイル</th></tr></thead><tbody>{source_rows}</tbody></table></div>
    <p>日時の根拠は <code>recordings/approach-manifest.json</code> の <code>kind=onsite-plate</code> と <code>capturedAt</code>。JPEGにEXIFはなく、取得後にPCが記録した時刻です。3枚とも現行 <code>show.json</code> に採用されている画像とSHA-256が一致します。</p>
    <dl class="qa"><dt>現行の描画</dt><dd>Unity {html.escape(manifest['unity'])} / {html.escape(manifest['graphics'])} / {html.escape(manifest['colorSpace'])}。実シェーダ ScreenComposite と実 CrtScreenMesh。比較時点の画面設定で、1-A相当の加工強度0・自動露出の収束値・入力フレーム0。</dd>
    <dt>共通の画面と撮影</dt><dd>外形16:9。幅2.3704m・高さ1.3333m。曲面の膨らみ0.055。正面の距離2.6m・垂直画角38°。出力1920×1080。全案で同じカメラと曲面を使用。</dd>
    <dt>共通の色加工</dt><dd>露出 {manifest['post']['exposure']:.2f} EV。コントラスト {manifest['post']['contrast']:.2f}。彩度 {manifest['post']['saturation']:.2f}。色温度 {manifest['post']['temperature']:.2f}。ビネット {manifest['post']['vignette']:.2f}。新しい走査線・時間ノイズは追加していません。</dd>
    <dt>提案の描画</dt><dd>本編と同じ色加工・CameraFeelを通した画像に、比較専用のガラスと筐体を描画。枠の反射はこの案の表現です。物理的な反射の計測ではありません。</dd>
    <dt>このレポートで未確認のこと</dt><dd>Quest実機のスクショではなく、Unity Editorの比較描画です。両眼の見え方・追従・時間変化・実機の処理負荷・怖さは未検証。無人写真なので、人形の顔や着物の読め方も判定していません。本編への採用は行っていません。</dd></dl>
    <h2>保存した証拠</h2><p>このHTMLは画像を内蔵しており、単体で開けます。同じフォルダに元写真・全20枚のPNG・全15パターンの一覧画像・描画条件を保存しています。</p>
    <p class="file"><code>{html.escape(str(OUT))}</code></p><p>検証：20枚すべて1920×1080。欠損なし。3枚の元写真のハッシュ一致。各カメラの5パターンは全て異なる画像です。</p>
    <p class="note">未採用案 / 2026-10-30 / シュビー。画像の特徴と言語化の対応を比べる資料です。スクリーンを採用するかの判定は含めていません。</p></section>
    <dialog id="zoom"><div class="dialog-head"><span id="zoom-label"></span><button id="close">閉じる</button></div><img id="zoom-image" alt=""></dialog>
    <script>
    const names={json.dumps(NAMES,ensure_ascii=False)};
    const camera=document.getElementById('camera'),variant=document.getElementById('variant'),view=document.getElementById('view');
    function update(){{if(camera.value!=='A')view.value='front';view.options[1].disabled=camera.value!=='A';const key=camera.value+'-';const suffix='-'+view.value;document.getElementById('before').src=document.getElementById('img-'+key+'0'+suffix).src;document.getElementById('after').src=document.getElementById('img-'+key+variant.value+suffix).src;document.getElementById('candidate-label').textContent='右：'+names[Number(variant.value)];}}
    for(const control of [camera,variant,view])control.addEventListener('change',update);
    document.getElementById('split').addEventListener('input',e=>{{document.getElementById('after').style.clipPath='inset(0 0 0 '+e.target.value+'%)';document.querySelector('.divider').style.left=e.target.value+'%';}});
    const zoom=document.getElementById('zoom');
    function openImage(img){{const target=document.getElementById('zoom-image');target.src=img.src;target.alt=img.alt;document.getElementById('zoom-label').textContent=img.alt;zoom.showModal();}}
    for(const img of document.querySelectorAll('.gallery img')){{img.addEventListener('click',()=>openImage(img));img.addEventListener('keydown',e=>{{if(e.key==='Enter'||e.key===' '){{e.preventDefault();openImage(img);}}}});}}
    document.getElementById('close').addEventListener('click',()=>zoom.close());zoom.addEventListener('click',e=>{{if(e.target===zoom)zoom.close();}});update();
    </script></body></html>'''
    assert '<script src=' not in page and 'src="http' not in page
    atomic(OUT / "index.html", page)
    print(json.dumps({"report": str(OUT / "index.html"), "screenshots": len(manifest["shots"]),
                      "html_bytes": (OUT / "index.html").stat().st_size, "validation": str(OUT / "validation.json")}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", type=Path, default=DEFAULT_REFERENCE)
    args = parser.parse_args()
    build(args.reference)
