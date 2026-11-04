"""Build the self-contained image comparison report. Missing evidence is fatal."""
from pathlib import Path
import base64
import hashlib
import html
import json
import sys
from datetime import datetime
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
OLD = OUT.parent / "2026-10-30_screen-study/screenshots"
CSS = Path("C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css")
library, entries, rounds, old_entries, extra_entries, ablation_entries = [], [], [], [], [], []
seen = {}

def read_json(p):
    return json.loads(p.read_text(encoding="utf-8-sig"))

def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def image(p, label):
    assert p.is_file(), p
    digest = sha(p)
    if digest in seen:
        return seen[digest]
    ident = "asset-" + str(len(library))
    with Image.open(p) as im:
        size = list(im.size)
    mime = "image/jpeg" if p.suffix.lower() == ".jpg" else "image/png"
    library.append({"id": ident, "name": p.name, "label": label, "hash": digest,
                    "size": size, "uri": f"data:{mime};base64," + base64.b64encode(p.read_bytes()).decode("ascii")})
    seen[digest] = ident
    return ident

reference = image(ROOT / "Assets/Resources/CrtReference/CaseColor.jpg", "ユーザーの参照")
for folder in sorted(OUT.glob("round-[0-9][0-9]")):
    j = read_json(folder / "judgment.json")
    m = read_json(folder / "manifest.json")
    capture = next(c for c in m["captures"] if c["file"] == "A_front.png")
    assert sha(folder / "02.png") == capture["sha256"], folder
    assert sha(folder / "01.jpg") == library[0]["hash"], folder
    key = image(folder / "02.png", folder.name + " / 実描画")
    feedback = j.get("feedback", j.get("feedback_summary", [j.get("verbatim", "")]))
    entry = {"key": key, "label": folder.name + " / " + str(j["score"]) + "%",
             "score": j["score"], "feedback": feedback, "response": j.get("response", ""), "group": "画像から作った版"}
    rounds.append(entry)
    entries.append(entry)
assert rounds
manifest = read_json(OUT / "manifest.json")
current_hash = next(c for c in manifest["captures"] if c["file"] == "A_front.png")["sha256"]
current_key = next(a["id"] for a in library if a["hash"] == current_hash)
current_index = next(i for i, e in enumerate(rounds) if e["key"] == current_key)
latest = rounds[current_index]
calibration = read_json(OUT / "calibration.json")
coordinates = read_json(OUT / "coordinate-validation.json")
build = read_json(OUT / "build-validation.json")
current_build = (build["shader_sha256"].lower() == manifest["shaderHash"].lower()
                 and build["runtime_profile_sha256"].lower() == manifest["runtimeProfileHash"].lower())
control = calibration["same_material_pair"]
control_entries = []
for relative in control["images"]:
    key = image(OUT / relative, "材質同値・入力だけ変更 / " + relative)
    e = {"key": key, "label": relative, "group": "判定方法の校正用対照", "feedback": []}
    entries.append(e); control_entries.append(e)
study = OUT / "ablation-study-01"
study_manifest = read_json(study / "manifest.json")
assert len(study_manifest["ablations"]) == 12
for row in study_manifest["ablations"]:
    folder = study / Path(row["file"]).stem
    j = read_json(folder / "judgment.json")
    assert sha(folder / "02.png") == row["sha256"]
    assert sha(folder / "01.jpg") == library[0]["hash"]
    key = image(folder / "02.png", "減光の除去比較 / " + folder.name)
    e = {"key": key, "label": folder.name + " / " + str(j["score"]) + "%", "score": j["score"],
         "feedback": j["feedback"], "group": "周辺減光と明暗fieldの除去比較"}
    entries.append(e); ablation_entries.append(e)
for c in manifest["captures"]:
    p = OUT / c["file"]
    assert sha(p) == c["sha256"], p
    if p.name == "A_front.png":
        continue
    key = image(p, "現在の " + p.name)
    e = {"key": key, "label": p.name, "group": "別の無人写真・夜間・反射", "feedback": []}
    entries.append(e); extra_entries.append(e)
for camera in "ABC":
    for variant in range(5):
        for angle in (["front", "oblique"] if camera == "A" else ["front"]):
            p = OLD / f"{camera}_{variant}_{angle}.png"
            key = image(p, f"旧比較 {camera} / {variant} / {angle}")
            e = {"key": key, "label": f"旧比較 {camera} / {variant} / {angle}", "group": "旧版と却下された4案", "feedback": []}
            entries.append(e); old_entries.append(e)
