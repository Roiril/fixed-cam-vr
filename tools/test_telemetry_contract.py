"""実機のログ（[XP] 行）の書き手と読み手を突き合わせる。

書き手は C#（ShowTelemetryHost ほか）、読み手は tools/analyze-xp-log.py。キー名は両側に文字列で書かれていて、
片方だけ直すと解析器が「無いキー」を読み続け、判定が黙って空振りする（2026-11-03 の lap= の意味の食い違い、
同じ値どうしを比べていた報告数の照合はどちらもこの形だった）。

ここで確かめること:
  1. 解析器が読む ev 名とキー名は、どれも C# のどこかが書いている
  2. 書かれなくなったキーを読み続けるなら、LEGACY_KEYS に「なぜ残すか」を書いてある（古いログを読むため等）

C# 側のキーは、文字列リテラルの中の「名前=」をすべて拾う（Emit の連結・_sb.Append(" key=") の両方を含む）。
読み手側のキーは e.get("k") / fnum(e, "k") / *_samples(events, "k") / get("ev") == "k" を拾う。

    py -3.11 tools/test_telemetry_contract.py
"""

import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EMITTERS = [
    "Assets/Scripts/Diagnostics/ShowTelemetryHost.cs",
    "Assets/Scripts/Diagnostics/CommsTakeoverAudio.cs",
    "Assets/Scripts/Diagnostics/CommsTakeoverError.cs",
    "Assets/Scripts/Streaming/Replay/ExperienceRecorder.cs",
    "Assets/Scripts/OvrBridge/XpTwinDriver.cs",   # ev=clock（固定刻みかどうか。解析器が対象外を決める）
]
ANALYZER = ROOT / "tools" / "analyze-xp-log.py"

# 解析器が読むが、いまの C# は書かないキー。古いログを読むため・解析器が自分で足すため、など理由を必ず書く。
LEGACY_KEYS = {
}

# 解析器がログ以外（show.json）を、ログと同じ変数名で読んでいるキー。ログのキーではない。
NOT_LOG_KEYS = {
    "cueId": "expected_from_show が schedule.entries を e で回している",
}

STRING_LITERAL = re.compile(r'\$?@?"((?:[^"\\\n]|\\.)*)"')
KEY_IN_STRING = re.compile(r"(?:^|[\s(])([A-Za-z_][A-Za-z0-9_]*)=")


def emitted_keys():
    keys = set()
    for rel in EMITTERS:
        text = (ROOT / rel).read_text(encoding="utf-8")
        for lit in STRING_LITERAL.findall(text):
            keys.update(KEY_IN_STRING.findall(lit))
    return keys


# 解析器の中で「ログの 1 行（イベント）」を指している変数名。show.json を読む変数（show / exp / t / seg / run …）は
# 同じ .get() を使うので、ここに無い名前は数えない。`s` は演出の段（step）にも ev=seg の行にも使われていて区別できないので外す。
EVENT_VARS = r"(?:e|m|p|nxt|last|first|full|chained|near\[0\]|trail_ev|halt_ev|spot_ev2|smp|boot|take_evs\d?\[-1\])"


def analyzer_reads():
    text = ANALYZER.read_text(encoding="utf-8")
    keys = set()
    for pat in (
        r"\b" + EVENT_VARS + r'\.get\("([A-Za-z_][A-Za-z0-9_]*)"',
        r"\bfnum\(" + EVENT_VARS + r', "([A-Za-z_][A-Za-z0-9_]*)"',
        r'_samples\(events, "([A-Za-z_][A-Za-z0-9_]*)"',
    ):
        keys.update(re.findall(pat, text))
    # ev 名: get("ev") == "x" と、get("ev") in ("x", "y") の両方
    evs = set(re.findall(r'get\("ev"\) (?:==|!=) "([A-Za-z_][A-Za-z0-9_]*)"', text))
    for group in re.findall(r'get\("ev"\) in \(([^)]*)\)', text):
        evs.update(re.findall(r'"([A-Za-z_][A-Za-z0-9_]*)"', group))
    return keys, evs


class TelemetryContractTests(unittest.TestCase):
    def test_every_key_the_analyzer_reads_is_written_somewhere(self):
        written = emitted_keys()
        reads, _ = analyzer_reads()
        # 解析器が行を分解するときに自分で付ける項目（ログには書かれない）
        internal = {"ev", "t", "_cams", "_raw", "_line"}
        missing = sorted(k for k in reads - written - internal if k not in LEGACY_KEYS and k not in NOT_LOG_KEYS)
        self.assertEqual(missing, [], "解析器が読むのに C# が書いていないキー（判定が黙って空振りする）。"
                                      "C# 側で書くか、古いログのためなら LEGACY_KEYS に理由つきで足す: "
                                      + ", ".join(missing))

    def test_every_event_the_analyzer_filters_on_is_written(self):
        written_text = "\n".join((ROOT / rel).read_text(encoding="utf-8") for rel in EMITTERS)
        _, evs = analyzer_reads()
        missing = sorted(ev for ev in evs if f"ev={ev}" not in written_text and ev not in LEGACY_KEYS)
        self.assertEqual(missing, [], "解析器が探す ev を C# が 1 度も書いていない: " + ", ".join(missing))

    def test_legacy_keys_are_really_not_written_anymore(self):
        written = emitted_keys()
        stale = sorted(k for k in LEGACY_KEYS if k in written)
        self.assertEqual(stale, [], "LEGACY_KEYS にあるのに今も書かれているキー。表から外す: " + ", ".join(stale))

    def test_the_scanner_finds_known_keys(self):
        # 計器の校正: 確かに書いているキーを拾えること、確かに読んでいるキーを拾えること
        written = emitted_keys()
        reads, evs = analyzer_reads()
        for k in ("plap", "repN", "anomalies", "marks", "sndSwap", "promptReady"):
            self.assertIn(k, written, k)
        for k in ("plap", "repN", "anom", "sndSwap"):
            self.assertIn(k, reads, k)
        for ev in ("mark", "outro", "seg", "zone"):
            self.assertIn(ev, evs, ev)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    unittest.main()
