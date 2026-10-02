"""Quest fleet と共有 ADB 列挙の回帰テスト。"""

import argparse
import importlib.util
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import android_devices


SCRIPT = pathlib.Path(__file__).with_name("quest-fleet.py")
SPEC = importlib.util.spec_from_file_location("quest_fleet", SCRIPT)
qf = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(qf)


class AndroidDeviceTests(unittest.TestCase):
    def test_usb_transport_wins_when_wifi_is_same_physical_device(self):
        output = ("List of devices attached\n"
                  "USB1 device product:x model:Quest_3 transport_id:1\n"
                  "192.168.10.31:5555 device product:x model:Quest_3 transport_id:2\n")

        def run(cmd, timeout=20):
            if cmd[-2:] == ["devices", "-l"]:
                return 0, output, ""
            return 0, "PHYSICAL1\n", ""

        devices = android_devices.list_android_devices(run, "adb.exe", ["PHYSICAL1"])
        self.assertEqual(len(devices), 1)
        self.assertEqual(devices[0]["serial"], "USB1")
        self.assertEqual(devices[0]["physicalSerial"], "PHYSICAL1")

    def test_connected_wifi_wins_over_unauthorized_usb_for_same_physical_device(self):
        output = ("List of devices attached\n"
                  "PHYSICAL1 unauthorized usb:1-1 transport_id:1\n"
                  "192.168.10.31:5555 device product:x model:Quest_3 transport_id:2\n")

        def run(cmd, timeout=20):
            if cmd[-2:] == ["devices", "-l"]:
                return 0, output, ""
            return 0, "PHYSICAL1\n", ""

        devices = android_devices.list_android_devices(run, "adb.exe", ["PHYSICAL1"])
        self.assertEqual(1, len(devices))
        self.assertEqual("192.168.10.31:5555", devices[0]["serial"])
        self.assertEqual("device", devices[0]["state"])

    def test_adb_environment_path_has_priority(self):
        with tempfile.TemporaryDirectory() as folder:
            configured = pathlib.Path(folder, "adb.exe")
            configured.write_bytes(b"")
            with mock.patch.dict(os.environ, {"ADB": str(configured)}, clear=False), \
                    mock.patch.object(android_devices.shutil, "which", return_value="other.exe"):
                self.assertEqual(android_devices.resolve_adb(), str(configured.resolve()))


class QuestFleetTests(unittest.TestCase):
    def test_sleep_others_excludes_same_physical_device_for_wifi_alias(self):
        devices = [
            {"serial": "USB-A", "physicalSerial": "A", "state": "device", "isQuest": True},
            {"serial": "USB-B", "physicalSerial": "B", "state": "device", "isQuest": True},
        ]
        with mock.patch.object(qf, "list_devices", return_value=devices), \
                mock.patch.object(qf, "adb", return_value=(0, "", "")) as adb, \
                mock.patch.object(qf, "sleep_device", return_value="Asleep") as sleep:
            rc = qf.cmd_sleep(argparse.Namespace(others="A", serial=None))
        self.assertEqual(rc, 0)
        sleep.assert_called_once_with("USB-B")
        self.assertTrue(all(call.args[0] == "USB-B" for call in adb.call_args_list))

    def test_streaming_assets_participate_in_apk_freshness(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            source = root / "StreamingAssets" / "show.json"
            source.parent.mkdir()
            source.write_text("{}", encoding="utf-8")
            apk = root / "mawarimi.apk"
            apk.write_bytes(b"apk")
            os.utime(apk, (1, 1))
            os.utime(source, (1000, 1000))
            with mock.patch.object(qf, "SOURCE_DIRS", [str(source.parent)]), \
                    mock.patch.object(qf, "BUILD_TOUCH_SLACK_SEC", 0):
                _, stale = qf.apk_freshness(str(apk))
        self.assertTrue(stale)


if __name__ == "__main__":
    unittest.main()