parts = []
for name, description in [
    ("CaseColor.jpg", "元参照の枠RGB。ファイルのバイトは同じ"),
    ("Frame.png", "画像生成で抜いた開口alpha"),
    ("Reflectance.png", "画像生成の反射強度"),
    ("Glass.png", "画像生成のガラス透過むら"),
    ("Normal.png", "画像生成のガラス法線"),
    ("Grade.png", "画像から数値で作った1D明暗曲線"),
    ("Palette.png", "中立色差を推定した実験素材。本編から撤去済み")]:
    part_path = (OUT / "experimental-assets/Palette.png") if name == "Palette.png" else ROOT / "Assets/Resources/CrtReference" / name
    key = image(part_path, description)
    with Image.open(part_path) as part:
        dimension = f"{part.width} × {part.height}"
    parts.append({"key": key, "label": name + " / " + dimension, "description": description})

def esc(s): return html.escape(str(s))
def photo(entry):
    return f'<figure><img data-source="{entry["key"]}" alt="{esc(entry["label"])}" loading="lazy"><figcaption>{esc(entry["label"])}</figcaption></figure>'

options = []
for group in dict.fromkeys(e["group"] for e in entries):
    options.append('<optgroup label="' + esc(group) + '">')
    for index, e in enumerate(entries):
        if e["group"] == group:
            options.append(f'<option value="{index}">{esc(e["label"])}</option>')
    options.append('</optgroup>')
