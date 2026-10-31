"""Compose a NEW native Google Docs report plan from screenshot evidence.

This script does not authenticate or upload. The connected Docs tool applies the
plan. These indexes must never be applied to an existing Doc. For incremental
edits, resolve the current native structure and revision through get_document.
Inline-image replacements have zero net UTF-16 length, keeping indexes stable.
"""
from pathlib import Path
import hashlib
import json
import runpy
import sys
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
OUT = Path("G:/マイドライブ/shubie/reports/2026-10-30_crt-reference")
OLD = OUT.parent / "2026-10-30_screen-study"
paragraphs, image_groups, text_parts, scores = [], {}, [], []
native_tables = []
normalize = runpy.run_path(str(Path(__file__).with_name("evidence-index.py")))["read_evidence"]
cursor = 1


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def add(text, style="NORMAL_TEXT"):
    global cursor
    text = text.rstrip("\n") + "\n"
    length = len(text.encode("utf-16-le")) // 2
    paragraphs.append({"start": cursor, "end": cursor + length, "style": style})
    text_parts.append(text)
    start = cursor
    cursor += length
    return start


def picture(path, index, width=220):
    assert path.is_file(), path
    digest = sha(path)
    with Image.open(path) as im:
        w, h = im.size
    group = image_groups.setdefault(digest, {"path": path.as_posix(), "sha256": digest, "locations": [], "aliases": []})
    group["locations"].append({"index": index, "width": width, "height": width * h / w})
    if path.as_posix() not in group["aliases"]:
        group["aliases"].append(path.as_posix())


def pair(left, right, caption):
    start = add("□ □", "IMAGE")
    picture(left, start)
    picture(right, start + 2)
    add(caption, "CAPTION")


def feedback(j):
    result = j.get("feedback", j.get("feedback_summary", [j.get("verbatim", "")]))
    if isinstance(result, str):
        result = [result]
    assert result and any(t.strip() for t in result), "Missing judgment text"
    for line in result:
        if line.strip():
            add(line, "BULLET")
    if j.get("response"):
        add("反映・扱い：" + j["response"])


reference = ROOT / "Assets/Resources/CrtReference/CaseColor.jpg"
manifest = read(OUT / "round-28/manifest.json")
verification = read(OUT / "coordinate-validation.json")
build = read(OUT / "build-28-validation.json")
# Historical screenshots belong to their frozen render/build, not the current
# working tree, which can contain later diagnostic-only experiments.
assert build["shader_sha256"] == manifest["shaderHash"]
assert build["runtime_profile_sha256"] == manifest["runtimeProfileHash"]

add("参照画像から再現したCRTスクリーンの比較", "TITLE")
add("廻リ視 ／ 2026年10月31日 ／ 確認用・見た目の完成条件は未達", "SUBTITLE")
add("参照画像を直接素材化したスクリーンを本編へ実装した。96%以上の完了条件は未達。枠・ガラス・映像を含むユーザー条件へ評価範囲を訂正し、同じ28版を一度校正した結果は84%。旧評価86%と映像だけの診断73%は別条件であり、点数を実装改善へ換算しない。", "LEAD")
add("先行した言語化による4案はユーザーが却下した。その後は参照画像を画像生成へ直接入力し、筐体の開口とガラスの素材を作った。枠のRGBには元JPEGそのものを使っている。")

add("現在の実描画", "HEADING_1")
current = OUT / "round-28/02.png"
assert sha(current) == next(c["sha256"] for c in manifest["captures"] if c["file"] == "A_front.png")
pair(reference, current, "左：ユーザーの参照　右：28版・Unity実描画／訂正後のユーザー条件84%（画像は同じ）")
add("物理筐体の比率は参照と同じ1280:1030。4:3ソースの左右の黒帯を表示しない。CGとマスクの著作座標は16:9を保ち、開口へ共通に写す。")
add("枠の形と艶、ガラス周囲の反射は近いとの評価。差は赤青の色縁の幅と輪郭の芯、灰色の膜のような階調、細かなざらつき。部屋の素材が階調差へ与えた影響は2枚だけでは分けられない。")
add("0295でユーザーから『スクリーンに映すのを、このプロジェクトの無人画像にして、元の画像との加工感の類似度で比較して似せていって。』と指示を受けた。無人写真を固定し、部屋や家具・撮影構図を除いた映像の加工感で調整を続ける。96%以上の条件は維持する。")

