# -*- coding: utf-8 -*-
"""走行のログから、その区間で鳴っていた音を組み直す（**録音ではない**）。

```
py -3.11 tools/mix-from-log.py logs/capture/<日時>_xp.log --from 112 --to 124 \
    --out logs/preview/eyes.wav
```

⚠⚠ **これは実機の録音ではない。** Quest の `screenrecord` は音を録らないので
（`CLAUDE.md`「音は録画に映らない」）、**走行ログに残った時刻と音量に、実際の音源を並べ直したもの**。

**合っているもの**（ログが持っている）:

- 一撃が鳴った**時刻**（`ev=sfx` の `t=`）と、どの音か（`id=`）
- 敷く音の**音量**（`ev=sum` の `sndCurse` / `sndDolls` / `sndSwap` / `sndWind` / `sndWhite` /
  `sndHeart` / `sndScore`）
- 変種の**順番**（`SoundCueLogic.VariantCount` の回し方は決定論なので再現できる）

⚠ **合っていないもの**（ログに無い）:

- 1 発ごとの音程と音量の散らし（`Random.Range`。似た幅で振り直している）
- **定位**（0130 の 3D）。実機は目の方角・スクリーン・後ろから鳴るが、ここでは平面に混ぜている
- 切替音がどの変種だったか（`swN` は回数しか持たない）
- 装置の唸りの内訳（`sndAud` から他を引いた残りを `bed_device` 1 本に寄せている）

⚠ **判定には使わない。** 判定は `analyze-xp-log.py` の数値。これは**人に聴かせるため**の道具。
"""

from __future__ import annotations

import argparse
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOUND = os.path.join(ROOT, "Assets", "Resources", "Sound")
SCORE = os.path.join(ROOT, "tools", "web-compositor", "audio")

# 節目の音の id → (音源の頭, 変種の数)。`SoundCueLogic.ResourceName` / `VariantCount` と対。
CUES = {
    "TitleIn": ("sfx_title_in", 1), "TitleOut": ("sfx_title_out", 1),
    "Shatter": ("sfx_shatter", 1), "ScreenOn": ("sfx_screen_on", 1),
    "Glitch": ("sfx_glitch", 3), "Bell": ("amb_bell", 1),
    "DollCall": ("sfx_doll_call", 1), "PowerOff": ("sfx_power_off", 1),
    "EyeOpen": ("sfx_eye", 6), "EyeBig": ("sfx_eye_big", 1),
}

# 敷く音: テレメトリのキー → その値で鳴る音源たち。
BEDS = {
    "sndCurse": ["bed_beat", "bed_horror2"],   # 2 本とも同じ値（0131）
    # ⚠ 笑いは体ごとに 1 本（0139）。走行の混ぜ直しでは**全部の体を足す**
    #    （実機は別々の方角から鳴っているが、ここは 2 本の耳で聴く 1 本を作る道具）。
    "sndDolls": [f"bed_dolls_laugh_{i}" for i in range(1, 9)],
    # ⚠ `sndSwap` は 3 枚の**合計**（一人 ＋ 増える 2 枚）。ログは内訳を持たないので、
    #    1 枚目から順に 1.0 ずつ配る（`SoundBedLogic.ApplyDollSwell` の入り方に近い）。
    "sndSwap": (["bed_doll_one"]
                + [f"bed_dolls_grow_a_{i}" for i in range(1, 3)]
                + [f"bed_dolls_grow_b_{i}" for i in range(1, 5)]),
    "sndWind": ["bed_wind"],
    "sndWhite": ["bed_white"],
    # 心音（0175）。追いつき → 3 周目 A の入れ替わりのあいだ 1 本だけ鳴る。
    "sndHeart": ["bed_heart"],
}

# 卓が配る劇伴（`show.json` の volume 込み）。
SCORE_TRACKS = {"bgm_HorrBGM": ("HorrBGM.mp3", 0.50),
                "bgm_LostPlace2": ("LostPlace2.mp3", 0.57)}