rows = ''.join(f'<tr><td><button data-round="{i}">{esc(e["label"])}</button></td><td>{e["score"]}%</td><td>{esc(e["feedback"][0])}</td></tr>' for i, e in enumerate(rounds))
ablation_rows = ''.join('<tr><td>'+camera+'</td>'+''.join('<td>'+str(next(e['score'] for e in ablation_entries if e['label'].startswith(f'{camera}_v{v}_f{f}')))+'%</td>' for v, f in [(1,1),(0,1),(1,0),(0,0)])+'</tr>' for camera in 'ABC')
state = "画像比較の条件を通過" if latest["score"] >= 96 else "96%以上の完了条件は未達"
score_text = " → ".join(str(e["score"]) for e in rounds)
css = CSS.read_text(encoding="utf-8-sig")
data = {"entries": entries, "library": [{k:v for k,v in a.items() if k != "uri"} for a in library], "latest": current_index}
payload = json.dumps(data, ensure_ascii=False).replace("</", "<\\/")
doc = f'''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>参照画像から作ったCRTスクリーンの比較</title><style>{css}
.comparison{{display:grid;grid-template-columns:1fr 1fr;gap:1rem}} img{{max-width:100%;height:auto;display:block;cursor:zoom-in}} figure{{margin:0}} figcaption{{font-size:var(--fs-sm);color:var(--mute);margin-top:.5rem;overflow-wrap:anywhere}} select,button{{font:inherit;color:var(--ink);background:var(--paper);border:1px solid var(--line);padding:.45rem .7rem}} select{{max-width:100%;width:100%}} button{{cursor:pointer}} .photos{{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,20rem),1fr));gap:1.5rem}} .comparison figure img{{width:100%}} .controls{{display:flex;gap:1rem;align-items:center;flex-wrap:wrap;margin:1rem 0}} .controls label{{flex:1;min-width:min(100%,18rem)}} .feedback{{min-height:4rem}} details{{margin:2rem 0}} dialog{{max-width:96vw;max-height:96vh;background:var(--paper);color:var(--ink);border:1px solid var(--line)}} dialog img{{max-height:85vh;object-fit:contain}} .library{{display:none}} code{{overflow-wrap:anywhere}} @media(max-width:650px){{.comparison{{grid-template-columns:1fr}}}} </style>
<main><div class="wrap"><h1>参照画像から作ったCRTスクリーンの比較</h1><p class="meta">{datetime.now():%Y-%m-%d} ／ 廻リ視 ／ Unity実描画</p>
<p class="lead">現在の版は{latest["label"]}です。全反復の初回判定の最高は{max(e["score"] for e in rounds)}%。<em>{state}です。</em>枠は元写真の画素へ替え、映像は本番シェーダで描いています。</p>
<p>本編28版と減光比較の追加11枚、計39枚を独立判定へ出しました。「家具とか部屋ではなくそれ以外」に映像のぼけ・色ずれ・明暗も含む意図かを確認中です。現在はそこまで含めて評価しています。96%の条件は維持しています。</p>
<p>各回、新しい評価者へ参照と実描画の2枚だけを渡しました。室内と家具の違いを除外するよう依頼しています。過去の点数と目標点は伝えていません。点数は評価者の主観で、測定精度を表すものではありません。</p>
<p>記録した点数は {score_text}%。採点の低かった回も残しています。</p>
<h2>参照と各版を並べる</h2><div class="controls"><label>比較する画像<select id="version">{''.join(options)}</select></label><button id="previous">前</button><button id="next">次</button></div></div>
<div class="wide" id="compare"><div class="comparison"><figure><img data-source="{reference}" alt="ユーザーの参照画像"><figcaption>参照 / 1280 × 1030</figcaption></figure><figure><img id="active" alt="選択した実描画"><figcaption id="active-caption"></figcaption></figure></div></div>
<div class="wrap"><div class="feedback"><h3 id="score"></h3><ul id="feedback"></ul><p id="response"></p></div>
<h2>全反復の評価</h2><div class="tblwrap"><table><thead><tr><th>実描画</th><th>類似度</th><th>判定の冒頭</th></tr></thead><tbody>{rows}</tbody></table></div>
<h2>周辺減光と追加した明暗補正を外した比較</h2><p>既存ビネットと元参照の低周波RGB fieldの有無だけを替え、A・B・Cで4条件を描きました。他の材質パラメータはハッシュで同値を確認しています。Aの両方ありはround-27と同じ画なので、判定を再利用しました。除去によって全入力が同じ方向へ改善する証拠は得られませんでした。</p>
<div class="tblwrap"><table><thead><tr><th>入力</th><th>両方あり</th><th>ビネットなし</th><th>fieldなし</th><th>両方なし</th></tr></thead><tbody>{ablation_rows}</tbody></table></div>
<p>確認用の現行実装からは、試験用のPaletteとcore配分を撤去しました。局所照明の正規化も撤去しています。通常時のfieldは0です。参照の室内照明を別室へ固定する処理を採用しないためで、点数が最高の版を選ぶ操作ではありません。</p></div>
<div class="wide photos">{''.join(photo(e) for e in ablation_entries)}</div><div class="wrap">
<h2>点数がどれだけ変動するか</h2><p>同じround-22の2枚を別の新規評価者3人へ渡すと79%・82%・86%でした。元の88%を含めると79〜88%です。小さな点差だけでは、加工が改善したかを区別できません。</p>
<p>下の2枚は22の設定で材質の全パラメータを同値にし、入力写真だけを替えた対照です。入力の解像度と形式も640×480 RGB24で一致します。評価は{control["score"]}%でした。評価者は明部のにじみと細部の硬さの違いを挙げ、被写体の影響もあるため加工設定の違いとは断定できないと記しました。</p>
<p>これらは比較方法の校正です。96%の条件を変更したり、点数を補正したりするためには使いません。材質の同値はLiveTex以外の全shader propertyのハッシュで検査しました。</p></div>
<div class="wide photos">{''.join(photo(e) for e in control_entries)}</div><div class="wrap">
<h2>別の写真でも同じ材質を使う</h2><p>Aだけで調整した明暗曲線をBとCにも適用しています。下の夜間モードと反射の確認も同じ本番シェーダの出力です。Quest実機は未接続のため、実機での見え方は未検証です。</p></div>
<div class="wide photos">{''.join(photo(e) for e in extra_entries)}</div>
<div class="wrap"><h2>素材を分けて確認する</h2><p>元参照はCaseColorとして使い、生成したFrameは開口のalphaだけを使います。ガラスの透過と反射は合成した映像へ掛けます。最終alphaは1です。</p></div>
<div class="wide photos">{''.join('<article>'+photo(p) + '<p>' + esc(p['description']) + '</p></article>' for p in parts)}</div>
<div class="wrap"><h2>旧版と却下された4案</h2><p>先行した4案はユーザーが却下しました。0は変更前です。下の20枚は当時の実描画で、今回の達成判定には使っていません。</p></div>
<div class="wide"><details><summary>旧版・4案の全20枚を開く</summary><div class="photos">{''.join(photo(e) for e in old_entries)}</div></details></div>
<div class="wrap"><h2>入力と検証の境界</h2><p>無人写真は2026-09-24の現場保存分です。Aの保存は17:22:09です。Bは17:22:10です。Cは17:22:11です。ファイルのSHA-256を各実描画のmanifestへ残しています。</p>
<p>物理筐体の比率は1280:1030です。CGとマスクの著作座標は16:9を保ち、表示の入口で同じ開口へ写します。4:3ソースの左右の黒帯は画面に出しません。</p>
<p>画像生成の完成予想図を実描画へ代用していません。生成の校正用見本は明暗曲線を作る入力だけに使用しました。輪郭のRGB位置差は元参照の60走査線から推定しましたが、被写体の縦枠の幅も含むためレンズの正確なPSFとは扱っていません。</p>
<h2>描画とビルドの検証</h2><p>別解像度の実写・録画マスク・CGへ同じ矩形を描いたGPU照合では、中心位置の差は最大{coordinates["maxCenterErrorPixels"]:.3f}px、面積比は{coordinates["minAreaRatio"]:.4f}でした。電源断の最大RGB値は{coordinates["powerOffMaxByte"]}です。全画面のマゼンタと黒を拒否する検査も、正常画像と失敗画像で校正しています。</p>
<p>記録したEditModeテストは2288件すべて成功しています。APKのビルドはround-{build["profile_round"]:02d}で成功し、VR起動設定と主画面シェーダのコンパイルを確認しました。{'現在のシェーダと設定を含むAPKです。' if current_build else 'このAPKは現在のシェーダと設定より前の版です。現在版の実機検証を済ませた証拠には使いません。'}Questへ導入しての確認は未実施です。</p>
<p class="note">実描画と判定: round-NN / 元画像とシェーダの記録: manifest.json / 生成ツールとプロンプト: generation-provenance.json / 明暗曲線: grade-fit.json / 数値検証: verification.json・telemetry-check.json。</p></div></main>
<dialog id="zoom"><button id="close">閉じる</button><p id="zoom-caption"></p><img id="zoom-image" alt="画像を拡大表示"></dialog>
<div class="library">{''.join('<img id="'+a['id']+'" src="'+a['uri']+'" alt="">' for a in library)}</div>
<script>const DATA={payload}; const version=document.getElementById('version');
function source(key){{return document.getElementById(key).src}}
function update(){{const e=DATA.entries[Number(version.value)],a=DATA.library.find(x=>x.id===e.key);document.getElementById('active').src=source(e.key);document.getElementById('active-caption').textContent=e.label+' / '+a.name+' / '+a.size.join(' × ')+' / SHA-256 '+a.hash.slice(0,12);document.getElementById('score').textContent=e.score===undefined?e.label:'独立評価 '+e.score+'%';document.getElementById('feedback').replaceChildren(...(e.feedback||[]).map(t=>{{const li=document.createElement('li');li.textContent=t;return li}}));document.getElementById('response').textContent=e.response||''}}
for(const im of document.querySelectorAll('[data-source]'))im.src=source(im.dataset.source);
version.value=String(DATA.latest);version.addEventListener('change',update);update();
for(const [id,d] of [['previous',-1],['next',1]])document.getElementById(id).onclick=()=>{{version.value=String((Number(version.value)+d+DATA.entries.length)%DATA.entries.length);update()}};
for(const b of document.querySelectorAll('[data-round]'))b.onclick=()=>{{version.value=b.dataset.round;update();document.getElementById('compare').scrollIntoView()}};
for(const im of document.querySelectorAll('main img'))im.onclick=()=>{{document.getElementById('zoom-image').src=im.src;document.getElementById('zoom-caption').textContent=im.closest('figure')?.querySelector('figcaption')?.textContent||im.alt;document.getElementById('zoom').showModal()}};
document.getElementById('close').onclick=()=>document.getElementById('zoom').close();</script></html>'''
temp = OUT / "index.html.tmp"
temp.write_text(doc, encoding="utf-8")
temp.replace(OUT / "index.html")
print(json.dumps({"rounds": len(rounds), "library_images": len(library), "latest_score": latest["score"], "bytes": (OUT / "index.html").stat().st_size}))
