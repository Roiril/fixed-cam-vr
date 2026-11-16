"""record-scroll.mjs で撮った通しの動画を、区間ごとの速さで詰めて曲を付ける（X 用）。

  py -3.11 website/scripts/retime-scroll.py <入力 mp4> <出力 mp4> [--seconds 30] [--music <音>] [--music-start 2]

区間は入力の秒で書く（下の PLAN）。速さは相対値で、全体が --seconds に収まるように一律に掛け直す。
区間の境目で速さが跳ばないよう、境目の前後 0.6 秒で滑らかにつなぐ。
コマは入力の最寄りのコマを拾うだけ（混ぜない。混ぜるとスクロールの文字が二重に見える）。
"""
import argparse
import json
import subprocess
import sys

# (入力の始まり秒, 相対の速さ, 何を見せる所か)。区間は record-scroll.mjs の周の印（marks）から決める
PLAN = [
    (0.0, 2.0, "頭の静止"),
    (1.4, 1.8, "1 周目"),
    (22.5, 1.4, "2 周目の頭（人形が 1 体群れを離れている）"),
    (25.0, 5.5, "2 周目の残り（変化が少ない）"),
    (36.1, 1.4, "3 周目の頭（人形がこちらを向く）"),
    (40.0, 4.5, "予告動画・ゲーム機の手前"),
    (43.5, 2.6, "ゲーム機とテレビ（赤く染まり、テレビに人形）"),
    (49.0, 4.5, "ストーリーからクレジット"),
    (60.0, 1.2, "暗がりから人形の顔"),
]
BLEND = 0.6


def probe(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                          "stream=width,height,r_frame_rate,nb_frames", "-of", "json", path],
                         capture_output=True, text=True, check=True).stdout
    s = json.loads(out)["streams"][0]
    num, den = s["r_frame_rate"].split("/")
    return s["width"], s["height"], int(num) / int(den), int(s["nb_frames"])


def speed_at(t):
    current = PLAN[0][1]
    for i, (start, speed, _) in enumerate(PLAN):
        if t < start - BLEND / 2:
            break
        previous = PLAN[i - 1][1] if i else speed
        x = min(max((t - (start - BLEND / 2)) / BLEND, 0.0), 1.0)
        x = x * x * (3 - 2 * x)
        current = previous + (speed - previous) * x
    return current


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("src")
    parser.add_argument("dst")
    parser.add_argument("--seconds", type=float, default=30.0)
    parser.add_argument("--music", default="")
    parser.add_argument("--music-start", type=float, default=2.0)
    parser.add_argument("--music-gain", type=float, default=-3.0)
    a = parser.parse_args()

    width, height, fps, count = probe(a.src)
    duration = count / fps
    # 入力の時間を細かく刻んで、出力の時間へ積分する
    dt = 1 / (fps * 8)
    raw = []
    t = 0.0
    out_t = 0.0
    while t < duration:
        raw.append((out_t, t))
        out_t += dt / speed_at(t)
        t += dt
    scale = a.seconds / out_t
    frames_out = round(a.seconds * fps)
    picks = []
    j = 0
    for k in range(frames_out):
        target = k / fps / scale
        while j + 1 < len(raw) and raw[j + 1][0] <= target:
            j += 1
        picks.append(min(int(round(raw[j][1] * fps)), count - 1))
    print(f"入力 {duration:.1f}s → 出力 {a.seconds:.1f}s（速さの掛け直し {1 / scale:.2f}）", file=sys.stderr)
    for start, speed, label in PLAN:
        print(f"  {start:5.1f}s〜 ×{speed / scale:.2f} {label}", file=sys.stderr)

    frame_bytes = width * height * 3
    decoder = subprocess.Popen(["ffmpeg", "-v", "error", "-i", a.src, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                               stdout=subprocess.PIPE)
    encode = ["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{width}x{height}",
              "-r", str(fps), "-i", "-"]
    if a.music:
        fade_out = a.seconds - 2.5
        encode += ["-ss", str(a.music_start), "-t", str(a.seconds), "-i", a.music,
                   "-filter_complex", f"[1:a]afade=t=in:st=0:d=1.5,afade=t=out:st={fade_out}:d=2.5,volume={a.music_gain}dB[a]",
                   "-map", "0:v", "-map", "[a]", "-c:a", "aac", "-b:a", "192k"]
    encode += ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
               "-t", str(a.seconds), a.dst]
    encoder = subprocess.Popen(encode, stdin=subprocess.PIPE)

    index = -1
    frame = None
    for want in picks:
        while index < want:
            frame = decoder.stdout.read(frame_bytes)
            if len(frame) < frame_bytes:
                raise SystemExit(f"入力のコマが足りない（{index + 1} で尽きた）")
            index += 1
        encoder.stdin.write(frame)
    encoder.stdin.close()
    decoder.stdout.close()
    decoder.wait()
    if encoder.wait() != 0:
        raise SystemExit("ffmpeg の書き出しに失敗")


if __name__ == "__main__":
    main()
