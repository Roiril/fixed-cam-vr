"""終幕の実機ログ判定を人工イベントで校正する。"""

import importlib.util
import json
from pathlib import Path
import sys
import unittest


TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("analyze_xp_log", TOOLS / "analyze-xp-log.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)


class EndingLogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with (TOOLS / "web-compositor" / "show.json").open(encoding="utf-8") as fh:
            cls.exp = xp.expected_from_show(json.load(fh))
        cls.exp["closingLineId"] = "test_line"

    def verdicts(self, events):
        return xp.analyze(events, [], self.exp)[1]

    def report_events(self, ending="Trapped", play="1", seconds=(0.25, 0.75)):
        events = [
            {"ev": "outro", "stage": stage, "t": str(i), "ending": ending,
             "pw": "0", "rep": "0.98" if stage == "Done" else "0",
             "repBuilt": "1", "repSfx": "1", "repChars": "12"}
            for i, stage in enumerate(("Dark", "Report", "Done"))
        ]
        events.extend(
            {"ev": "sum", "t": str(i + 3), "_cams": [], "clMax": "0",
             "repShown": "4", "repTypeN": "4", "repChars": "4",
             "repMusicBuilt": "1", "repMusicPlay": play,
             "repMusicVol": "0.2", "repMusicSec": str(sec)}
            for i, sec in enumerate(seconds)
        )
        return events

    def test_halt_by_line_without_line_event(self):
        events = [
            {"ev": "comms", "id": "Halt", "t": "1", "cline": "1", "closing": "0.5"},
            {"ev": "comms", "id": "Prompt", "t": "2", "closing": "1.5"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "OK" and "cline=1" in text for level, text in verdicts))
        self.assertTrue(any(level == "OK" and "③b" in text for level, text in verdicts))

    def test_halt_by_three_second_clock_with_line_in_show(self):
        events = [
            {"ev": "comms", "id": "Halt", "t": "3", "cline": "0", "closing": "3.0"},
            {"ev": "comms", "id": "Prompt", "t": "4", "closing": "4.0"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "OK" and "3.00s" in text for level, text in verdicts))

    def test_halt_before_line_and_clock_fails(self):
        events = [{"ev": "comms", "id": "Halt", "t": "2.9", "cline": "0", "closing": "2.9"}]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "FAIL" and "closing>=3" in text for level, text in verdicts))

    def test_log_cut_before_three_seconds_does_not_fail_halt(self):
        events = [
            {"ev": "seg", "t": "0", "lap": "4", "plap": "4", "cam": "0"},
            {"ev": "comms", "id": "MarkNothing", "t": "2", "cline": "0", "closing": "2"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "WARN" and "3 秒未満" in text for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "③a『止まってください！』が届いていない" in text
                             for level, text in verdicts))

    def test_recent_notice_can_delay_halt_after_three_seconds(self):
        events = [
            {"ev": "seg", "t": "0", "lap": "4", "plap": "4", "cam": "0"},
            {"ev": "comms", "id": "MarkNothing", "t": "3.1", "cline": "0", "closing": "3.1"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "WARN" and "読了待ち" in text for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "③a『止まってください！』が届いていない" in text
                             for level, text in verdicts))

    def test_halt_missing_after_three_seconds_without_recent_notice_fails(self):
        events = [
            {"ev": "comms", "id": "BeginHow", "t": "0"},
            {"ev": "seg", "t": "10", "lap": "4", "plap": "4", "cam": "0"},
            {"ev": "sum", "t": "13.1", "_cams": []},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "FAIL" and "3 秒以上" in text for level, text in verdicts))

    def test_prompt_before_halt_still_fails(self):
        events = [
            {"ev": "comms", "id": "Prompt", "t": "1"},
            {"ev": "comms", "id": "Halt", "t": "3", "cline": "0", "closing": "3"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "FAIL" and "③b が③a" in text for level, text in verdicts))

    def test_released_mark_uses_typed_logged_reply_without_curse_requirement(self):
        events = [
            {"ev": "mark", "t": "10", "lap": "4", "det": "1", "res": "1",
             "invasion": "1", "released": "1", "promptReady": "1"},
            {"ev": "comms", "id": "MarkLogged", "t": "10.1", "invasion": "1",
             "released": "1", "promptReady": "1", "delivery": "Typed", "chars": "8"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "OK" and "MarkLogged がスイの通常印字" in text
                            for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "斑が一度も目標へ" in text
                             for level, text in verdicts))

    def test_released_mark_possessed_reply_fails(self):
        events = [
            {"ev": "mark", "t": "10", "lap": "4", "det": "1", "res": "1",
             "invasion": "1", "released": "1", "promptReady": "1"},
            {"ev": "comms", "id": "MarkLogged", "t": "10.1", "invasion": "1",
             "released": "1", "promptReady": "1", "delivery": "Possessed", "chars": "0"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "FAIL" and "MarkLogged/Typed ではない" in text
                            for level, text in verdicts))

    def test_closing_mark_before_prompt_readable_fails(self):
        events = [
            {"ev": "mark", "t": "10", "lap": "4", "det": "1", "res": "0",
             "invasion": "1", "released": "0", "promptReady": "0"},
            {"ev": "comms", "id": "MarkLogged", "t": "10.1", "invasion": "1",
             "released": "0", "promptReady": "0", "delivery": "Possessed", "chars": "0"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "FAIL" and "promptReady=0" in text
                            for level, text in verdicts))

    def test_legacy_closing_mark_without_prompt_ready_remains_compatible(self):
        events = [
            {"ev": "mark", "t": "10", "lap": "4", "det": "1", "res": "0", "invasion": "1"},
            {"ev": "comms", "id": "MarkLogged", "t": "10.1", "invasion": "1",
             "delivery": "Possessed", "chars": "0"},
        ]
        verdicts = self.verdicts(events)
        self.assertFalse(any(level == "FAIL" and "promptReady=0" in text
                             for level, text in verdicts))

    def test_report_music_advances_and_visible_char_count_is_used(self):
        verdicts = self.verdicts(self.report_events())
        self.assertTrue(any(level == "OK" and "結果曲の再生位置が進んだ" in text
                            for level, text in verdicts))
        self.assertTrue(any(level == "OK" and "報告が 4 文字ぶん打たれ" in text
                            for level, text in verdicts))

    def test_report_music_play_zero_fails_despite_time_samples(self):
        verdicts = self.verdicts(self.report_events(play="0"))
        self.assertTrue(any(level == "FAIL" and "結果画面の曲が再生されていない" in text
                            for level, text in verdicts))
        self.assertFalse(any(level == "OK" and "結果曲の再生位置が進んだ" in text
                             for level, text in verdicts))

    def test_report_music_stationary_is_not_accepted_as_playing(self):
        verdicts = self.verdicts(self.report_events(seconds=(0.25, 0.25)))
        self.assertTrue(any(level == "WARN" and "再生位置の前進を確認できない" in text
                            for level, text in verdicts))
        self.assertFalse(any(level == "OK" and "結果曲の再生位置が進んだ" in text
                             for level, text in verdicts))

    def test_trapped_dark_report_done_does_not_require_collapse(self):
        verdicts = self.verdicts(self.report_events())
        self.assertTrue(any(level == "OK" and "帰還不能では通常映像を戻さず暗転する" in text
                            for level, text in verdicts))
        self.assertFalse(any(level in ("WARN", "FAIL") and "Collapse" in text
                             for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "電源断" in text for level, text in verdicts))

    def test_released_without_collapse_is_still_flagged(self):
        verdicts = self.verdicts(self.report_events(ending="Released"))
        self.assertTrue(any(level == "WARN" and "終幕の段 Collapse が出ていない" in text
                            for level, text in verdicts))
        self.assertTrue(any(level == "FAIL" and "電源断が 1 画素も" in text
                            for level, text in verdicts))


if __name__ == "__main__":
    unittest.main()
