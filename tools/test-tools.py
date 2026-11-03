"""tools/ の Python テストをまとめて走らせる入口。

Unity のテスト（`.\\tools\\unity.ps1 test`）とは別に、解析器・現場の道具・ログの照合のテストがここにある。
どれも実機や Unity を起動せずに数秒で終わる。1 つでも落ちたら終了コード 1。

    py -3.11 tools/test-tools.py

テストを足すときは tools/ に test-*.py か test_*.py の名前で置けば、ここが拾う。
"""

import subprocess
import sys
import time
from pathlib import Path

TOOLS = Path(__file__).resolve().parent


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    tests = sorted(p for p in TOOLS.glob("test*.py") if p.name != Path(__file__).name)
    failed = []
    for path in tests:
        start = time.monotonic()
        proc = subprocess.run([sys.executable, "-X", "utf8", str(path)], cwd=TOOLS.parent,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        sec = time.monotonic() - start
        summary = next((l for l in reversed(proc.stderr.splitlines()) if l.startswith(("Ran ", "OK", "FAILED"))), "")
        ran = next((l for l in proc.stderr.splitlines() if l.startswith("Ran ")), "")
        status = "OK  " if proc.returncode == 0 else "FAIL"
        print(f"{status} {path.name:36s} {ran or summary}  ({sec:.1f}s)")
        if proc.returncode != 0:
            failed.append(path.name)
            print(proc.stderr[-3000:])
    print(f"\n{len(tests) - len(failed)} / {len(tests)} 通過")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
