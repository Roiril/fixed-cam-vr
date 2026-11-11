"""ツインの通し走行を、何も知らない体験者役（サブエージェント）へ渡せる形にまとめる。

    py -3.11 tools/twin-playtest.py <走行のフォルダか時刻の名前> --unity-log <Unity の全文ログ>
    py -3.11 tools/twin-playtest.py <走行> --unity-log <ログ> --briefing-ref HEAD   # 博士の台詞を前の版で渡す

出すもの（`Logs/twin-playtest/<走行>/`）:
    experience.md   体験者に届く順の記録。博士の説明（字幕）→ 装着後の文字 → 画（コマ）・通信画面の文字・音
    frames/NNN.jpg  目線の動画から切ったコマ。走行の名前・区間名・内部の語は焼かない

使い方と限界は `skills/twin-loop` の「体験者役のサブエージェントを使うとき」。
- `twin-look.py cut` のコマは下の帯に内部の語（MarkLogged・take の id）が焼いてあり、出来事の縁で切るので
  通信画面の文字を打っている途中が写る。こちらは読み終わる頃を切り、帯を焼かない
- 通信画面の文字は Unity のログの `[Comms] AIエージェントからの連絡` から写す（その走行で実際に出た文）
- 音は動画に入らない（固定刻み）。`ev=sfx` と、画の出来事に必ず伴う音だけを言葉で書く
- 体験者役の感想は案。初めて被る人の代わりにはならない（`reference/why.md` §3）
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[1]
DRIVE = Path("G:/マイドライブ/shubie/renders/twin-verify")
BRIEFING = "tablet/app/src/main/assets/web/asset/briefing-v1.json"

# 乗っ取り後の返事は、どの報告にも同じ赤い文が出る（CommsPanel.TakeoverLieText・日本語）。
LIE_JA = "異常は検出されませんでした"

# 動画に入らない音を、画の出来事から言葉にする。ev=sfx の id → 体験者に聞こえるもの。
SFX = {
    "TitleIn": "低くうねる音", "TitleOut": "題字が焼けて消える音", "ScreenOn": "テレビの電源が入る音",
    "Bell": "鈴が 1 回鳴る", "Glitch": "映像が乱れる音（ザッ）", "DollCall": "すぐ後ろで、子どもの声「あーそぼー」",
    "ScreenAdd": "テレビが 1 台点く音", "PowerOff": "電源が落ちる音", "DollEndFall": "低く落ちていく音",
}
# 画の出来事に必ず伴う音（rules/sound-design.md・rules/ending.md の契約）。走行が鳴らしたかは実機で聴く。
ALWAYS = [
    (r"ev=commsError active=1", "警告音が鳴りはじめ、だんだん強くなる"),
    (r"ev=farewell phase=appear", "人形のいる方から、オルゴールが小さく鳴りはじめる"),
    (r"ev=farewell phase=retreat", "オルゴールが、人形と一緒に遠ざかって小さくなる"),
]


def parse(line):
    m = re.match(r"tau=([\d.]+)\s+\[XP\]\s+(.*)", line)
    if not m:
        return None
    ev = {"tau": float(m.group(1)), "raw": m.group(2)}
    for k, v in re.findall(r"(\w+)=(\S+)", m.group(2)):
        ev.setdefault(k, v)
    return ev


def comms_texts(unity_log):
    """Unity の全文ログから、出た順の（種類, 文面）。"""
    out = []
    pat = re.compile(r"\[Comms\] AIエージェントからの連絡 (\w+)「(.*?)」")
    for line in unity_log.read_text(encoding="utf-8", errors="replace").splitlines():
        m = pat.search(line)
        if m:
            out.append((m.group(1), m.group(2).replace("／", " ／ ")))
    return out


def briefing_lines(ref):
    if ref:
        raw = subprocess.run(["git", "-C", str(ROOT), "show", f"{ref}:{BRIEFING}"],
                             capture_output=True, check=True).stdout.decode("utf-8")
    else:
        raw = (ROOT / BRIEFING).read_text(encoding="utf-8")
    return [cue["text"] for scene in json.loads(raw)["scenes"] for cue in scene["cues"]["ja"]]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run")
    ap.add_argument("--unity-log", type=Path, required=True)
    ap.add_argument("--briefing-ref", default="", help="博士の台詞を読む git の版（既定は作業ツリー）")
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()

    run = Path(args.run) if Path(args.run).is_dir() else DRIVE / args.run
    video, log = run / "eye.mp4", run / "replay_xp.log"
    for need in (video, log, args.unity_log):
        if not need.is_file():
            raise SystemExit(f"無い: {need}")
    out = args.out or ROOT / "Logs" / "twin-playtest" / run.name
    frames = out / "frames"
    frames.mkdir(parents=True, exist_ok=True)
    for old in frames.glob("*.jpg"):
        old.unlink()

    events = [e for e in map(parse, log.read_text(encoding="utf-8").splitlines()) if e]
    spoken = comms_texts(args.unity_log)
    # 記録の頭より前（装着直後の練習と名乗り）は動画に出ない。文だけ先に渡す。
    first_run_comms = next((e for e in events if e.get("ev") == "comms"), None)
    onboarding, queue = [], list(spoken)
    while queue and first_run_comms and queue[0][0] != first_run_comms.get("id"):
        onboarding.append(queue.pop(0))

    moments = []   # (tau, 種類, 文)

    def add(tau, kind, text):
        moments.append((round(tau, 2), kind, text))

    last_scene = None
    for e in events:
        ev, tau = e.get("ev"), e["tau"]
        if ev == "comms":
            kind_id = e.get("id")
            text = ""
            while queue:
                qid, qtext = queue.pop(0)
                if qid == kind_id:
                    text = qtext
                    break
            delivery = e.get("delivery", "Typed")
            if kind_id == "Takeover":
                add(tau + 0.3, "画", "通信画面が勝手に開く。いつもの顔が出て、文字が一度に出る")
                add(tau + 0.3, "文字", f"「{text}」")
                add(tau + 3.2, "画", "顔と文字が、左から右へ塗り替わっていく。顔は日本人形の顔になり、文字は赤い字になる")
                add(tau + 3.2, "文字（赤い字・人形の顔）", f"「{LIE_JA}」")
                continue
            if delivery == "Cursed":
                add(tau + 0.8, "文字（赤い字・人形の顔。打つ音はしない）", f"「{LIE_JA}」")
                continue
            # 打つ出方は、打ち終わってから切る。③b は弱った起動の後に打つので、
            # ログの chars が 0 でも文の長さから待つ（待たないと打っている途中のコマになる）。
            typed = delivery == "Typed"
            length = len(re.sub(r"[\s／]", "", text))
            wait = length / 12.0 + (0.9 if kind_id == "Prompt" else 0.7) if typed else 1.0
            how = "カタカタと 1 字ずつ打たれる" if typed else "すっと浮かぶ"
            add(tau + wait, f"文字（{how}）", f"「{text}」")
        elif ev == "mark":
            add(tau, "あなた", "左手のボタンを 1 秒長押しして、報告した")
        elif ev == "sfx" and e.get("id") in SFX:
            add(tau, "音", SFX[e["id"]])
        elif ev == "commsError" and "step" in e:
            cap = ["WARNING", "WARNING ／ 不正アクセス", "WARNING ／ 不正アクセス ／ 接続元不明",
                   "WARNING ／ 不正アクセス ／ 接続元不明 ／ 遮断を執行（バーが伸びていく）",
                   "WARNING ／ 不正アクセス ／ 接続元不明 ／ 失敗"][min(int(e["step"]), 4)]
            add(tau + 0.4, "画（テレビいっぱいの警告）", cap)
        elif ev == "twinlap":
            seg = e.get("seg")
            if seg != last_scene:
                last_scene = seg
                add(tau + 1.2, "画", "（歩いて次の場所へ入った。テレビの映像が切り替わる）")
        elif ev in ("cue", "step", "farewell", "outro", "title", "intro", "guide", "swap"):
            add(tau + 0.7, "画", "")
        for pattern, text in ALWAYS:
            if re.search(pattern, e["raw"]):
                add(tau, "音", text)

    # 間が空いた所を埋める（7 秒より長い空白に 1 枚）。
    end = max(e["tau"] for e in events)
    times = sorted({m[0] for m in moments})
    filler, prev = [], 0.0
    for t in times + [end]:
        while t - prev > 7.0:
            prev += 6.0
            filler.append(prev)
        prev = t
    for t in filler:
        add(t, "画", "")
    moments.sort(key=lambda m: m[0])

    # 0.6 秒以内の出来事は 1 枚のコマにまとめる。
    groups = []
    for tau, kind, text in moments:
        if groups and tau - groups[-1]["tau"] <= 0.6:
            groups[-1]["items"].append((kind, text))
        else:
            groups.append({"tau": tau, "items": [(kind, text)]})

    lines = ["# 体験の記録", "",
             "展示会場で、ある VR 作品を 1 回だけ体験した人に届いたものを、届いた順に並べてある。", "",
             "## 1. 被る前 — タブレットの中の人物が話したこと（字幕）", ""]
    lines += [f"- {t}" for t in briefing_lines(args.briefing_ref)]
    lines += ["", "タブレットは最後に砂嵐で切れた。ヘッドセットを被り、左手にコントローラーを 1 つ持った。右手は手すりを持つ。", "",
              "## 2. 被った直後 — 暗闇の中に小さな通信画面が開き、文字が出た", ""]
    lines += [f"- 「{text}」" for _, text in onboarding]
    lines += ["", "## 3. そのあと — 見えたもの・読めた文字・聞こえた音", "",
              "コマは、目の前に見えていたものを時刻順に切ったもの。コマの間も映像は続いている。", ""]
    n = 0
    for g in groups:
        if g["tau"] > end + 0.5:
            continue
        n += 1
        name = f"{n:03d}.jpg"
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-ss", f"{g['tau']:.2f}", "-i", str(video),
                        "-frames:v", "1", "-vf", "crop=1180:860:370:220,scale=920:-2", "-q:v", "5", str(frames / name)], check=True)
        mm, ss = divmod(g["tau"], 60)
        lines.append(f"### {int(mm)}:{ss:04.1f}　frames/{name}")
        seen = set()
        for kind, text in g["items"]:
            if not text or (kind, text) in seen:
                continue
            seen.add((kind, text))
            lines.append(f"- {kind}: {text}")
        lines.append("")
    (out / "experience.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(json.dumps({"out": str(out), "frames": n, "onboarding": len(onboarding),
                      "unmatchedTexts": len(queue)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
