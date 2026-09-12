"""Export the tablet's actual dialogue and timings for later media production."""
import json
from pathlib import Path
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/Resources/Visitor/briefing-v1.json.bytes"
OUTPUT = Path(__file__).resolve().parent
LANGUAGES = ("ja", "en", "fr")


def timestamp(ms):
    seconds, millis = divmod(ms, 1000)
    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02}:{minutes:02}:{seconds:02},{millis:03}"


def write_atomic(path, content):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(content, encoding="utf-8", newline="\n")
    temporary.replace(path)


def main():
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    assert data["schemaVersion"] == 1 and data["scenes"]
    for scene in data["scenes"]:
        assert (ROOT / "Assets/Resources/Visitor" / (scene["image"] + ".bytes")).is_file()
        for language in LANGUAGES:
            assert scene["cues"][language]
            for cue in scene["cues"][language]:
                assert cue["text"].strip() and type(cue["durationMs"]) is int and cue["durationMs"] > 0

    script = ["# 博士の導入台本", "",
              "案 / 2026-09-12 / シュビー。台詞と画面の演出は未判定の実装案。", "",
              "正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。この文書と SRT はそこから生成する。",
              "博士の役割は事前説明。装着後の支援は既存のエージェントが担当する。", "",
              "音声と動画は未制作。以下の時間は静止画版の仮の尺。完成した音声に合わせて JSON の durationMs を調整する。",
              "動画は既存博士画像と同じ人物と画角を使用する。映像の切替や字幕を動画に焼き込まない。", "",
              "各場面を個別に音声化する。ファイル名は `場面ID-言語.mp3` または `場面ID-言語.mp4` を推奨する。",
              "音声付き博士動画を使う場合は video のみ指定する。同じ音声を audio にも指定しない。", ""]
    totals = {}
    for language in LANGUAGES:
        offset = 0
        subtitles = []
        cue_index = 1
        script.extend([f"## {language}", ""])
        for index, scene in enumerate(data["scenes"], 1):
            scene_duration = sum(c["durationMs"] for c in scene["cues"][language])
            script.extend([f"### {index:02} {scene['id']} — {scene['title'][language]}", "",
                           f"開始 {offset / 1000:.1f} 秒 / 尺 {scene_duration / 1000:.1f} 秒", "",
                           f"画像: `{scene['image']}`", ""])
            for cue in scene["cues"][language]:
                end = offset + cue["durationMs"]
                subtitles.append(f"{cue_index}\n{timestamp(offset)} --> {timestamp(end)}\n{cue['text']}\n")
                script.extend([cue["text"], ""])
                cue_index += 1
                offset = end
        totals[language] = offset / 1000
        write_atomic(OUTPUT / f"briefing-{language}.srt", "\n".join(subtitles))
    write_atomic(OUTPUT / "briefing-narration.md", "\n".join(script))
    print(json.dumps({"scenes": len(data["scenes"]), "durationSeconds": totals, "output": str(OUTPUT)}))


if __name__ == "__main__":
    main()
