"""Unity の実行ログの伏字処理（tools/unity-log-filter.ps1）を PC の門で回す。

本体の検査は tools/test-unity-log-filter.ps1。ここはそれを別の PowerShell で起こし、終了コードだけを見る
（同じプロセスで .ps1 を呼ぶと $LASTEXITCODE が更新されず、合否を読み違える）。
"""

import shutil
import subprocess
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent


class UnityLogFilterTest(unittest.TestCase):
    @unittest.skipUnless(shutil.which("powershell.exe"), "Windows の PowerShell が無い")
    def test_masks_tokens_including_a_log_held_by_another_process(self):
        proc = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(TOOLS / "test-unity-log-filter.ps1")],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        self.assertEqual(proc.returncode, 0, proc.stdout[-2000:] + proc.stderr[-2000:])
        self.assertIn("held log masked in place", proc.stdout)


if __name__ == "__main__":
    unittest.main()
