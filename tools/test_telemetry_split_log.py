"""割った [XP] 行（`[XP] + <続き>`）の、書き手と読み手を突き合わせる。

- 書き手は C# の TelemetryLineSplit（Quest の Unity は 1 回の Debug.Log を ≒1020 バイトで黙って切る）
- 読み手は analyze-xp-log.py の load_events / mix-from-log.py の parse / xp-evidence.py の load_rows
- 印（"+"）は C# の TelemetryLineSplit.ContinuationMark と対

    py -3.11 tools/test_telemetry_split_log.py
"""

import importlib.util
import re
import tempfile
import unittest
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "tools"


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, TOOLS / file)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


xp = load("xp_analyzer", "analyze-xp-log.py")
mix = load("mix_from_log", "mix-from-log.py")
evid = load("xp_evidence", "xp-evidence.py")

PRE = "08-27 12:08:56.580 I/Unity   (16305): "
LOG = "\n".join([
    PRE + "[XP] t=1.00 ev=boot build=x",
    PRE + "[XP] t=155.50 ev=sum fps=45.0 clMax=0.94",
    PRE + "[XP] + nzMax=1.00 ctrlR=1/1 sndCurse=0.40",
    PRE + "[XP] + hud=0 |c0 con=1 rx=30.0 |c1 con=1 rx=29.5",
    PRE + "[XP] t=156.00 ev=outro stage=Dark",
]) + "\n"


class SplitLogTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile("w", suffix=".log", delete=False, encoding="utf-8")
        self.tmp.write(LOG)
        self.tmp.close()

    def tearDown(self):
        Path(self.tmp.name).unlink()

    def test_analyzer_joins_the_continuations_into_one_event(self):
        events, _ = xp.load_events(self.tmp.name)
        self.assertEqual([e.get("ev") for e in events], ["boot", "sum", "outro"])
        s = events[1]
        self.assertEqual(s["clMax"], "0.94")
        self.assertEqual(s["nzMax"], "1.00")
        self.assertEqual(s["hud"], "0")
        self.assertEqual([c["_idx"] for c in s["_cams"]], ["0", "1"])
        self.assertEqual(s["_cams"][1]["rx"], "29.5")

    def test_mix_reader_keeps_the_sound_keys_from_the_continuation(self):
        _, sums = mix.parse(self.tmp.name)
        self.assertEqual(len(sums), 1)
        self.assertEqual(sums[0][1]["sndCurse"], "0.40")

    def test_evidence_reader_keeps_the_head_keys_from_the_continuation(self):
        rows, _ = evid.load_rows(self.tmp.name, datetime(2026, 8, 27))
        sum_rows = [r for r in rows if r["ev"].get("ev") == "sum"]
        self.assertEqual(len(sum_rows), 1)
        self.assertEqual(sum_rows[0]["ev"]["nzMax"], "1.00")
        self.assertNotIn("con", sum_rows[0]["ev"])  # カメラ欄は頭のキーに混ぜない

    def test_csharp_mark_matches_the_readers(self):
        src = (ROOT / "Assets/Scripts/Diagnostics/TelemetryLineSplit.cs").read_text(encoding="utf-8")
        m = re.search(r'ContinuationMark = "([^"]+)"', src)
        self.assertIsNotNone(m)
        self.assertEqual(m.group(1), xp.CONTINUATION)


if __name__ == "__main__":
    unittest.main()