add("評価の読み方", "HEADING_1")
add("新しい評価者に参照と実描画の2枚だけを渡し、部屋・家具の内容と配置・撮影構図を除外して辛口比較を求める。目標点や履歴・実装は知らせない。途中でエージェントが筐体を除外する条件を追加していたため訂正した。今後は枠・ガラス・映像を含む範囲を固定する。点数は主観であり画像測定の誤差率ではない。")
add("同じ22版の画像対を別の評価者へ渡した判定は79%・82%・86%。元の88%を含めると79〜88%に分かれた。小さな点差だけでは加工の改善を区別できない。")
control = read(OUT / "calibration.json")["same_material_pair"]
pair(OUT / control["images"][0], OUT / control["images"][1], "22設定の対照：材質の全パラメータを同値にし、入力写真だけを変更／94%")
add("入力は両方640×480 RGB24。LiveTex以外のshader propertyのハッシュで同値を確認した。評価者も明るさや細かさは被写体の影響を受けるため、加工設定の違いとは断定できないと記した。この対照で目標点や採点結果を補正しない。")

add("映像だけに限定した診断比較（2026年10月31日）", "HEADING_1")
add("この節はエージェントが枠・ガラスを除外した診断条件での記録。ユーザーの完成条件を示す点数ではない。各候補を同じ条件のまま比較した点数を保存し、訂正後の84%などへ補正しない。")
order = OUT / "processing-order-01"
if (order / "manifest.json").exists():
    om = read(order / "manifest.json")
    add("明暗補正とぼけ・色ずれの順序", "HEADING_2")
    add("同じRT経路でPSF→GradeとGrade→PSFを比較した。両条件の本番singlepassとの差には再サンプリングとlens/sensor/glareの相対順序の変更も含む。順序差より共通変更の画素差が大きいため、本番改善とは扱わない。校正不合格の初回出力は評価から除外した。")
    for capture in om["captures"]:
        camera = capture["plate"]
        j = read(order / f"blind-{camera}/judgment.json")
        for key in ["singlepass", "control", "variant"]:
            assert sha(order / capture[key]) == capture[key + "Hash"]
        add(f"順序比較 {camera} ／ 候補 {j['score']}%", "HEADING_2")
        pair(reference, order / capture["variant"], f"左：参照　右：Grade→PSFの候補{camera}／加工感{j['score']}%")
        pair(order / capture["control"], order / capture["variant"], "左：PSF→Gradeの対照　右：Grade→PSFの候補（同じRT条件）")
        for line in j["feedback"]:
            add(line, "BULLET")
optics = OUT / "processing-optics-01"
if (optics / "judgments.json").exists():
    op, oj = read(optics / "manifest.json"), read(optics / "judgments.json")
    assert op["baselinePixelMatch"] and op["baselineHashMatch"]
    add("ぼけ幅とRGB位置差の2×2", "HEADING_2")
    add("ぼけ幅1倍/0.5倍とRGB位置差1倍/2.5倍の4条件。それ以外を固定し、本番singlepassでA/B/Cを描いた。現行3枚は28版とhash一致。A現行は画素差0で既存の73%を再利用し、他11枚を新規評価者へ出した。")
    for index, capture in enumerate(op["captures"], 1):
        j = next(r for r in oj["rows"] if r["index"] == index)
        assert sha(optics / capture["file"]) == capture["sha256"]
        assert sha(Path(capture["source"])) == capture["sourceHash"]
        assert not capture["diagnosticKeyword"]
        label = f"{capture['plate']} ／ ぼけ{capture['spreadMultiplier']}倍・RGB位置差{capture['convergenceMultiplier']}倍"
        add(f"{label} ／ {j['score']}%", "HEADING_2")
        pair(reference, optics / capture["file"], f"左：参照　右：{capture['file']} ／ 加工感{j['score']}%")
        add("評価の要約：" + j["feedback_summary"])
    add("全入力を同じ方向へ改善する条件は得られず、既定値は変更していない。")

