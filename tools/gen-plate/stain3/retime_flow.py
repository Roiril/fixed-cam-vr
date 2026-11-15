"""Flow の動画 v2（しみ無し → 案1 の顔と笑う人形）に緩急を付ける。音は外す（本編は笑い声の効果音を使う）。LEDGER 0389。

区間は元の時刻（秒）。速さは 1 が等速、0.5 は半分の速さ。hold は最後のコマを止めて見せる秒数。
"""
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[3]
D = ROOT / "Logs/gen-plate/stain2A_right_video_20261115"  # 入力の動画と出力の置き場（git 管理外）
SRC = D / "flow_v2_plain_to_grin_4x3.mp4"
FPS = 24

PLANS = {
    # じわじわ滲んで、顔は一気に出る
    "A_creep_pop": [
        (0.00, 0.33, 0.5, 0),     # 何も無い
        (0.33, 2.00, 0.55, 0),    # しみがゆっくり滲む
        (2.00, 2.90, 3.0, 0),     # 顔が一気に浮き出る
        (2.90, 4.00, 1.6, 0),     # 人形が速めに覗く
        (4.00, 5.20, 1.0, 0),     # そのまま
    ],
    # 滲み切ったところで一度止まり、顔と人形が跳ぶように出る
    "B_stall_jump": [
        (0.00, 0.33, 0.5, 0),
        (0.33, 2.00, 0.5, 0.7),   # 滲んだしみで 0.7 秒止まる
        (2.00, 2.90, 4.5, 0),     # 顔はほぼ一瞬
        (2.90, 4.00, 2.5, 0),     # 人形も一瞬で覗く
        (4.00, 5.40, 1.0, 0),
    ],
}


def build(name, plan):
    parts, labels = [], []
    for i, (a, b, speed, hold) in enumerate(plan):
        f = f"[0:v]trim=start={a}:end={b},setpts=(PTS-STARTPTS)/{speed}"
        if hold:
            f += f",tpad=stop_mode=clone:stop_duration={hold}"
        f += f",fps={FPS}[v{i}]"
        parts.append(f)
        labels.append(f"[v{i}]")
    fc = ";".join(parts) + ";" + "".join(labels) + f"concat=n={len(plan)}:v=1:a=0[out]"
    out = D / f"flow_v2_retime_{name}.mp4"
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(SRC), "-filter_complex", fc, "-map", "[out]",
                    "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(out)], check=True)
    dur = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(out)],
                         capture_output=True, text=True).stdout.strip()
    print(name, dur, "秒")
    return out


def build_dissolve(name, plain_end, stain_at, dissolve, rest):
    """何も無い画（0〜plain_end を等速）から、しみが滲み切ったコマ（stain_at）へディゾルブで飛ぶ。
    滲んでいく途中は使わない。rest は stain_at 以降の (a, b, speed) の並び。"""
    parts = [f"[0:v]trim=start=0:end={plain_end},setpts=PTS-STARTPTS,fps={FPS},"
             f"tpad=stop_mode=clone:stop_duration={dissolve}[p]"]
    labels = []
    for i, (a, b, speed) in enumerate(rest):
        f = f"[0:v]trim=start={a}:end={b},setpts=(PTS-STARTPTS)/{speed},fps={FPS}"
        if i == 0:
            f += f",tpad=start_mode=clone:start_duration={dissolve}"
        parts.append(f + f"[r{i}]")
        labels.append(f"[r{i}]")
    parts.append("".join(labels) + f"concat=n={len(rest)}:v=1:a=0,fps={FPS},settb=1/{FPS}[rest]")
    parts[0] = parts[0].replace("[p]", f",settb=1/{FPS}[p]")
    parts.append(f"[p][rest]xfade=transition=fade:duration={dissolve}:offset={plain_end}[out]")
    out = D / f"flow_v2_retime_{name}.mp4"
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(SRC), "-filter_complex", ";".join(parts),
                    "-map", "[out]", "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                    str(out)], check=True)
    dur = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(out)],
                         capture_output=True, text=True).stdout.strip()
    print(name, dur, "秒")


# 2026-11-16 ユーザー「しみがじわーっとなるところはカットで、ディゾルブ的に一瞬で膨らむ前までくる」
# 「半分の速さにする必要はない」。顔が出る所は A と同じく加速する
DISSOLVE = {
    "C_dissolve_pop": dict(plain_end=0.25, stain_at=2.0, dissolve=0.25,
                           rest=[(2.0, 2.9, 3.0), (2.9, 4.0, 1.6), (4.0, 5.2, 1.0)]),
}

if __name__ == "__main__":
    only = sys.argv[1:]
    for n, p in PLANS.items():
        if not only or n in only:
            build(n, p)
    for n, p in DISSOLVE.items():
        if not only or n in only:
            build_dissolve(n, **p)