_cache: dict[str, np.ndarray] = {}


def load(name: str) -> np.ndarray | None:
    """`Resources/Sound/<name>.wav` を読む（ステレオへ揃える）。"""
    if name in _cache:
        return _cache[name]
    p = os.path.join(SOUND, f"{name}.wav")
    if not os.path.exists(p):
        print(f"  無い: {p}")
        _cache[name] = None
        return None
    y, _sr = sk.read_wav(p)
    _cache[name] = sk.to_stereo(y)
    return _cache[name]


def load_score(track: str) -> tuple[np.ndarray, float] | None:
    """劇伴を読む（`show.json` の volume を掛けた実効の高さで返す）。"""
    if track not in SCORE_TRACKS:
        return None
    fn, vol = SCORE_TRACKS[track]
    src = os.path.join(SCORE, fn)
    if not os.path.exists(src):
        return None
    key = f"__score__{fn}"
    if key not in _cache:
        import subprocess

        import imageio_ffmpeg
        raw = os.path.join(ROOT, "logs", "sound", "probe", fn + ".wav")
        os.makedirs(os.path.dirname(raw), exist_ok=True)
        if not os.path.exists(raw):
            subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error", "-i", src,
                            "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", raw], check=True)
        y, _sr = sk.read_wav(raw)
        _cache[key] = sk.to_stereo(y)
    return _cache[key], vol


def parse(path: str):
    """`ev=sfx` の縁と `ev=sum` の標本を拾う。"""
    spots, sums = [], []
    for line in open(path, encoding="utf-8", errors="replace"):
        if "[XP] " not in line:
            continue
        d = dict(re.findall(r"(\w+)=([^\s]+)", line.split("[XP] ", 1)[1]))
        if "t" not in d:
            continue
        try:
            t = float(d["t"])
        except ValueError:
            continue
        if d.get("ev") == "sfx":
            spots.append((t, d.get("id", "")))
        elif d.get("ev") == "sum":
            sums.append((t, d))
    return spots, sums