# Later manifests have several wrappers. Match a judgment through the actual
# neutral blind image hash, rather than assume an enum or a record position.
def diagnostic_phase(folder_name, title, baseline_dir, baseline_pattern, baseline_scores):
    folder = OUT / folder_name
    if not (folder / "judgments.json").exists():
        return
    judgments = read(folder / "judgments.json")
    by_hash = {}
    for j in judgments["rows"]:
        blind = folder / f"blind-{j['index']:02d}/02.png"
        digest = sha(blind)
        assert digest not in by_hash, "Same image was judged twice"
        by_hash[digest] = j
    add(title, "HEADING_2")
    for capture in normalize(folder)["rows"]:
        j = by_hash.get(capture["sha256"])
        if j is None:
            baseline = OUT / baseline_dir / baseline_pattern.format(camera=capture["plate"])
            assert sha(baseline) == capture["sha256"], "Unjudged image is not the declared control"
            score = baseline_scores[capture["plate"]]
            note = "同じ画像の既存診断を再利用"
        else:
            score = j["score"]
            note = j["feedback_summary"]
        add(f"{capture['file']} ／ 診断{score}%", "HEADING_2")
        pair(reference, Path(capture["path"]), f"左：参照　右：{capture['file']}／映像だけの診断{score}%")
        add(note)

psf = OUT / "processing-psf-linear-01"
if (psf / "manifest.json").exists():
    add("白点の光量とぼけの幅の校正", "HEADING_2")
    add("旧mipはencoded平均を後からsRGB decodeするため、白点の線形光量が約14〜18%へ落ちた。専用Float linear mipとGaussianは元写真のbaselevelを保ち、PSFだけをlinear平均へ替える。glare等は元LiveTexを保持する。光量保存や校正合格を見た目の達成と扱わない。")
    native_tables.append({"index": cursor, "rows": [["条件", "点光量 R / G・B", "広がり R / G・B（画素²）"],
        ["旧mip", "0.1408 / 0.1830", "1.3685 / 0.8205"],
        ["linear mip", "1.0000 / 1.0000", "1.3594 / 0.8103"],
        ["linear Gaussian", "1.0000 / 1.0001", "1.3689 / 0.8217"]]})
    add("")
diagnostic_phase("processing-psf-linear-01", "ぼけの色空間と形状", "", "{camera}_front.png", {"A":73,"B":58,"C":64})
diagnostic_phase("processing-softness-01", "強い低域化とglareの除去", "processing-psf-linear-01", "{camera}_linear_mip.png", dict.fromkeys("ABC",62))
diagnostic_phase("processing-tone-01", "明暗曲線だけの対照", "processing-psf-linear-01", "{camera}_linear_mip.png", dict.fromkeys("ABC",62))
if (OUT / "ai-processing-guide-3.png").exists():
    add("画像を直接入力した加工見本3", "HEADING_2")
    pair(reference, OUT / "ai-processing-guide-3.png", "左：参照　右：AI加工見本3／映像限定診断66%・Unity実描画ではない")
    add("元参照と28版Aを直接入力して生成。原写真や本番の背景へ代用せず、完成証拠へ含めない。")
candidate = OUT / "processing-candidate-01"
if (candidate / "judgments.json").exists():
    add("ユーザー条件での統合候補", "HEADING_1")
    add("枠・ガラス・映像を含む範囲へ訂正した新候補の比較。家具と部屋の内容・配置・撮影構図は除外。28版の校正84%を基準にし、映像だけの診断点数と連続させない。通常設定はまだ変更していない。")
    judgments = {j["plate"]: j for j in read(candidate / "judgments.json")["rows"]}
    for capture in normalize(candidate)["rows"]:
        if capture["metadata"].get("condition") != "integrated":
            continue
        j = judgments[capture["plate"]]
        assert sha(candidate / f"blind-{capture['plate']}/02.png") == capture["sha256"]
        add(f"統合候補{capture['plate']} ／ {j['score']}%", "HEADING_2")
        pair(reference, Path(capture["path"]), f"左：参照　右：{capture['file']}／訂正後のユーザー条件{j['score']}%")
        add("評価の要約：" + j["feedback_summary"])

add("無人写真・夜間・反射", "HEADING_1")
add("入力は2026年9月24日の現場保存分。Aは17:22:09、Bは17:22:10、Cは17:22:11に保存された。全反復で元の640×480 JPEGを固定し、生成した部屋へ差し替えていない。")
for camera in "ABC":
    capture = next(c for c in manifest["captures"] if c["file"] == f"{camera}_front.png")
    source = Path(capture["source"])
    assert sha(source) == capture["sourceHash"]
    pair(source, OUT / capture["file"], f"左：元の無人写真{camera}・640×480　右：同じ現行材質による実描画／1280×1030")
