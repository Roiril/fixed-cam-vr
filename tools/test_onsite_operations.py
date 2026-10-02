"""当日の点検が未確認の機器を正常と表示しないことを確かめる。"""

import importlib.util
import copy
import json
import pathlib
import tempfile
import unittest
import argparse
import contextlib
import io
from unittest import mock


SCRIPT = pathlib.Path(__file__).with_name("onsite.py")
SPEC = importlib.util.spec_from_file_location("onsite", SCRIPT)
onsite = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(onsite)


class OnsiteOperationsTests(unittest.TestCase):
    def setUp(self):
        self.fleet = {
            "schema": 1,
            "show": "mawarimi",
            "cameras": [
                {"id": cid, "host": f"192.168.10.{ip}", "port": 8080,
                 "uuid": cid.lower()} for cid, ip in (("A", 21), ("B", 22), ("C", 23))
            ],
            "quests": [
                {"id": "alpha", "label": "Quest α", "host": "192.168.10.31"},
                {"id": "beta", "label": "Quest β", "host": "192.168.10.32"},
            ],
        }

    def test_missing_required_cameras_are_ng(self):
        rows = onsite.Rows()
        show = {"cameras": [{"id": "A", "host": "192.168.10.21", "port": 8080}]}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "probe_camera", return_value={
                    "id": "A", "host": "192.168.10.21", "port": 8080,
                    "info": {"cameraId": "A", "uuid": "a", "show": "mawarimi",
                             "appVersion": "0.15.0", "lensFovDeg": 104.3,
                             "tiltState": "ok"},
                    "stream": {"ok": True, "frames": 2, "firstSeq": 1, "lastSeq": 2},
                    "health": {}, "adb": True,
                }), mock.patch.object(onsite, "get_json", return_value={}):
            onsite.check_cameras(rows, show)
        self.assertEqual({r["label"] for r in rows.items if r["state"] == "ng"},
                         {"カメラB 登録", "カメラC 登録"})

    def test_wrong_uuid_does_not_report_camera_ok(self):
        rows = onsite.Rows()
        show = {"cameras": [dict(c) for c in self.fleet["cameras"]]}

        def probe(cam):
            return {"id": cam["id"], "host": cam["host"], "port": 8080,
                    "info": {"cameraId": cam["id"], "uuid": "wrong", "show": "mawarimi"},
                    "stream": {"ok": True, "frames": 2}, "health": {}, "adb": True}

        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "probe_camera", side_effect=probe), \
                mock.patch.object(onsite, "get_json", return_value={}):
            onsite.check_cameras(rows, show)
        self.assertEqual(rows.counts()["ok"], 0)
        self.assertEqual(rows.counts()["ng"], 3)

    def test_empty_or_wrong_fixed_host_is_ng_before_probe(self):
        rows = onsite.Rows()
        show = {"cameras": [
            {"id": "A", "host": ""},
            {"id": "B", "host": "192.168.10.99", "port": 8080},
            {"id": "C", "host": "192.168.10.23", "port": 8080},
        ]}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "probe_camera", return_value={
                    "id": "C", "host": "192.168.10.23", "port": 8080,
                    "info": None, "health": None, "adb": False,
                }) as probe, mock.patch.object(onsite, "get_json", return_value={}):
            onsite.check_cameras(rows, show)
        self.assertEqual(probe.call_count, 1)
        self.assertEqual({r["label"] for r in rows.items if r["state"] == "ng"},
                         {"カメラA 登録", "カメラB 登録", "カメラC"})

    def test_stalled_stream_is_ng_even_when_http_returns_bytes(self):
        rows = onsite.Rows()
        show = {"cameras": [dict(c) for c in self.fleet["cameras"]]}

        def probe(cam):
            return {"id": cam["id"], "host": cam["host"], "port": 8080,
                    "info": {"cameraId": cam["id"], "uuid": cam["uuid"],
                             "show": "mawarimi", "appVersion": "0.15.0",
                             "lensFovDeg": 104.3, "tiltState": "ok"},
                    "stream": {"ok": False, "bytes": 65536, "frames": 1},
                    "health": {"fps": 0, "clientCount": 0}, "adb": True}

        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "probe_camera", side_effect=probe), \
                mock.patch.object(onsite, "get_json", return_value={}):
            onsite.check_cameras(rows, show)
        self.assertFalse(any(r["state"] == "ok" and r["label"] in
                             ("カメラA", "カメラB", "カメラC") for r in rows.items))
        self.assertEqual(rows.counts()["ng"], 3)

    def test_probe_camera_uses_frame_progress_probe(self):
        import operations

        cam = self.fleet["cameras"][0]
        stream = {"ok": False, "bytes": 65536, "frames": 1}
        with mock.patch.object(onsite, "get_json", side_effect=[
                {"cameraId": "A", "uuid": "a"}, {"fps": 0, "clientCount": 0}]), \
                mock.patch.object(operations, "probe_stream", return_value=stream) as probe, \
                mock.patch.object(onsite.socket, "socket"):
            result = onsite.probe_camera(cam)
        probe.assert_called_once_with("192.168.10.21", 8080)
        self.assertEqual(result["stream"], stream)

    def test_stale_beta_is_ng_when_alpha_is_fresh(self):
        rows = onsite.Rows()
        devices = {"devices": [
            {"deviceId": "alpha-id", "localIp": "192.168.10.31", "ageSec": 1,
             "status": {"sndMissing": 0, "ctrlLConnected": True,
                        "ctrlRConnected": True}},
            {"deviceId": "beta-id", "localIp": "192.168.10.32", "ageSec": 30,
             "status": {"sndMissing": 0, "ctrlLConnected": True,
                        "ctrlRConnected": True}},
        ]}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "get_json", return_value=devices), \
                mock.patch.object(onsite.time, "sleep"):
            onsite.sample_heartbeat(rows, 0.01, 2)
        self.assertTrue(any(r["label"] == "Quest β" and r["state"] == "ng"
                            for r in rows.items))
        self.assertFalse(any(r["label"] == "Quest β" and r["state"] == "ok"
                             for r in rows.items))

    def test_unknown_sound_measurement_is_not_ok(self):
        rows = onsite.Rows()
        devices = {"devices": [
            {"deviceId": q["id"], "localIp": q["host"], "ageSec": 1,
             "status": {"sndMissing": -1, "ctrlLConnected": True,
                        "ctrlRConnected": True, "eyeJackListed": -1,
                        "eyeJackReady": -1}}
            for q in self.fleet["quests"]
        ]}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "get_json", return_value=devices), \
                mock.patch.object(onsite.time, "sleep"):
            onsite.sample_heartbeat(rows, 0.01, 2)
        self.assertEqual({r["state"] for r in rows.items if r["label"].endswith("音")},
                         {"skip"})

    def test_unused_eye_jack_does_not_leave_unconfirmed_quest_row(self):
        rows = onsite.Rows()
        devices = {"devices": [
            {"deviceId": q["id"], "localIp": q["host"], "ageSec": 1,
             "status": {"sndMissing": 0, "ctrlLConnected": True,
                        "ctrlRConnected": True}}
            for q in self.fleet["quests"]
        ]}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "get_json", return_value=devices), \
                mock.patch.object(onsite.time, "sleep"):
            onsite.sample_heartbeat(rows, 0.01, 2, eye_jack_required=False)
        self.assertFalse(any("目の写真" in r["label"] for r in rows.items))

    def test_skip_is_visible_in_summary(self):
        rows = onsite.Rows()
        rows.add("点検", "skip", "詳細", "未確認")
        self.assertIn("skip 1", rows.render())
        self.assertNotIn("開場してよい", rows.render())

    def test_unused_material_does_not_leave_required_warning(self):
        rows = onsite.Rows()
        show = {"timeline": {"segments": [{"takes": [{"steps": [
            {"cueId": "ordinary", "eyeJack": False}
        ]}]}]}}
        with mock.patch.object(onsite, "get_json", return_value={"items": []}) as get:
            onsite.check_show_material(rows, show, {})
        self.assertEqual(get.call_count, 1)
        self.assertEqual([(r["label"], r["state"], r["required"]) for r in rows.items],
                         [("目の写真", "skip", False)])
        self.assertNotIn("未確認または注意", rows.render())

    def test_only_referenced_pov_takes_are_required(self):
        rows = onsite.Rows()
        show = {"timeline": {"segments": [{"takes": [{"steps": [
            {"cueId": "pov_0", "eyeJack": False},
            {"cueId": "pov_1", "eyeJack": False},
        ]}]}]}, "cues": [
            {"id": "pov_0", "sourceUrl": "/recordings/pov_0.mp4"},
            {"id": "pov_1", "sourceUrl": "/recordings/missing.mp4"},
        ]}
        with tempfile.TemporaryDirectory() as folder:
            recordings = pathlib.Path(folder, "recordings")
            recordings.mkdir()
            (recordings / "pov_0.mp4").write_bytes(b"video")
            with mock.patch.object(onsite, "COMPOSITOR", folder), \
                    mock.patch.object(onsite, "get_json", return_value={"items": []}) as get:
                onsite.check_show_material(rows, show, {})
        self.assertEqual(get.call_count, 1)
        self.assertTrue(any(r["label"] == "人形視点" and r["state"] == "ng" and
                            "pov_1" in r["detail"] for r in rows.items))

    def test_current_pov_files_are_present_without_manifest(self):
        rows = onsite.Rows()
        with mock.patch.object(onsite, "get_json", return_value={"items": []}) as get:
            onsite.check_show_material(rows, onsite.load_show(), {})
        self.assertEqual(get.call_count, 1)
        self.assertTrue(any(r["label"] == "人形視点" and r["state"] == "ok"
                            for r in rows.items))

    def test_external_pov_url_is_unverified_and_traversal_is_ng(self):
        show = {"timeline": {"segments": [{"takes": [{"steps": [
            {"cueId": "pov_0"}, {"cueId": "pov_1"}
        ]}]}]}, "cues": [
            {"id": "pov_0", "sourceUrl": "https://example.invalid/video.mp4"},
            {"id": "pov_1", "sourceUrl": "/recordings/%2e%2e/private.mp4"},
        ]}
        rows = onsite.Rows()
        with mock.patch.object(onsite, "get_json", return_value={"items": []}):
            onsite.check_show_material(rows, show, {})
        self.assertTrue(any(r["label"] == "人形視点" and r["state"] == "ng"
                            for r in rows.items))
        self.assertTrue(any(r["label"] == "人形視点の外部素材" and r["state"] == "skip"
                            for r in rows.items))

    def test_server_normalization_revision_is_not_a_setting_error(self):
        import operations

        disk = {"rev": 1267, "cameras": [{"id": cid} for cid in "ABC"],
                "control": {}, "timeline": {"rev": 46}}
        live = copy.deepcopy(disk)
        operations.normalize_show(live)
        live["rev"] = 1268
        rows = onsite.Rows()
        with mock.patch.object(onsite, "listening_pids", return_value=["12"]), \
                mock.patch.object(onsite, "get_json", return_value=live), \
                mock.patch.object(onsite, "run", return_value=(0, "✓ 同じ", "")), \
                mock.patch.object(onsite, "local_ipv4", return_value=[onsite.DESK_IP]), \
                mock.patch.object(onsite, "baked_content_matches_show", return_value=True):
            onsite.check_desk(rows, disk)
        self.assertTrue(any(r["label"] == "卓の設定" and r["state"] == "ok"
                            for r in rows.items))

    def test_real_setting_difference_is_ng(self):
        import operations

        disk = {"rev": 1267, "cameras": [{"id": cid} for cid in "ABC"],
                "control": {}, "timeline": {"rev": 46}}
        live = copy.deepcopy(disk)
        operations.normalize_show(live)
        live["rev"] = 1268
        live["timeline"]["rev"] = 47
        rows = onsite.Rows()
        with mock.patch.object(onsite, "listening_pids", return_value=["12"]), \
                mock.patch.object(onsite, "get_json", return_value=live), \
                mock.patch.object(onsite, "run", return_value=(0, "✓ 同じ", "")), \
                mock.patch.object(onsite, "local_ipv4", return_value=[onsite.DESK_IP]), \
                mock.patch.object(onsite, "baked_content_matches_show", return_value=True):
            onsite.check_desk(rows, disk)
        self.assertTrue(any(r["label"] == "卓の設定" and r["state"] == "ng"
                            for r in rows.items))

    def test_baked_content_comparison_remaps_assets_but_detects_changes(self):
        show = {"rev": 1267, "timeline": {"rev": 46}, "cues": [
            {"id": "pov_0", "sourceUrl": "/recordings/a.mp4"}]}
        baked = {"rev": 1263, "timeline": {"rev": 46}, "cues": [
            {"id": "pov_0", "sourceUrl": "sa://assets/a.mp4"}],
            "assetMap": [{"from": "/recordings/a.mp4", "to": "sa://assets/a.mp4"}]}
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder, "show.json")
            path.write_text(json.dumps(baked), encoding="utf-8")
            with mock.patch.object(onsite, "BAKED_SHOW_JSON", str(path)):
                self.assertTrue(onsite.baked_content_matches_show(show))
                baked["timeline"]["rev"] = 47
                path.write_text(json.dumps(baked), encoding="utf-8")
                self.assertFalse(onsite.baked_content_matches_show(show))

    def test_external_connection_is_optional(self):
        import operations

        disk = {"rev": 1267, "cameras": [{"id": cid} for cid in "ABC"],
                "control": {}, "timeline": {"rev": 46}}
        live = copy.deepcopy(disk)
        operations.normalize_show(live)
        rows = onsite.Rows()
        with mock.patch.object(onsite, "listening_pids", return_value=["12"]), \
                mock.patch.object(onsite, "get_json", return_value=live), \
                mock.patch.object(onsite, "run", side_effect=[
                    (0, "✓ 同じ", ""), (0, "", ""), (1, "", "")]), \
                mock.patch.object(onsite, "local_ipv4", return_value=[onsite.DESK_IP]), \
                mock.patch.object(onsite, "baked_content_matches_show", return_value=True):
            onsite.check_desk(rows, disk)
        self.assertTrue(any(r["label"] == "インターネット接続" and
                            r["state"] == "warn" and not r["required"] for r in rows.items))

    def test_baked_content_mismatch_is_required_failure(self):
        import operations

        disk = {"rev": 1267, "cameras": [{"id": cid} for cid in "ABC"],
                "control": {}, "timeline": {"rev": 46}}
        live = copy.deepcopy(disk)
        operations.normalize_show(live)
        line = "✗ 焼き込みが卓と違う contentId"
        for code, state in ((0, "ok"), (4, "ng")):
            rows = onsite.Rows()
            with mock.patch.object(onsite, "listening_pids", return_value=["12"]), \
                    mock.patch.object(onsite, "get_json", return_value=live), \
                    mock.patch.object(onsite, "run", return_value=(code, line, "")), \
                    mock.patch.object(onsite, "local_ipv4", return_value=[onsite.DESK_IP]):
                onsite.check_desk(rows, disk)
            baked_row = next(r for r in rows.items if r["label"] == "ビルド用の設定")
            self.assertEqual(baked_row["state"], state)
            self.assertTrue(baked_row["required"])

    def test_ready_requires_no_required_skip(self):
        rows = onsite.Rows()
        rows.add("点検", "warn", "任意", "注意", required=False)
        self.assertTrue(rows.ready)
        rows.add("点検", "skip", "必須", "未確認")
        self.assertFalse(rows.ready)
        self.assertEqual(rows.required_unconfirmed, 1)

    def test_selected_quest_does_not_expect_the_other(self):
        selected = onsite.selected_quests(self.fleet, "alpha")
        self.assertEqual([q["id"] for q in selected], ["alpha"])

    def test_heartbeat_content_mismatch_is_ng(self):
        rows = onsite.Rows()
        devices = {"devices": [{
            "deviceId": "alpha-id", "localIp": "192.168.10.31", "ageSec": 1,
            "status": {"contentId": "old", "buildGuid": "old", "sndMissing": 0,
                       "ctrlLConnected": True, "ctrlRConnected": True},
        }]}
        manifest = {"contentId": "new", "buildGuid": "abcdef"}
        with mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "get_json", return_value=devices), \
                mock.patch.object(onsite, "load_apk_content", return_value=manifest), \
                mock.patch.object(onsite.time, "sleep"):
            onsite.sample_heartbeat(rows, 0.01, 1, False, "alpha")
        self.assertTrue(any(r["label"] == "Quest α の導入済み内容" and r["state"] == "ng"
                            for r in rows.items))

    def test_json_check_writes_one_json_document_and_fails_required_skip(self):
        def desk(rows, show):
            rows.add("卓", "skip", "証明", "未確認")
            return None

        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(onsite, "LOG_DIR", folder), \
                mock.patch.object(onsite, "load_show", return_value={}), \
                mock.patch.object(onsite, "check_desk", side_effect=desk), \
                mock.patch.object(onsite, "check_cameras"), \
                mock.patch.object(onsite, "quest_rows", return_value=[]), \
                mock.patch.object(onsite, "check_adb_android_networks"), \
                mock.patch.object(onsite, "sample_heartbeat"), \
                mock.patch.object(onsite, "check_tablets"):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                rc = onsite.cmd_check(argparse.Namespace(
                    deep=False, json=True, quests="alpha"))
        payload = json.loads(output.getvalue())
        self.assertEqual(rc, 1)
        self.assertFalse(payload["ready"])
        self.assertEqual(payload["requiredUnconfirmed"], 1)

    def test_wifi_guard_does_not_change_unknown_android(self):
        unknown = {"serial": "USB-X", "physicalSerial": "X", "state": "device",
                   "model": "Tablet", "isQuest": False, "usb": True}
        with mock.patch.object(onsite, "connected_android_devices",
                               return_value=([unknown], "")), \
                mock.patch.object(onsite, "load_fleet", return_value=self.fleet), \
                mock.patch.object(onsite, "_authorized_android",
                                  return_value=(False, "固定登録に一致しない Android")), \
                mock.patch.object(onsite, "wifi_guard") as guard:
            rc = onsite.cmd_wifi_guard(argparse.Namespace())
        self.assertEqual(rc, 1)
        guard.assert_not_called()


if __name__ == "__main__":
    unittest.main()
