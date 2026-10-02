import argparse
import tempfile
import unittest
import json
import sys
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import exhibit_setup as setup
import onsite


class SetupTests(unittest.TestCase):
    @staticmethod
    def backup(**values):
        return {key: {'namespace': namespace, 'value': values.get(key, '0')}
                for key, (namespace, _) in setup.SETTINGS.items()}

    def test_locked_or_unknown_display_does_not_pass(self):
        self.assertEqual({'awake': True, 'unlocked': False}, setup.display_state('mWakefulness=Awake', 'mIsShowing=true'))
        self.assertEqual({'awake': False, 'unlocked': False}, setup.display_state('', ''))
        self.assertEqual({'awake': True, 'unlocked': True}, setup.display_state('mWakefulness=Awake', 'mIsShowing=false'))
    def test_only_registered_serials_and_selected_quest_are_used(self):
        fleet = {'quests': [{'id': 'alpha', 'serial': 'Q-A'}, {'id': 'beta', 'serial': 'Q-B'}],
                 'tablets': [{'questId': 'alpha', 'serial': 'T-A'}, {'questId': 'beta', 'serial': 'T-B'}]}
        devices = [{'physicalSerial': 'Q-A', 'state': 'unauthorized'},
                   {'physicalSerial': 'UNKNOWN', 'state': 'device'},
                   {'physicalSerial': 'T-A', 'state': 'device'}]
        quests, tablets, available = setup.choose_devices(fleet, devices, 'alpha')
        self.assertEqual(['Q-A'], [q['serial'] for q in quests])
        self.assertEqual(['T-A'], [t['serial'] for t in tablets])
        self.assertNotIn('Q-A', available)

    def test_missing_registered_device_is_rescanned_once_and_can_reconnect(self):
        fleet = {'quests': [{'id': 'alpha', 'serial': 'Q-A'}],
                 'tablets': [{'questId': 'alpha', 'serial': 'T-A'}]}
        first = [{'physicalSerial': 'Q-A', 'state': 'device'}]
        second = first + [{'physicalSerial': 'T-A', 'state': 'device'}]
        with patch.object(setup, 'list_android_devices', side_effect=[first, second]) as scan, \
                patch.object(setup.time, 'sleep') as sleep:
            devices = setup.scan_setup_devices('adb', fleet, 'alpha')
        self.assertEqual(second, devices)
        self.assertEqual(2, scan.call_count)
        sleep.assert_called_once_with(.5)

    def test_missing_registered_device_remains_missing_after_one_rescan(self):
        fleet = {'quests': [{'id': 'alpha', 'serial': 'Q-A'}],
                 'tablets': [{'questId': 'alpha', 'serial': 'T-A'}]}
        observed = [{'physicalSerial': 'Q-A', 'state': 'device'}]
        with patch.object(setup, 'list_android_devices', side_effect=[observed, observed]) as scan, \
                patch.object(setup.time, 'sleep'):
            devices = setup.scan_setup_devices('adb', fleet, 'alpha')
        _, _, available = setup.choose_devices(fleet, devices, 'alpha')
        self.assertEqual(2, scan.call_count)
        self.assertNotIn('T-A', available)

    def test_complete_registered_devices_are_not_rescanned(self):
        fleet = {'quests': [{'id': 'alpha', 'serial': 'Q-A'}],
                 'tablets': [{'questId': 'alpha', 'serial': 'T-A'}]}
        observed = [{'physicalSerial': 'Q-A', 'state': 'device'},
                    {'physicalSerial': 'T-A', 'state': 'device'}]
        with patch.object(setup, 'list_android_devices', return_value=observed) as scan, \
                patch.object(setup.time, 'sleep') as sleep:
            self.assertEqual(observed, setup.scan_setup_devices('adb', fleet, 'alpha'))
        scan.assert_called_once()
        sleep.assert_not_called()

    def test_port_open_is_not_sufficient_to_identify_desk(self):
        with patch.object(onsite, 'listening_pids', return_value=[123]), patch.object(onsite, 'get_json', return_value={'hello': 'other'}):
            self.assertEqual(1, onsite.cmd_serve(argparse.Namespace()))

    def test_restore_validates_all_keys_before_writing(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            (evidence / 'tablet-T-settings.json').write_text('{"unexpected":{"namespace":"system","value":"0"}}')
            with patch.object(setup, 'run') as command:
                with self.assertRaises(ValueError):
                    setup.restore_tablet('adb', '192.168.10.3:5555', 'T', evidence)
                command.assert_not_called()

    def test_restore_deletes_previously_absent_setting(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            (evidence / 'tablet-T-settings.json').write_text(
                json.dumps(self.backup(user_rotation='null')))
            with patch.object(setup, 'run', return_value=(0, '', '')) as command:
                archive = setup.restore_tablet('adb', '192.168.10.3:5555', 'T', evidence)
                self.assertEqual(['adb', '-s', '192.168.10.3:5555', 'shell', 'settings',
                                  'delete', 'system', 'user_rotation'], command.call_args.args[0])
            self.assertFalse((evidence / 'tablet-T-settings.json').exists())
            self.assertTrue(archive.exists())
            self.assertEqual(evidence.resolve(), archive.parent)

    def test_portal_requires_registered_fresh_matching_heartbeat(self):
        quest = {'deviceId': 'device-a', 'host': '192.168.10.31'}
        portal = {'portalSessionId': 'session-a'}
        device = {'deviceId': 'device-a', 'localIp': '192.168.10.31',
                  'alive': True, 'ageSec': 1,
                  'status': {'visitorPortal': {'portalSessionId': 'session-a'}}}
        self.assertTrue(setup.validate_portal(quest, portal, {'devices': [device]})[0])
        broken = [
            ({**quest, 'deviceId': ''}, portal, {'devices': [device]}),
            (quest, portal, {'devices': [{**device, 'deviceId': 'other'}]}),
            (quest, portal, {'devices': [{**device, 'localIp': '192.168.10.99'}]}),
            (quest, portal, {'devices': [{**device, 'alive': False}]}),
            (quest, portal, {'devices': [{**device, 'ageSec': 6}]}),
            (quest, {'portalSessionId': 'other'}, {'devices': [device]}),
            (quest, portal, {'devices': [{**device, 'status': {}}]}),
        ]
        for args in broken:
            with self.subTest(args=args):
                self.assertFalse(setup.validate_portal(*args)[0])

    def test_wait_portal_rechecks_desk_heartbeat_before_success(self):
        quest = {'deviceId': 'device-a', 'host': '192.168.10.31'}
        portal = {'portalSessionId': 'session-a'}
        good = {'devices': [{'deviceId': 'device-a', 'localIp': '192.168.10.31',
                             'alive': True, 'ageSec': 1,
                             'status': {'visitorPortal': {'portalSessionId': 'session-a'}}}]}
        stale = {'devices': [{**good['devices'][0], 'ageSec': 9}]}
        with patch.object(setup, 'get_status', side_effect=[portal, stale, portal, good]) as get, \
                patch.object(setup.time, 'monotonic', side_effect=[0, 0, 0]), \
                patch.object(setup.time, 'sleep'):
            self.assertEqual(portal, setup.wait_portal('http://quest/', quest))
        self.assertEqual(setup.DESK_DEVICES, get.call_args_list[-1].args[0])

    def test_restore_failure_keeps_original_backup(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            source = evidence / 'tablet-T-settings.json'
            source.write_text(json.dumps(self.backup(screen_off_timeout='1000')))
            with patch.object(setup, 'run', side_effect=[(0, '', ''), (1, '', 'failed')]):
                with self.assertRaises(RuntimeError):
                    setup.restore_tablet('adb', 'wifi:5555', 'T', evidence)
            self.assertTrue(source.exists())
            self.assertEqual([], list(evidence.glob('*.restored-*.json')))

    def test_restore_command_does_not_start_desk(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fleet = root / 'fleet.json'
            fleet.write_text(json.dumps({
                'quests': [{'id': 'alpha', 'serial': 'Q', 'deviceId': 'D'}],
                'tablets': [{'questId': 'alpha', 'serial': 'T'}],
            }), encoding='utf-8')
            (root / 'tablet-T-settings.json').write_text(
                json.dumps(self.backup()), encoding='utf-8')
            devices = [{'physicalSerial': 'T', 'serial': '192.168.10.3:5555',
                        'state': 'device'}]
            args = argparse.Namespace(evidence=str(root), quests='all', restore_tablet='T')
            with patch.object(setup, 'FLEET', fleet), \
                    patch.object(setup, 'resolve_adb', return_value='adb'), \
                    patch.object(setup, 'list_android_devices', return_value=devices), \
                    patch.object(setup, 'run', return_value=(0, '', '')), \
                    patch.object(onsite, 'cmd_serve') as serve:
                self.assertEqual(0, setup.cmd_setup(args))
            serve.assert_not_called()


if __name__ == '__main__':
    unittest.main()