pair(OUT / "A_mono0.5.png", OUT / "A_mono1.0.png", "夜間モード：左は移行途中、右は完全移行。どちらも本番シェーダの出力")
pair(current, OUT / "A_reflection.png", "視点による反射：左は正面、右は視点を横へ移した材質出力")

add("描画・ビルドの検証", "HEADING_1")
for item in [
    "EditModeテストは2,288件すべて通過。",
    f"実写・録画マスク・CGのGPU照合は中心差最大{verification['maxCenterErrorPixels']:.3f}px、面積比{verification['minAreaRatio']:.4f}。",
    f"電源断の最大RGB値は{verification['powerOffMaxByte']}。筐体とガラスも消える。",
    "正常画像が通ることと、全面マゼンタ・黒出力を拒否することを検査した。材質未到達・物理比率違い・論理比率違いも診断で検出した。",
    f"28版APKは{build['bytes']:,}bytesでビルド成功。VR起動設定とfocusaware、主画面シェーダのコンパイルを確認した。",
    "描画manifestとビルドのシェーダ・CRT設定のハッシュが一致。既存のシーン・フォント・設定など11ファイルはビルド前後で不変。",
    "Questは未接続。実機の見え方・立体感・スケールは未検証。",
]:
    add(item, "BULLET")
add("APK：round-28.apk／SHA-256 " + build["sha256"])

add("実装した素材", "HEADING_1")
add("Frameは画像生成で抽出した開口alphaだけを使う。筐体のRGBはCaseColorとして保存した元JPEG。Glass・Normal・Reflectanceは参照を直接入力して生成した。Gradeは数値から作った単調1D曲線。画像生成の完成予想図を実描画へ代用していない。")
maps = ["CaseColor.jpg", "Frame.png", "Glass.png", "Normal.png", "Reflectance.png", "Grade.png"]
for i in range(0, len(maps), 2):
    pair(ROOT / "Assets/Resources/CrtReference" / maps[i], ROOT / "Assets/Resources/CrtReference" / maps[i+1],
         "素材：左 " + maps[i] + " ／右 " + maps[i+1])
add("NormalはRGBの法線データとして読む。GlassはISP後のガラス透過へ使い、Fresnel反射とともに夜間でも残す。最終alphaは1。Gradeの256値は記録からバイト一致で再生成できる。")
add("試験用Palette、core配分、局所照明の正規化、追加の微細粒、空間PSFは整理版から撤去した。参照の室内照明を別室へ固定するfieldも通常時は0とし、診断の比較だけに残している。")

add("全28版の実描画と判定", "HEADING_1")
add("以下は各時点の実描画。左は常に同じ参照画像。右がその回のUnity出力。各画像の存在とmanifestのSHA-256を照合している。")
for folder in sorted(OUT.glob("round-[0-9][0-9]")):
    j, m = read(folder / "judgment.json"), read(folder / "manifest.json")
    capture = next(c for c in m["captures"] if c["file"] == "A_front.png")
    assert sha(folder / "02.png") == capture["sha256"]
    assert sha(folder / "01.jpg") == sha(reference)
    add(folder.name + " ／ " + str(j["score"]) + "%", "HEADING_2")
    pair(reference, folder / "02.png", "左：参照　右：" + folder.name + "/02.png ／ SHA-256 " + capture["sha256"][:12])
    feedback(j)
    scores.append([folder.name, str(j["score"]) + "%"])

add("周辺減光と明暗fieldの4条件", "HEADING_1")
add("既存ビネットと参照の低周波RGB fieldの有無だけを替えた2×2の除去比較。A・B・Cで計12枚を描き、他の材質パラメータはハッシュで同値を確認した。Aの両方ありは27版と同じ画なので、その判定を再利用した。追加の独立判定は11枚。")
add("除去によって3入力すべてが同じ方向へ改善する証拠は得られなかった。Aは78〜82%、BとCは78〜84%。この結果だけを根拠に採用版を選んでいない。")
study = OUT / "ablation-study-01"
study_manifest = read(study / "manifest.json")
for camera in "ABC":
    for v, f in [(1,1), (0,1), (1,0), (0,0)]:
        name = f"{camera}_v{v}_f{f}"
        folder = study / name
        j = read(folder / "judgment.json")
        row = next(r for r in study_manifest["ablations"] if Path(r["file"]).stem == name)
        assert sha(folder / "02.png") == row["sha256"]
        label = f"{camera} ／ ビネット{'あり' if v else 'なし'}・field{'あり' if f else 'なし'}"
        add(label + " ／ " + str(j["score"]) + "%", "HEADING_2")
        pair(reference, folder / "02.png", "左：参照　右：" + name + "/02.png ／ SHA-256 " + row["sha256"][:12])
        feedback(j)
        if name != "A_v1_f1":
            scores.append([name, str(j["score"]) + "%"])

