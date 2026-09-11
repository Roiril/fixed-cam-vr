"""機ごとの heartbeat の整形（unity_devices.py）を stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import unity_devices as ud  # noqa: E402


class DeviceRowsTest(unittest.TestCase):
    def test_rows_alive_and_fields(self):
        devices = {
            'A111111': {'at': 100.0, 'deviceModel': 'Quest 3', 'localIp': '192.168.10.31', 'phase': 'RUN',
                        'titleStage': 'Done', 'lang': 'ja', 'visitorPort': 8090, 'visitorReceived': 3,
                        'visitorPending': False, 'appliedRev': 1160},
            'B222222': {'at': 90.0, 'deviceModel': 'Quest 3', 'localIp': '192.168.10.32', 'phase': 'INTRO',
                        'titleStage': 'Wait', 'lang': 'fr', 'relief': True, 'visitorPort': 0},
        }
        out = ud.device_rows(devices, now=102.0)
        by = {r['deviceId']: r for r in out['devices']}
        self.assertEqual([r['deviceId'] for r in out['devices']], ['A111111', 'B222222'])  # IP 順
        self.assertTrue(by['A111111']['alive'])        # 2 秒前
        self.assertFalse(by['B222222']['alive'])       # 12 秒前
        self.assertEqual(by['A111111']['shortId'], 'A11111')
        self.assertEqual(by['A111111']['visitorPort'], 8090)
        self.assertEqual(by['A111111']['visitorReceived'], 3)
        self.assertEqual(by['B222222']['visitorPort'], 0)   # 口を開けていない機
        self.assertTrue(by['B222222']['relief'])
        self.assertEqual(by['B222222']['titleStage'], 'Wait')
        self.assertEqual(by['B222222']['appliedRev'], -1)

    def test_old_apk_without_fields_still_rows(self):
        out = ud.device_rows({'X': {'at': 5.0}}, now=6.0)
        r = out['devices'][0]
        self.assertEqual(r['lang'], 'ja')
        self.assertFalse(r['relief'])
        self.assertEqual(r['visitorPort'], 0)
        self.assertTrue(r['alive'])

    def test_no_devices(self):
        self.assertEqual(ud.device_rows({}, now=0.0), {'devices': []})


if __name__ == '__main__':
    unittest.main()
