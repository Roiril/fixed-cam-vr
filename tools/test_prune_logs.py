"""刈り込みの「残す側」を stdlib unittest で固定する。

実行: py -3.11 -m unittest discover -s tools -p "test_*.py"

なぜテストするか: `prune-logs.py` の削除は **.gitignore 済みのファイルに対する完全削除**で、
戻す手段が無い。境界が 1 日ずれるだけで、並行セッションが今まさに突き合わせている走行が
消える。2026-08-23 は 18 セッションが同じ working copy で動いていて、20:07 の撮影と
20:14 のビルドが同時に走っていた。

だから固定するのは「消える側」ではなく **「残る側」** — 走行の数値証拠・直近 2 日・
`gen-plate`・`sound/ingest`。ここが緑なら、境界を触った変更が安全側に倒れる。
"""
import datetime as dt
import importlib.util
import os
import shutil
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))


def _load_prune_module():
    path = os.path.join(_HERE, 'prune-logs.py')
    spec = importlib.util.spec_from_file_location('prune_logs', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_MOD = _load_prune_module()

TODAY = dt.date(2026, 8, 23)
YESTERDAY = TODAY - dt.timedelta(days=1)
KEEP_FROM = YESTERDAY          # --keep-days 2 のときの境界
MP4_KEEP_FROM = TODAY          # --mp4-keep-days 1 のときの境界


def _stamp(path, day):
    """mtime を指定の日に置く（ファイル名に日付が無いものの判定に効く）。"""
    when = dt.datetime.combine(day, dt.time(12, 0)).timestamp()
    os.utime(path, (when, when))


class PruneScopeTest(unittest.TestCase):

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix='prune-test-')
        self.logs = os.path.join(self.root, 'Logs')
        self.shots = os.path.join(self.root, 'Assets', 'Screenshots')
        self._saved = (_MOD.LOGS, _MOD.SHOTS)
        _MOD.LOGS, _MOD.SHOTS = self.logs, self.shots

    def tearDown(self):
        _MOD.LOGS, _MOD.SHOTS = self._saved
        shutil.rmtree(self.root, ignore_errors=True)

    # --- 足場 ---

    def _write(self, *parts, day=None, body=b'x'):
        path = os.path.join(self.logs, *parts)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'wb') as f:
            f.write(body)
        if day:
            _stamp(path, day)
        return path

    def _shot(self, *parts, day=None, with_meta=True):
        path = os.path.join(self.shots, *parts)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        for p in ([path, path + '.meta'] if with_meta else [path]):
            with open(p, 'wb') as f:
                f.write(b'x')
            if day:
                _stamp(p, day)
        return path

    def _collect(self, include_assets=False):
        plan = _MOD.collect(KEEP_FROM, MP4_KEEP_FROM, include_assets)
        return {path for _size, path, _isdir, _reason in plan}

    # --- 走行の数値証拠は日付に関わらず残る ---

    def test_run_evidence_files_are_never_in_scope(self):
        kept = [
            self._write('capture', '20260801_120000_xp.log'),
            self._write('capture', '20260801_120000_logcat.log'),
            self._write('capture', '20260801_120000_meta.json'),
            self._write('capture', '20260801_120000-report.md'),
        ]
        doomed = self._write('capture', '20260801_120000_raw.mp4')
        plan = self._collect()
        for path in kept:
            self.assertNotIn(path, plan, os.path.basename(path))
        self.assertIn(doomed, plan)

    # --- 境界。ここが 1 日ずれると進行中の作業が消える ---

    def test_capture_mp4_keeps_only_today(self):
        today = self._write('capture', '20260823_120000_raw.mp4')
        yesterday = self._write('capture', '20260822_120000_raw.mp4')
        plan = self._collect()
        self.assertNotIn(today, plan)
        self.assertIn(yesterday, plan, 'mp4 は 1 走行 300-450MB なので今日ぶんだけ残す')

    def test_other_logs_keep_today_and_yesterday(self):
        today = self._write('preview', 'a.mp4', day=TODAY)
        yesterday = self._write('preview', 'b.mp4', day=YESTERDAY)
        older = self._write('preview', 'c.mp4', day=TODAY - dt.timedelta(days=2))
        plan = self._collect()
        self.assertNotIn(today, plan)
        self.assertNotIn(yesterday, plan)
        self.assertIn(older, plan)

    def test_evidence_dirs_are_judged_by_name_not_mtime(self):
        """走行ディレクトリは中を書き換えると mtime が今日になる。名前の日付で判定する。"""
        old = os.path.join(self.logs, 'evidence', '20260801_120000')
        os.makedirs(old)
        open(os.path.join(old, 'sheet.png'), 'wb').close()  # mtime は今日のまま
        fresh = os.path.join(self.logs, 'evidence', '20260823_120000')
        os.makedirs(fresh)
        plan = self._collect()
        self.assertIn(old, plan)
        self.assertNotIn(fresh, plan)

    # --- 丸ごと触らないもの ---

    def test_gen_plate_is_never_in_scope(self):
        """selftest.py が古い走行名を名指しし、yield.py は全走行を母数にする。"""
        old = self._write('gen-plate', 'q_A_20260816', 'out.png', day=dt.date(2026, 8, 16))
        self.assertNotIn(old, self._collect())

    def test_sound_ingest_is_never_in_scope(self):
        """焼き直せない元音源。"""
        old = self._write('sound', 'ingest', 'src_score_LostPlace2.wav',
                          day=dt.date(2026, 8, 1))
        self.assertNotIn(old, self._collect())

    # --- Editor プレビュー ---

    def test_screenshots_need_the_assets_flag(self):
        old = self._shot('cgviz', 'a.png', day=dt.date(2026, 8, 1))
        self.assertNotIn(old, self._collect(include_assets=False))
        self.assertIn(old, self._collect(include_assets=True))

    def test_screenshot_size_counts_the_meta_pair(self):
        """.meta を置き去りにすると Unity が孤児として警告し続ける。"""
        self._shot('cgviz', 'a.png', day=dt.date(2026, 8, 1))
        plan = _MOD.collect(KEEP_FROM, MP4_KEEP_FROM, True)
        self.assertEqual(len(plan), 1, '.meta は独立した項目にしない')
        size, path, _isdir, _reason = plan[0]
        self.assertFalse(path.endswith('.meta'))
        self.assertEqual(size, 2, '本体 1 バイト + .meta 1 バイト')


if __name__ == '__main__':
    unittest.main()
