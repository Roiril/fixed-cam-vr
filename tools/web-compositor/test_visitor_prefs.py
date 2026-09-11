"""タブレットの枠（visitor_prefs.py）の判断を stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools/web-compositor -p "test_*.py"

なぜテストするか: 枠は「体験者が始めた瞬間に既定へ戻す」で次の人への持ち越しを断つ。
ここが 1 つ崩れると、前の人が選んだ English・軽減モードが次の人の体験に黙って乗る
（音は録画に映らず、言語は画から読めない ＝ 現場で気づけない）。
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import visitor_prefs as vp  # noqa: E402


class DefaultsTest(unittest.TestCase):
    def test_default_control_fields_have_both_roles(self):
        f = vp.default_control_fields()
        self.assertEqual(set(f['visitorDevices']), {'alpha', 'beta'})
        self.assertEqual(f['visitor']['beta'], {'lang': 'ja', 'relief': False, 'epoch': 0})

    def test_view_fills_missing_keys_without_touching_ctrl(self):
        ctrl = {'runEpoch': 3}
        devs, slots = vp.view(ctrl)
        self.assertEqual(devs, {'alpha': '', 'beta': ''})
        self.assertEqual(slots['alpha']['epoch'], 0)
        self.assertEqual(ctrl, {'runEpoch': 3})  # 読むだけ

    def test_ensure_fills_in_place(self):
        ctrl = {'visitor': {'alpha': {'lang': 'en'}}}
        vp.ensure(ctrl)
        self.assertEqual(ctrl['visitor']['alpha'], {'lang': 'en', 'relief': False, 'epoch': 0})
        self.assertIn('beta', ctrl['visitor'])
        self.assertEqual(ctrl['visitorDevices'], {'alpha': '', 'beta': ''})


class SetVisitorTest(unittest.TestCase):
    def test_bumps_epoch_each_time(self):
        ctrl = {}
        self.assertEqual(vp.set_visitor(ctrl, 'alpha', 'en', True), 1)
        self.assertEqual(vp.set_visitor(ctrl, 'alpha', 'fr', False), 2)
        self.assertEqual(ctrl['visitor']['alpha'], {'lang': 'fr', 'relief': False, 'epoch': 2})
        self.assertEqual(ctrl['visitor']['beta']['epoch'], 0)  # 別の役は動かない

    def test_rejects_unknown_role_and_lang(self):
        with self.assertRaises(ValueError):
            vp.set_visitor({}, 'gamma', 'ja', False)
        with self.assertRaises(ValueError):
            vp.set_visitor({}, 'alpha', 'de', False)

    def test_lang_is_case_insensitive_and_defaults_to_ja(self):
        ctrl = {}
        vp.set_visitor(ctrl, 'beta', 'EN', 'yes')
        self.assertEqual(ctrl['visitor']['beta']['lang'], 'en')
        self.assertTrue(ctrl['visitor']['beta']['relief'])
        vp.set_visitor(ctrl, 'beta', None, 0)
        self.assertEqual(ctrl['visitor']['beta']['lang'], 'ja')


class BindDeviceTest(unittest.TestCase):
    def test_one_device_holds_one_role(self):
        ctrl = {}
        vp.bind_device(ctrl, 'alpha', 'DEV1')
        vp.bind_device(ctrl, 'beta', 'dev1')   # 同じ端末を β へ ＝ α からは外れる
        self.assertEqual(ctrl['visitorDevices'], {'alpha': '', 'beta': 'dev1'})
        self.assertEqual(vp.role_for_device(ctrl, 'DEV1'), 'beta')

    def test_empty_unbinds(self):
        ctrl = {}
        vp.bind_device(ctrl, 'alpha', 'x')
        vp.bind_device(ctrl, 'alpha', '')
        self.assertEqual(vp.role_for_device(ctrl, 'x'), '')


class ConsumeTest(unittest.TestCase):
    def setUp(self):
        self.ctrl = {}
        vp.bind_device(self.ctrl, 'alpha', 'A1')
        vp.set_visitor(self.ctrl, 'alpha', 'en', True)   # epoch 1

    def test_resets_when_epochs_match(self):
        self.assertTrue(vp.needs_consume(self.ctrl, 'A1', 1))
        self.assertTrue(vp.consume(self.ctrl, 'A1', 1))
        self.assertEqual(self.ctrl['visitor']['alpha'], {'lang': 'ja', 'relief': False, 'epoch': 2})

    def test_stale_consumed_epoch_does_nothing(self):
        # 体験者が始めた後にタブレットで次の人が選び直した（epoch 2）→ 古い消費 1 では戻さない
        vp.set_visitor(self.ctrl, 'alpha', 'fr', False)
        self.assertFalse(vp.needs_consume(self.ctrl, 'A1', 1))
        self.assertFalse(vp.consume(self.ctrl, 'A1', 1))
        self.assertEqual(self.ctrl['visitor']['alpha']['lang'], 'fr')

    def test_default_slot_is_not_reset_again(self):
        # 既定の枠に対して同じ世代が返ってきても、世代を無駄に進めない（long-poll を起こさない）
        vp.consume(self.ctrl, 'A1', 1)                  # → epoch 2・既定
        self.assertFalse(vp.needs_consume(self.ctrl, 'A1', 2))

    def test_unbound_device_never_consumes(self):
        self.assertFalse(vp.needs_consume(self.ctrl, 'B9', 1))

    def test_zero_epoch_never_consumes(self):
        # 再起動直後の Quest は 0 を返す（何も始めていない）
        self.assertFalse(vp.needs_consume(self.ctrl, 'A1', 0))
        self.assertFalse(vp.needs_consume(self.ctrl, 'A1', None))


class DeviceRowsTest(unittest.TestCase):
    def test_rows_and_roles(self):
        ctrl = {}
        vp.bind_device(ctrl, 'beta', 'B222222')
        vp.set_visitor(ctrl, 'beta', 'fr', True)
        devices = {
            'A111111': {'at': 100.0, 'deviceModel': 'Quest 3', 'phase': 'RUN', 'lang': 'ja'},
            'B222222': {'at': 90.0, 'deviceModel': 'Quest 3', 'phase': 'INTRO', 'lang': 'fr',
                        'relief': True, 'visitorRole': 'beta', 'visitorPendingEpoch': 1,
                        'visitorAppliedEpoch': 1},
        }
        out = vp.device_rows(devices, ctrl, now=102.0)
        by = {r['deviceId']: r for r in out['devices']}
        self.assertEqual(out['devices'][0]['deviceId'], 'B222222')  # 役が付いた機が先
        self.assertEqual(by['B222222']['role'], 'beta')
        self.assertTrue(by['A111111']['alive'])       # 2 秒前
        self.assertFalse(by['B222222']['alive'])      # 12 秒前
        self.assertEqual(by['B222222']['shortId'], 'B22222')
        self.assertEqual(out['roles']['beta']['deviceId'], 'B222222')
        self.assertEqual(out['roles']['beta']['slot'], {'lang': 'fr', 'relief': True, 'epoch': 1})
        self.assertEqual(out['roles']['alpha']['deviceId'], '')


if __name__ == '__main__':
    unittest.main()