def gain_at(sums, key: str, t: float) -> float:
    """その時刻の敷く音の音量（標本のあいだは線形で埋める）。"""
    prev = None
    for st, d in sums:
        v = d.get(key)
        if v is None:
            continue
        try:
            val = float(v)
        except ValueError:
            continue
        if st >= t:
            if prev is None:
                return val
            pt, pv = prev
            k = 0.0 if st == pt else (t - pt) / (st - pt)
            return pv + (val - pv) * k
        prev = (st, val)
    return prev[1] if prev else 0.0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--from", dest="t0", type=float, required=True, help="XP 秒")
    ap.add_argument("--to", dest="t1", type=float, required=True, help="XP 秒")
    ap.add_argument("--out", default=None)
    ap.add_argument("--seed", type=int, default=20260903)
    a = ap.parse_args()

    spots, sums = parse(a.log)
    if not sums:
        print("ev=sum が 1 行も無い（この計装より前のログ）")
        return 1

    dur = a.t1 - a.t0
    n = int(dur * sk.SR)
    out = np.zeros((n, 2))
    rng = np.random.default_rng(a.seed)

    # ---- 敷く音（音量の推移をそのまま写す）----------------------------------
    # ⚠ 音源はループなので、窓の長さぶん繰り返してから包絡を掛ける。
    step = sk.SR // 20   # 50ms ごとに音量を引き直す
    used = []
    for key, names in BEDS.items():
        env = np.array([gain_at(sums, key, a.t0 + i * step / sk.SR)
                        for i in range(n // step + 2)])
        if env.max() < 0.005:
            continue
        env_full = np.interp(np.arange(n), np.arange(len(env)) * step, env)
        for k, nm in enumerate(names):
            y = load(nm)
            if y is None:
                continue
            # ⚠ 合計しか無いキー（`sndSwap`）は 1 枚 1.0 を上限に順へ配る。
            #    同じ値で鳴る 2 本（`sndCurse`）はそのまま両方へ掛ける。
            part = (np.clip(env_full - k, 0.0, 1.0) if len(names) > 2 else env_full)
            if float(part.max()) < 0.005:
                continue
            rep = np.tile(y, (n // len(y) + 2, 1))[:n]
            out += rep * part[:, None]
            used.append(f"{nm}（最大 {part.max():.2f}）")

    # 装置の唸り（`sndAud` から他を引いた残り）。⚠ 内訳はログに無い。
    dev = []
    for i in range(n // step + 2):
        t = a.t0 + i * step / sk.SR
        rest = (gain_at(sums, "sndAud", t)
                - 2 * gain_at(sums, "sndCurse", t)
                - gain_at(sums, "sndDolls", t) - gain_at(sums, "sndSwap", t)
                - gain_at(sums, "sndWind", t) - gain_at(sums, "sndWhite", t)
                - gain_at(sums, "sndHeart", t))
        dev.append(max(0.0, min(1.5, rest)))
    dev = np.array(dev)
    if dev.max() > 0.005:
        y = load("bed_device")
        if y is not None:
            env_full = np.interp(np.arange(n), np.arange(len(dev)) * step, dev)
            rep = np.tile(y, (n // len(y) + 2, 1))[:n]
            out += rep * env_full[:, None]
            used.append(f"bed_device（残り・最大 {dev.max():.2f}）")

    # ---- 劇伴（鳴っていれば）-------------------------------------------------
    sc = np.array([gain_at(sums, "sndScore", a.t0 + i * step / sk.SR)
                   for i in range(n // step + 2)])
    if sc.max() > 0.005:
        trk = "bgm_HorrBGM"
        for st, d in sums:
            if st <= a.t0 and d.get("bgmTrk"):
                trk = d["bgmTrk"]
        got = load_score(trk)
        if got:
            y, vol = got
            env_full = np.interp(np.arange(n), np.arange(len(sc)) * step, sc)
            rep = np.tile(y, (n // len(y) + 2, 1))[:n]
            out += rep * env_full[:, None] * vol
            used.append(f"{trk}（最大 {sc.max():.2f}）")

    # ---- 節目の一撃 ----------------------------------------------------------
    turn: dict[str, int] = {}
    fired = []
    for t, cid in spots:
        base_n = CUES.get(cid)
        if base_n is None:
            continue
        base, cnt = base_n
        v = turn.get(cid, 0)
        turn[cid] = v + 1
        if not (a.t0 <= t < a.t1):
            continue
        name = base if cnt == 1 else f"{base}_{v % cnt + 1}"
        y = load(name)
        if y is None:
            continue
        # ⚠ 散らし幅は `SfxPlayer` の既定と同じだが、**種はログに無い**ので再現ではない。
        g = 10 ** (rng.uniform(-1.2, 1.2) / 20.0)
        i0 = int((t - a.t0) * sk.SR)
        m = min(len(y), n - i0)
        if m > 0:
            out[i0:i0 + m] += y[:m] * g
        fired.append(f"{t - a.t0:6.2f}s {name}")

    tp = sk.true_peak_db(out)
    if tp > -3.0:
        out *= 10 ** ((-3.0 - tp) / 20.0)

    dst = a.out or os.path.join(ROOT, "logs", "preview", "mix.wav")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    sk.write_wav(dst, out, peak_db=-3.0)
    d = sk.describe(out)
    print(f"  敷く音: {' / '.join(used) if used else '（無し）'}")
    print(f"  一撃 {len(fired)} 発:")
    for f in fired:
        print(f"    {f}")
    print(f"\n  → {dst}  {d['sec']:.2f}s  {d['lufs']:.1f} LUFS  tp {d['true_peak_db']:.2f}dB")
    print("  ⚠ これは録音ではない。ログの時刻と音量に音源を並べ直したもの（定位は入っていない）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
