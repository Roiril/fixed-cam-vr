"""quest-shots.py の判断部分（機の選択・フォルダ名の拾い方）を固定する。

実行: py -3.11 -m unittest discover -s tools -p "test_quest_shots.py"

固定するのは 2 つ。
- **複数の Quest が繋がっているとき、黙って選ばない。** 別の機の写真を「これだ」と読ませない
  （この機体には α と β の 2 台が繋がる）
- **撮影セット以外（ON / OFF の印）を、セットとして数えない。** 数えると取り出しが印を
  フォルダとして開こうとして失敗し、`--delete` が印まで消す形になる
"""
import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load():
    spec = importlib.util.spec_from_file_location("quest_shots", os.path.join(_HERE, "quest-shots.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


shots = _load()

DEVICES_TWO = """List of devices attached
2G0YC1ZF890864         device usb:1-1 product:eureka model:Quest_3 device:eureka transport_id:1
2G0YC1ZF7S06BW         device usb:1-2 product:eureka model:Quest_3 device:eureka transport_id:2
"""

DEVICES_MIXED = """List of devices attached
2G0YC1ZF890864         device usb:1-1 product:eureka model:Quest_3 device:eureka transport_id:1
1A2B3C4D5E             device usb:1-3 product:lynx model:Pixel_7a device:lynx transport_id:3
"""

DEVICES_UNAUTH = """List of devices attached
2G0YC1ZF890864         unauthorized usb:1-1 transport_id:1
"""


class PickSerialTests(unittest.TestCase):
    def test_single_quest_is_picked_without_asking(self):
        serial, err = shots.pick_serial(None, shots.parse_devices(DEVICES_MIXED))
        self.assertEqual("2G0YC1ZF890864", serial)
        self.assertIsNone(err)

    def test_two_quests_are_never_guessed(self):
        serial, err = shots.pick_serial(None, shots.parse_devices(DEVICES_TWO))
        self.assertIsNone(serial)
        self.assertIn("--serial", err)
        self.assertIn("2G0YC1ZF890864", err)
        self.assertIn("2G0YC1ZF7S06BW", err)

    def test_explicit_serial_wins(self):
        serial, err = shots.pick_serial("2G0YC1ZF7S06BW", shots.parse_devices(DEVICES_TWO))
        self.assertEqual("2G0YC1ZF7S06BW", serial)
        self.assertIsNone(err)

    def test_phone_is_not_a_target(self):
        names = [d["serial"] for d in shots.parse_devices(DEVICES_MIXED)]
        self.assertNotIn("1A2B3C4D5E", names)

    def test_unauthorized_quest_is_not_ready(self):
        devs = shots.parse_devices(DEVICES_UNAUTH)
        self.assertEqual(1, len(devs), "model が読めない unauthorized の機は落とさない")
        serial, err = shots.pick_serial(None, devs)
        self.assertIsNone(serial)
        self.assertIn("繋がっていない", err)


class ParseSetsTests(unittest.TestCase):
    def test_only_set_folders_are_counted(self):
        out = "20260930_142315_001\n20260930_142400_002\nON\nOFF\nnotes.txt\n"
        self.assertEqual(["20260930_142315_001", "20260930_142400_002"], shots.parse_sets(out))

    def test_oldest_first(self):
        out = "20260930_150000_003\n20260930_142315_001\n"
        self.assertEqual(["20260930_142315_001", "20260930_150000_003"], shots.parse_sets(out))

    def test_matches_the_unity_folder_name(self):
        # ExperienceShotLogic.SetFolderName の例と同じ形。
        self.assertTrue(shots.SET_RE.match("20260930_142315_001"))
        self.assertFalse(shots.SET_RE.match("20260930_142315"))


if __name__ == "__main__":
    unittest.main()
