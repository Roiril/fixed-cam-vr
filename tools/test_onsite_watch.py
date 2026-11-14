"""会期中の監視が、正常なカメラを体験の最中に起こし直さないことを確かめる。

- `operations.probe_stream` は 3 秒の窓で判定する（フレームの間が 0.6 秒を超えても止まったと言わない）
- `onsite.py watch` は 2 巡回続けて止まって見えたときだけ起こし直す
- watch と卓の `/ops/restart` は同じ記録を見て、同じ端末を 90 秒以内に 2 回起こさない

実機・adb は叩かない。映像は test の中に立てた偽の MJPEG サーバで出す。
"""

import argparse
import contextlib
import importlib.util
import io
import json
import os
import pathlib
import socket
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS / "web-compositor"))
import operations  # noqa: E402

SPEC = importlib.util.spec_from_file_location("onsite", TOOLS / "onsite.py")
onsite = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(onsite)

JPEG = b"\xff\xd8" + b"x" * 64 + b"\xff\xd9"


class FakeMjpeg:
    """配信アプリの /video を真似る。`interval` 秒おきに連番つきの JPEG を `frames` 枚送る。

    `chunked=True` は配信アプリ（NanoHTTPD の chunked 応答）と同じ形で送る。
    送り終えたら接続を開いたまま黙る（止まったカメラの形）。
    """

    def __init__(self, interval=0.0, frames=0, chunked=False,
                 content_type="multipart/x-mixed-replace; boundary=frame"):
        self.interval, self.frames, self.chunked = interval, frames, chunked
        self.content_type = content_type
        self.stop = threading.Event()
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(4)
        self.listener.settimeout(0.1)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self._serve, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_args):
        self.stop.set()
        self.thread.join(timeout=5)
        self.listener.close()

    def _serve(self):
        while not self.stop.is_set():
            try:
                conn, _ = self.listener.accept()
            except (socket.timeout, OSError):
                continue
            threading.Thread(target=self._handle, args=(conn,), daemon=True).start()

    def _send(self, conn, body):
        if self.chunked:
            body = f"{len(body):x}\r\n".encode() + body + b"\r\n"
        conn.sendall(body)

    def _handle(self, conn):
        with conn:
            conn.settimeout(2)
            request = b""
            try:
                while b"\r\n\r\n" not in request:
                    piece = conn.recv(4096)
                    if not piece:
                        return
                    request += piece
                head = ("HTTP/1.1 200 OK\r\nContent-Type: " + self.content_type + "\r\n"
                        + ("Transfer-Encoding: chunked\r\n" if self.chunked else "")
                        + "\r\n")
                conn.sendall(head.encode())
                for seq in range(1, self.frames + 1):
                    if self.stop.wait(self.interval):
                        return
                    part = (b"--frame\r\nContent-Type: image/jpeg\r\n"
                            + f"Content-Length: {len(JPEG)}\r\nX-Frame-Seq: {seq}\r\n\r\n".encode()
                            + JPEG + b"\r\n")
                    self._send(conn, part)
                self.stop.wait(6)
            except OSError:
                return


class ProbeStreamTests(unittest.TestCase):
    def test_frames_every_0_8_sec_are_alive(self):
        # 体験中の正常なカメラでも、Wi-Fi の揺れでフレームの間が 0.6 秒を超えることはある。
        for chunked in (False, True):
            with self.subTest(chunked=chunked), FakeMjpeg(interval=0.8, frames=3,
                                                          chunked=chunked) as server:
                result = operations.probe_stream("127.0.0.1", server.port)
                self.assertTrue(result["ok"], result)
                self.assertEqual((result["firstSeq"], result["lastSeq"]), (1, 2))
                self.assertGreaterEqual(result["frames"], 2)
                self.assertLess(result["elapsedSec"], 3.0)

    def test_silent_stream_is_not_ok_after_about_3_sec(self):
        with FakeMjpeg(frames=0) as server:
            started = time.monotonic()
            result = operations.probe_stream("127.0.0.1", server.port)
            spent = time.monotonic() - started
        self.assertFalse(result["ok"])
        self.assertEqual(result["frames"], 0)
        self.assertGreaterEqual(spent, 2.9)
        self.assertLess(spent, 3.4)

    def test_single_frame_then_silence_is_not_ok(self):
        with FakeMjpeg(interval=0.1, frames=1) as server:
            result = operations.probe_stream("127.0.0.1", server.port)
        self.assertFalse(result["ok"])
        self.assertEqual(result["frames"], 1)
        self.assertEqual((result["firstSeq"], result["lastSeq"]), (1, 1))
        self.assertLess(result["elapsedSec"], 3.4)

    def test_missing_port_gives_up_within_1_sec(self):
        holder = socket.socket()
        holder.bind(("127.0.0.1", 0))
        port = holder.getsockname()[1]
        holder.close()
        started = time.monotonic()
        result = operations.probe_stream("127.0.0.1", port)
        self.assertFalse(result["ok"])
        self.assertLess(time.monotonic() - started, 1.0)

    def test_non_multipart_is_not_ok(self):
        with FakeMjpeg(interval=0.05, frames=3, content_type="text/html") as server:
            result = operations.probe_stream("127.0.0.1", server.port)
        self.assertFalse(result["ok"])
        self.assertEqual(result["frames"], 0)


class RestartLedgerTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.path = os.path.join(folder.name, "onsite", "restart-ledger.json")
        patcher = mock.patch.object(operations, "RESTART_LEDGER", self.path)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_recent_restart_blocks_for_90_sec(self):
        now = 1_800_000_000.0
        self.assertFalse(operations.recently_restarted("192.168.10.21", now=now))
        self.assertTrue(operations.record_restart("192.168.10.21", "watch", now=now))
        self.assertTrue(operations.recently_restarted("192.168.10.21", now=now + 1))
        self.assertTrue(operations.recently_restarted("192.168.10.21", now=now + 89.9))
        self.assertFalse(operations.recently_restarted("192.168.10.21", now=now + 90.1))
        self.assertFalse(operations.recently_restarted("192.168.10.22", now=now + 1))
        with open(self.path, encoding="utf-8") as source:
            saved = json.load(source)
        self.assertEqual(saved["hosts"]["192.168.10.21"]["by"], "watch")
        self.assertFalse([name for name in os.listdir(os.path.dirname(self.path))
                          if name.endswith(".tmp")])

    def test_claim_restart_allows_once_per_window(self):
        now = 1_800_000_000.0
        self.assertTrue(operations.claim_restart("192.168.10.21", "watch", now=now))
        self.assertFalse(operations.claim_restart("192.168.10.21", "desk", now=now + 5))
        self.assertTrue(operations.claim_restart("192.168.10.21", "desk", now=now + 91))

    def test_broken_or_missing_ledger_reads_as_no_record(self):
        self.assertIsNone(operations.last_restart_at("192.168.10.21"))
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        for broken in ("{not json", "[]", '{"hosts": []}',
                       '{"hosts": {"192.168.10.21": {"at": "soon"}}}', ""):
            with self.subTest(broken=broken):
                with open(self.path, "w", encoding="utf-8") as sink:
                    sink.write(broken)
                self.assertIsNone(operations.last_restart_at("192.168.10.21"))
                self.assertFalse(operations.recently_restarted("192.168.10.21"))
        self.assertTrue(operations.record_restart("192.168.10.21", "desk", now=10.0))
        self.assertEqual(operations.last_restart_at("192.168.10.21"), 10.0)

    def test_desk_restart_refuses_after_watch_restart(self):
        fixed = operations.FLEET["cameras"][0]
        operations.record_restart(fixed["host"], "watch")
        calls = []
        with mock.patch.dict(operations._last_restart, {}, clear=True):
            code, result = operations.restart(fixed["id"], {}, [], 1,
                                              runner=lambda *a, **k: calls.append(a))
        self.assertEqual(code, 409)
        self.assertFalse(result["ok"])
        self.assertIn("90 秒", result["message"])
        self.assertEqual(calls, [])


class WatchTests(unittest.TestCase):
    HOST = "192.168.10.21"

    def run_watch(self, alive_by_round, ledger_path=None):
        """`alive_by_round[i]` を i 巡回目のカメラ A の状態として watch を回す。"""
        rounds = {"n": 0}

        def probe(_host, _port):
            alive = alive_by_round[rounds["n"]]
            return ({"ok": True, "frames": 2, "firstSeq": 1, "lastSeq": 2,
                     "elapsedSec": 0.1, "bytes": 100} if alive else
                    {"ok": False, "frames": 0, "firstSeq": None, "lastSeq": None,
                     "elapsedSec": 3.0, "bytes": 0})

        def sleep(_sec):
            rounds["n"] += 1
            if rounds["n"] >= len(alive_by_round):
                raise KeyboardInterrupt

        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        ledger = ledger_path or os.path.join(folder.name, "restart-ledger.json")
        show = {"cameras": [{"id": "A", "host": self.HOST, "port": 8080}]}
        fleet = {"quests": []}
        with mock.patch.object(onsite, "load_show", return_value=show), \
                mock.patch.object(onsite, "load_fleet", return_value=fleet), \
                mock.patch.object(onsite, "LOG_DIR", folder.name), \
                mock.patch.object(onsite, "listening_pids", return_value=["1"]), \
                mock.patch.object(onsite, "get_json", return_value={}), \
                mock.patch.object(onsite, "wake_and_restart", return_value="起こし直しました") as wake, \
                mock.patch.object(operations, "probe_stream", side_effect=probe), \
                mock.patch.object(operations, "RESTART_LEDGER", ledger), \
                mock.patch.object(onsite.time, "sleep", side_effect=sleep), \
                contextlib.redirect_stdout(io.StringIO()):
            onsite.cmd_watch(argparse.Namespace(sec=20, no_fix=False, quests="all"))
        return wake, ledger

    def test_single_miss_does_not_restart(self):
        wake, _ = self.run_watch([True, False, True, True])
        wake.assert_not_called()

    def test_two_consecutive_misses_restart_once(self):
        wake, ledger = self.run_watch([True, False, False, False])
        wake.assert_called_once_with(self.HOST)
        with open(ledger, encoding="utf-8") as source:
            self.assertEqual(json.load(source)["hosts"][self.HOST]["by"], "watch")

    def test_alive_between_misses_resets_the_count(self):
        wake, _ = self.run_watch([False, True, False, True, False])
        wake.assert_not_called()

    def test_watch_waits_when_desk_restarted_recently(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        ledger = os.path.join(folder.name, "restart-ledger.json")
        with mock.patch.object(operations, "RESTART_LEDGER", ledger):
            operations.record_restart(self.HOST, "desk")
        wake, _ = self.run_watch([False, False, False], ledger_path=ledger)
        wake.assert_not_called()

    def test_stall_counter(self):
        stalls = {}
        self.assertEqual(onsite.count_stall(stalls, "A", False), 1)
        self.assertEqual(onsite.count_stall(stalls, "A", False), 2)
        self.assertEqual(onsite.count_stall(stalls, "B", False), 1)
        self.assertEqual(onsite.count_stall(stalls, "A", True), 0)
        self.assertEqual(onsite.count_stall(stalls, "A", False), 1)


if __name__ == "__main__":
    unittest.main()
