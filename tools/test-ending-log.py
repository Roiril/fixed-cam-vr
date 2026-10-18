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

    def report_events(self, ending="Trapped", play="1", seconds=(0.25, 0.75),
                      report_style=None, shown="4", typed="4", report_chars="4", sfx="1"):
        events = [
            {"ev": "outro", "stage": stage, "t": str(i), "ending": ending,
             "pw": "0", "rep": "0.98" if stage == "Done" else "0",
             "repBuilt": "1", "repSfx": sfx, "repChars": report_chars,
             **({"repStyle": report_style} if report_style else {})}
            for i, stage in enumerate(("Dark", "Report", "Done"))
        ]
        events.extend(
            {"ev": "sum", "t": str(i + 3), "_cams": [], "clMax": "0",
             "repShown": shown, "repTypeN": typed, "repChars": report_chars,
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
            {"ev": "comms", "id": "Begin", "t": "0"},
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

    def test_left_half_ending_can_interrupt_prompt(self):
        events = [
            {"ev": "comms", "id": "Halt", "t": "3", "cline": "0", "closing": "3"},
            {"ev": "outro", "stage": "Dark", "t": "4", "ending": "Trapped", "endingLeft": "1"},
        ]
        verdicts = self.verdicts(events)
        self.assertTrue(any(level == "OK" and "③b の未着" in text for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "③a は届いたのに③b" in text for level, text in verdicts))

    def test_left_half_ending_can_precede_halt(self):
        events = [
            {"ev": "comms", "id": "Begin", "t": "0"},
            {"ev": "seg", "t": "10", "lap": "4", "plap": "4", "cam": "0"},
            {"ev": "outro", "stage": "Dark", "t": "14", "ending": "Trapped", "endingLeft": "1"},
        ]
        self.assertTrue(any(level == "OK" and "③a の未着" in text
                            for level, text in self.verdicts(events)))

    def test_other_endings_do_not_excuse_missing_prompt(self):
        for ending, left in (("Trapped", "0"), ("Released", "1"), ("Interrupted", "1")):
            with self.subTest(ending=ending, left=left):
                events = [
                    {"ev": "comms", "id": "Halt", "t": "3", "cline": "0", "closing": "3"},
                    {"ev": "outro", "stage": "Dark", "t": "4", "ending": ending, "endingLeft": left},
                ]
                self.assertTrue(any(level == "FAIL" and "③a は届いたのに③b" in text
                                    for level, text in self.verdicts(events)))

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

    def test_roll_report_accepts_visible_text_without_type_sound(self):
        verdicts = self.verdicts(self.report_events(
            report_style="roll", shown="12", typed="nc", report_chars="12", sfx="0"))
        self.assertTrue(any(level == "OK" and "必要な 12 文字を表示した" in text
                            for level, text in verdicts))
        self.assertFalse(any(level == "FAIL" and "打鍵" in text for level, text in verdicts))

    def test_roll_report_warns_when_visible_text_is_incomplete(self):
        verdicts = self.verdicts(self.report_events(
            report_style="roll", shown="4", typed="nc", report_chars="12", sfx="0"))
        self.assertTrue(any(level == "WARN" and "4/12 文字" in text
                            for level, text in verdicts))

    def test_legacy_report_without_style_still_requires_type_sound(self):
        verdicts = self.verdicts(self.report_events(sfx="0"))
        self.assertTrue(any(level == "FAIL" and "打鍵の音源を掴めていない" in text
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


class CurrentXpLogTests(unittest.TestCase):
    def setUp(self):
        self.show = {
            "run": {"introEnabled": False},
            "cues": [{"id": "used"}, {"id": "unused"}],
            "timeline": {"schema": 3, "segments": [
                {"lap": 1, "camera": 0, "takes": [
                    {"id": "test", "steps": [{"cueId": "used"}]}]}]},
        }

    def analyze(self, events, others=()):
        return xp.analyze(events, list(others), xp.expected_from_show(self.show))

    def has(self, verdicts, level, text):
        return any(lv == level and text in message for lv, message in verdicts)

    def notices(self):
        return [{"ev": "comms", "id": kind, "t": str(i), "chars": "0"}
                for i, kind in enumerate(("Walk", "Begin"))]

    def test_baked_config_uses_referenced_cues_but_live_keeps_all(self):
        for src, count, wanted_level in (("baked", "1", "OK"), ("baked", "2", "FAIL"),
                                          ("live", "2", "OK"), ("live", "1", "FAIL")):
            with self.subTest(src=src, count=count):
                rep, verdicts = self.analyze([{"ev": "config", "src": src, "cues": count}])
                self.assertTrue(self.has(verdicts, wanted_level, "実機は show.json" if wanted_level == "OK"
                                         else "実機の設定"))
                self.assertNotIn("reset-config", "\n".join(rep + [t for _, t in verdicts]))

    def test_baked_cue_references_match_exporter_rules(self):
        self.show.update({"schedule": {"entries": [{"cueId": "unused"}]},
                          "control": {"activeCue": "active"}})
        seg = self.show["timeline"]["segments"][0]
        seg.update({"cues": [{"cueId": "legacy"}], "hasInsert": True,
                    "insert": {"cueId": "insert"}})
        take = seg["takes"][0]
        take["markStartCueId"] = "mark"
        take["steps"][0]["overlay2CueId"] = "overlay"
        self.show["cues"].extend({"id": name} for name in ("active", "legacy", "insert", "mark", "overlay"))
        self.assertEqual(xp.baked_cue_count(self.show), 6)
        self.show["timeline"]["schema"] = 2
        self.assertEqual(xp.baked_cue_count(self.show), 7)
        self.assertEqual(xp.baked_cue_count({"cues": self.show["cues"]}), 7)

    def test_begin_is_required_and_beginhow_is_optional(self):
        _, verdicts = self.analyze(self.notices())
        self.assertTrue(self.has(verdicts, "OK", "本編開始の連絡 Begin"))
        self.assertFalse(self.has(verdicts, "FAIL", "①の連絡"))
        _, verdicts = self.analyze([{"ev": "comms", "id": "BeginHow"}])
        self.assertTrue(self.has(verdicts, "FAIL", "本編開始の連絡 Begin"))

    def test_automation_skips_greeting_but_manual_missing_greeting_fails(self):
        for others, level in (([("XPWalk", "経路: 0->1 1->2")], "WARN"), ([], "FAIL")):
            with self.subTest(others=others):
                _, verdicts = self.analyze(self.notices(), others)
                self.assertTrue(self.has(verdicts, level, "自己紹介"))
                self.assertFalse(self.has(verdicts, "OK", "名乗り"))

    def test_recovery_allows_current_boot_power_and_keeps_other_checks(self):
        good = {"ev": "commsRecover", "st": "end", "powMin": "0.30",
                "resMax": "0.5", "resEnd": "0.2", "redMax": "2", "tearMax": "0.6"}
        for changes, level in (({}, "OK"), ({"powMin": "0.31"}, "FAIL"),
                               ({"resMax": "0"}, "FAIL"), ({"redMax": "0"}, "FAIL"),
                               ({"tearMax": "0"}, "FAIL")):
            with self.subTest(changes=changes):
                _, verdicts = self.analyze(self.notices() + [dict(good, **changes)])
                self.assertTrue(self.has(verdicts, level, "弱った起動" if level == "OK" else "復帰の姿"))

    def test_async_character_observation_does_not_prove_duplicate_typing(self):
        # Quest α 2026-10-18: typeN=93、通知の chars 合計=65。
        for count, level in (("93", "WARN"), ("0", "FAIL")):
            with self.subTest(count=count):
                events = self.notices()
                events[0]["chars"] = "65"
                events.append({"ev": "sum", "t": "2", "_cams": [], "typeN": count})
                rep, verdicts = self.analyze(events)
                self.assertTrue(self.has(verdicts, level, "打鍵"))
                if count == "93":
                    self.assertTrue(self.has(verdicts, "WARN", "照合は保留"))
                    self.assertFalse(self.has(verdicts, "FAIL", "打鍵"))
                    self.assertIn("typeN=93", "\n".join(rep))

    def test_panel_material_conjunction_does_not_identify_missing_background(self):
        _, verdicts = self.analyze(self.notices() + [
            {"ev": "sum", "t": "2", "_cams": [], "commsBg": "0", "commsCurse": "1/0/0"}])
        self.assertTrue(self.has(verdicts, "FAIL", "背景または縦罫線"))
        self.assertFalse(any("宙に浮く" in text for _, text in verdicts))

    def test_record_line_only_requires_its_camera_and_observed_crossing(self):
        self.show["record"] = {"enabled": True, "laps": [1], "startLineId": "rec_line"}
        self.show["layout"] = {"lines": [{"id": "rec_line", "camera": 0}]}
        for start, crossed, level in (("0", False, "WARN"), ("0", True, "FAIL"), ("1", True, "OK")):
            with self.subTest(start=start, crossed=crossed):
                events = [{"ev": "rec", "v": "stop", "lap": "1", "cam": str(cam),
                           "start": start if cam == 0 else "0", "frames": "78"}
                          for cam in range(3)]
                others = [("SegmentRecorder", "録り始めの線を横切った line=rec_line L1C0 pos=(0,0)")]
                _, verdicts = self.analyze(events, others if crossed else [])
                self.assertTrue(self.has(verdicts, level, "録り始めの線"))
                if level == "OK":
                    self.assertTrue(self.has(verdicts, "OK", "1/1 区間"))
                    self.assertFalse(self.has(verdicts, "WARN", "横断を確認できない"))

    def test_screenon_is_in_video_but_bell_and_shatter_are_required(self):
        for sounds, missing in ((("Bell", "Shatter"), False), (("Shatter",), True)):
            with self.subTest(sounds=sounds):
                events = [{"ev": "intro", "stage": "Swap", "t": "1"}]
                events.extend({"ev": "sfx", "id": sound, "t": "1"} for sound in sounds)
                _, verdicts = self.analyze(events)
                self.assertFalse(self.has(verdicts, "FAIL", "ScreenOn"))
                self.assertEqual(self.has(verdicts, "FAIL", "鈴"), missing)
                self.assertTrue(self.has(verdicts, "WARN", "音声出力は未検証"))


if __name__ == "__main__":
    unittest.main()
