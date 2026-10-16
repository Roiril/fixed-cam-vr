"""博士タブレットの効果音を作る（LEDGER 0266「タブレットは音がないので、音をつけて」）。

    py -3.11 tools/make-tablet-sounds.py            # 書き出し → tablet/.../web/asset/
    py -3.11 tools/make-tablet-sounds.py --check    # 書かずに測るだけ

方針（rules/sound-design.md）:
- **Quest と同じ装置の音を、同じ意味の操作に当てる。** 言語の切替音（0153 の 2 本を交互）、
  打鍵、カメラの切替音、題字の音、スクリーンが点く音。タブレットは研究所の端末で、
  Quest と別の楽器を鳴らすと 2 つの機械に聞こえる
- **もらった音・焼いた音はバイト単位で複写する**（§4.5「音量と端の処理だけ」）。
  音量は書き換えず、鳴らす側が表の `gainDb` を掛ける
- 新しく合成するのは 2 本だけ: 反映の判子（`sfx_tablet_stamp`）と、説明の終わりに
  通信が切れる音（`sfx_tablet_signal_cut`。`bed_static` ＝ 信号断の砂嵐 ＋ `sfx_power_off`）
- **曲は増やさない**（0256「BGMは、タブレット上では、軽減の画面だけ」）
- 大きさは博士の声（約 -21 LUFS）より下。いちばん大きい「通信が切れる」でも声の 1dB 下

出力: `asset/<name>.wav` と、鳴らす側が読む表 `asset/tablet-sounds.json`。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Assets" / "Resources" / "Sound"
OUT = ROOT / "tablet" / "app" / "src" / "main" / "assets" / "web" / "asset"
SR = sk.SR
PEAK_DB = -3.0
VOICE_LUFS = -21.0   # 博士の動画 4 本の実測 -20.2〜-22.2（2026-10-16）

# 鳴らす名前 → (素材, 目標 LUFS, 何に当てるか)。素材が複数なら順に回す（乱数で選ばない）。
COPIES = {
    "title-in": (["sfx_title_in"], -26.0, "題字の書き出し（Quest のタイトルと同じ音）"),
    "title-out": (["sfx_title_out"], -28.0, "題字を押して去るとき（Quest と同じ音）"),
    "choice": (["sfx_lang_1", "sfx_lang_2"], -30.0, "言語・軽減を選び替えたとき（0153 の 2 本を交互）"),
    "key": ([f"sfx_type_{i}" for i in range(1, 9)], -32.0, "次へ・戻る・設定に戻る・一時停止など（打鍵 1 打）"),
    "send": (["sfx_switch_1"], -29.0, "この設定を送る（送る瞬間の切替）"),
    "screen-on": (["sfx_screen_on"], -27.0, "博士の通信が点く（説明の始まり）"),
    "chapter": ([f"sfx_switch_{i}" for i in range(2, 7)], -32.0, "章が切り替わる（カメラの切替）"),
}
SYNTH_TARGET = {"stamp": -26.0, "signal-cut": -22.0}

# 説明の終わりの時刻（signal-cut.js と対。片方だけ直すと音と画がずれる）
NOISE_SEC = 0.95
OFF_SEC = 0.26
BURSTS = [(0.50, 0.58), (0.65, 0.71), (0.79, 0.85)]


def mono(y: np.ndarray) -> np.ndarray:
    return y.mean(axis=1) if y.ndim == 2 else y


def read(name: str) -> np.ndarray:
    y, sr = sk.read_wav(str(SRC / f"{name}.wav"))
    assert sr == SR, f"{name} は {sr}Hz（48kHz のはず）"
    return y


def norm_lufs(y: np.ndarray, target: float) -> np.ndarray:
    """ラウドネスを target へ。尖頭が -3dBTP を超えるなら丸めてから合わせ直す（make-sounds.py と同じ考え）。"""
    z = y * 10 ** ((target - sk.lufs(mono(y))) / 20)
    drive = 0.0
    while sk.true_peak_db(mono(z)) > PEAK_DB and drive < 14.0:
        drive += 2.0
        z = sk.soft_clip(y, drive)
        z = z * 10 ** ((target - sk.lufs(mono(z))) / 20)
    if sk.true_peak_db(mono(z)) > PEAK_DB:
        z = z * 10 ** ((PEAK_DB - sk.true_peak_db(mono(z))) / 20)
    return z


def stamp() -> np.ndarray:
    """研究所の角印が紙に押される。木の柄が机を打つ鈍い胴と、紙が叩かれる乾いた音。

    胴は 200Hz より上に置く（タブレットの小さなスピーカーでも残る）。共振は整数倍に積まない（§3.6）。
    """
    sec = 0.42
    n = int(sec * SR)
    body = sk.mix(
        (sk.loop_noise(sec, 190, 760, slope_db_oct=-3.0, seed=6101) * sk.env_ar(sec, 0.002, 0.075, curve=2.6), 1.0),
        (sk.tone_band(sec, 262, q=9, seed=6102) * sk.env_ar(sec, 0.003, 0.13, curve=2.2), 0.55),
        (sk.tone_band(sec, 262 * 1.97, q=8, seed=6103) * sk.env_ar(sec, 0.002, 0.09, curve=2.4), 0.35),
        (sk.tone_band(sec, 262 * 3.06, q=7, seed=6104) * sk.env_ar(sec, 0.002, 0.06, curve=2.6), 0.2),
    )
    slap = sk.loop_noise(sec, 1800, 7000, slope_db_oct=-2.0, seed=6105) * sk.env_ar(sec, 0.0008, 0.018, curve=3.0)
    # 押し付けた後、紙がわずかに擦れる
    rub = sk.loop_noise(sec, 900, 4200, slope_db_oct=-3.0, seed=6106)
    t = sk.t_axis(sec)
    rub_env = np.clip((t - 0.05) / 0.03, 0, 1) * np.exp(-np.clip(t - 0.08, 0, None) / 0.07)
    y = sk.mix((body, 1.0), (slap, 0.45), (rub * rub_env, 0.08))
    y = sk.env_fade(y[:n], 0.0005, 0.06)
    return norm_lufs(y, SYNTH_TARGET["stamp"])


def intensity(p: float) -> float:
    """signal-cut.js の intensity と同じ形（画の砂嵐の濃さ）。"""
    i = p ** 1.7 * 0.75
    for a, b in BURSTS:
        if a <= p < b:
            i += 0.38
    if p > 0.88:
        i = 1.0
    return min(1.0, i)


def signal_cut() -> np.ndarray:
    """説明の終わり。止まった博士に砂嵐が乗り、ざ・ざ・ざと強まり、ブラウン管が切れて黙る。

    砂嵐は `bed_static`（信号断の砂嵐・合成）を画の濃さの曲線で開き、ざ・ざ・ざの頭で強める。
    消える瞬間（NOISE_SEC）に `sfx_power_off`（もらったブラウン管の電源断）の立ち上がりを合わせる。
    電源断は音量だけを掛け、切らない・削らない（§4.5）。尾は黒の一拍の中へ流れる。
    """
    static = mono(read("bed_static"))
    off = mono(read("sfx_power_off"))
    a = np.abs(off)
    onset = int(np.argmax(a > a.max() * 0.1))
    start = max(0, int(NOISE_SEC * SR) - onset)
    n = max(int((NOISE_SEC + OFF_SEC) * SR), start + len(off))
    y = np.zeros(n)

    seg = static[: int((NOISE_SEC + OFF_SEC) * SR)]
    t = np.arange(len(seg)) / SR
    # 濃さ：画と同じ曲線。30ms ごとに強さを少し揺らす（同じ高さで鳴る機械の音にしない・種は固定）
    rng = np.random.default_rng(6201)
    step = int(0.03 * SR)
    flutter = np.repeat(0.75 + 0.5 * rng.random(len(seg) // step + 1), step)[: len(seg)]
    flutter = np.convolve(flutter, np.ones(96) / 96, mode="same")   # 段差でプチプチ鳴らさない
    env = np.array([intensity(min(1.0, x / NOISE_SEC)) for x in t])
    env = np.maximum(env, 0.06) * flutter
    # 潰れる段：電源断に場所を譲りながら、ぶつっと切れる
    collapse = np.clip((t - NOISE_SEC) / OFF_SEC, 0, 1)
    env *= np.where(t < NOISE_SEC, 1.0, (1 - collapse) ** 2 * 0.5)
    # 濃いほど上が開く（薄いうちは遠くの受信機、強まると目の前で鳴る）
    dark = sk.biquad(seg, "lp", 1700)
    bright = seg
    mixw = np.array([intensity(min(1.0, x / NOISE_SEC)) for x in t])
    noise = (dark * (1 - mixw) + bright * mixw) * env
    y[: len(noise)] += noise
    noise_part = norm_lufs(y[: int(NOISE_SEC * SR)], SYNTH_TARGET["signal-cut"])
    gain = float(np.max(np.abs(noise_part))) / max(float(np.max(np.abs(y[: int(NOISE_SEC * SR)]))), 1e-9)
    y *= gain
    # 合成した砂嵐の部分だけは合成音の線で測る（後ろに足す電源断はもらった音なので、
    # sound-lint は全体を判定外にしている。そのぶんここで落とす）
    head = y[: int(NOISE_SEC * SR)]
    assert sk.tone_purity(head) <= 0.60, f"砂嵐に発振器が居る（純度 {sk.tone_purity(head):.2f}）"
    assert sk.tonality_db(head) <= 25.0, f"砂嵐に突出したトーン（{sk.tonality_db(head):.1f}dB）"
    off_gain = 10 ** ((SYNTH_TARGET["signal-cut"] + 1.0 - sk.lufs(off)) / 20)
    y[start: start + len(off)] += off * off_gain
    y = sk.env_fade(y, 0.004, 0.03)
    # 砂嵐と電源断の釣り合い（電源断は砂嵐の頭打ちより 1dB 上）を保ったまま、全体を目標の高さへ
    y *= 10 ** ((SYNTH_TARGET["signal-cut"] - sk.lufs(y)) / 20)
    tp = sk.true_peak_db(y)
    if tp > PEAK_DB:
        y *= 10 ** ((PEAK_DB - tp) / 20)
    return y


def measure(name: str, y: np.ndarray) -> dict:
    m = mono(y)
    return {
        "sec": round(len(m) / SR, 3),
        "lufs": round(sk.lufs(m), 1),
        "truePeakDb": round(sk.true_peak_db(m), 1),
        "speakerDb": round(sk.speaker_loss_db(m), 1),
    }


# 試聴の並び（秒・名前）。タブレットで実際に起きる順と間。判子は印が押される 0.51 秒後に置く
PREVIEW = [
    (0.3, "title-in"), (4.0, "title-out"),
    (6.0, "choice"), (6.8, "choice"), (8.0, "key"),
    (9.5, "choice"), (10.4, "choice"), (11.6, "key"),
    (13.0, "send"), (14.5 + 0.51, "stamp"),
    (17.0, "screen-on"), (20.0, "chapter"), (23.0, "chapter"),
    (26.0, "signal-cut"),
]


def write_preview(path: Path, manifest: dict) -> None:
    out = np.zeros(int(30.0 * SR))
    turns: dict[str, int] = {}
    for at, name in PREVIEW:
        files = manifest["sounds"][name]["files"]
        entry = files[turns.get(name, 0) % len(files)]
        turns[name] = turns.get(name, 0) + 1
        y = mono(sk.read_wav(str(OUT / entry["file"]))[0]) * 10 ** (entry["gainDb"] / 20)
        start = int(at * SR)
        out[start: start + len(y)] += y[: len(out) - start]
    path.parent.mkdir(parents=True, exist_ok=True)
    sk.write_wav(str(path), out, mono=True)
    print(f"preview {path}  ({', '.join(f'{at:g}s {n}' for at, n in PREVIEW)})")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="書かずに測るだけ")
    parser.add_argument("--preview", help="人が聴く 1 本（画面の順に並べ、表の大きさで鳴らす）の書き出し先")
    args = parser.parse_args()

    manifest = {
        "version": 1,
        "note": "tools/make-tablet-sounds.py が書く。手で直さない。gainDb は鳴らす側が掛ける（素材の音量は触っていない）",
        "voiceLufs": VOICE_LUFS,
        "sounds": {},
    }
    for key, (names, target, purpose) in COPIES.items():
        files = []
        for name in names:
            y = read(name)
            info = measure(name, y)
            gain = round(target - info["lufs"], 2)
            files.append({"file": f"{name}.wav", "gainDb": gain, "sourceLufs": info["lufs"], "sec": info["sec"]})
            if not args.check:
                shutil.copyfile(SRC / f"{name}.wav", OUT / f"{name}.wav")
                assert sha256(SRC / f"{name}.wav") == sha256(OUT / f"{name}.wav")
        manifest["sounds"][key] = {"purpose": purpose, "targetLufs": target, "files": files}
        listed = ", ".join("%s (%+.1fdB)" % (f["file"], f["gainDb"]) for f in files)
        print(f"{key:11s} {target:6.1f} LUFS  {listed}")

    for key, fn, file in (("stamp", stamp, "sfx_tablet_stamp.wav"), ("signal-cut", signal_cut, "sfx_tablet_signal_cut.wav")):
        y = fn()
        info = measure(key, y)
        print(f"{key:11s} {SYNTH_TARGET[key]:6.1f} LUFS  {file}  {info}")
        assert info["truePeakDb"] <= PEAK_DB + 0.05, f"{key} の尖頭 {info['truePeakDb']}dBTP"
        if not args.check:
            sk.write_wav(str(OUT / file), y, mono=True)
        manifest["sounds"][key] = {"purpose": {"stamp": "反映の判子（印が押される瞬間）",
                                              "signal-cut": "説明の終わり（通信が切れる・signal-cut.js と同じ時刻）"}[key],
                                   "targetLufs": SYNTH_TARGET[key],
                                   "files": [{"file": file, "gainDb": 0.0, "sourceLufs": info["lufs"], "sec": info["sec"]}]}

    if args.preview:
        write_preview(Path(args.preview), manifest)

    loudest = max(f["sourceLufs"] + f["gainDb"] for s in manifest["sounds"].values() for f in s["files"])
    assert loudest < VOICE_LUFS, f"博士の声（{VOICE_LUFS}）より大きい音がある: {loudest}"
    if not args.check:
        path = OUT / "tablet-sounds.json"
        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        json.loads(tmp.read_text(encoding="utf-8"))
        os.replace(tmp, path)
        print(f"wrote {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