add("旧版と却下された4案", "HEADING_1")
add("以下の20枚は先行した比較の実描画。0は変更前で、1〜4は言語化から作った案。4案すべてがユーザーに却下されており、今回の達成判定へ含めない。各行の左に同じ入力・角度の0、右に試した案を置く。")
old_manifest = read(OLD / "manifest.json")
old_rows = old_manifest["shots"]
for camera, angle in [("A", "front"), ("B", "front"), ("C", "front"), ("A", "oblique")]:
    for variant in range(1, 5):
        left = OLD / "screenshots" / f"{camera}_0_{angle}.png"
        right = OLD / "screenshots" / f"{camera}_{variant}_{angle}.png"
        for path in [left, right]:
            row = next(r for r in old_rows if r["file"] == path.relative_to(OLD).as_posix())
            assert sha(path) == row["sha256"]
        add(f"旧比較 {camera}・{angle} ／ 0と{variant}", "HEADING_2")
        pair(left, right, f"左：{left.name}　右：{right.name}（却下済み）")

add("AI校正用見本と検証の境界", "HEADING_1")
add("次の2枚は画像生成の見本であり、Unityの実描画ではない。独立判定は最初の見本80%、作り直した見本86%。見本も正解として扱っていない。実描画39枚の数や完成判定へ含めない。")
for n, filename in enumerate(["ai-calibration-target.png", "ai-calibration-target-2.png"], 1):
    pair(reference, OUT / filename, f"左：参照　右：AI校正用見本{n}／完成判定の対象外")
add("参照の襖と縦枠の60走査線からRGBの広がりと位置差を推定した。ただし被写体の枠の幅も含むため、レンズPSFの真値とは扱わない。参照の明暗分布を別室へそのまま移すと中間調が失われた。")

add("証跡と現在の状態", "HEADING_1")
add("保存先：G:\\マイドライブ\\shubie\\reports\\2026-10-30_crt-reference\\")
add("round-NN：各実描画・参照・判定。ablation-study-01：除去比較と当時のソース。manifest.json：入力と材質のハッシュ。generation-provenance.json：画像生成の入力とプロンプト。grade-fit.json：明暗曲線の全256値。build-validation.json：ビルドと検証。")
add("本編実装は4a922443、資料の配置記録はd71c366c。後続の診断は検証済みのcheckpointとしてfeature/tablet-ui-motionへ保存する。採用と96%以上の完成判定は未確定。ユーザー調整中だった既存ファイルはコミットへ含めていない。")
add("この文書は確認用の比較報告。96%到達や採用の確定、再現不能の結論を示すものではない。")
add("採点一覧", "HEADING_1")
add("実描画39枚の独立判定。A_v1_f1は27版と同じため重複計上しない。校正用の再評価とAI見本の点数も含めない。")
assert len(scores) == 39

native_tables.append({"index": cursor, "rows": [["条件", "判定"]] + scores})
plan = {"purpose": "new_document_plan_only", "text": "".join(text_parts), "paragraphs": paragraphs, "images": list(image_groups.values()), "tables": native_tables,
        "table_index": cursor, "score_table": [["条件", "判定"]] + scores,
        "unique_images": len(image_groups), "inline_images": sum(len(g["locations"]) for g in image_groups.values())}
path = OUT / "google-docs-plan.json"
temp = path.with_suffix(".json.tmp")
temp.write_text(json.dumps(plan, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
temp.replace(path)
print(json.dumps({"utf16_length": cursor - 1, "paragraphs": len(paragraphs), "unique_images": plan["unique_images"], "inline_images": plan["inline_images"], "score_rows": len(scores)}))
