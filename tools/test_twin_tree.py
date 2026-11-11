"""tools/twin-tree.py の、持ち込むファイルの選び方と木の場所。git も Unity も動かさない。"""

import importlib.util
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("twin_tree", TOOLS / "twin-tree.py")
tt = importlib.util.module_from_spec(spec)
sys.modules["twin_tree"] = tt
spec.loader.exec_module(tt)


class Wanted(unittest.TestCase):
    def test_inputs_are_carried(self):
        self.assertTrue(tt.wanted("Assets/StreamingAssets/show/show.json"))
        self.assertTrue(tt.wanted("Assets/Scripts/Streaming/New.cs.meta"))
        self.assertTrue(tt.wanted("tools/web-compositor/show.json"))

    def test_tree_local_state_is_not_carried(self):
        for rel in ("Library/ArtifactDB", "Temp/UnityLockfile", "Logs/twin-run/x/run_xp.log",
                    "tools/__pycache__/a.pyc", "UserSettings/Layouts/x.dwlt", "obj/Debug/a.dll"):
            self.assertFalse(tt.wanted(rel), rel)


class TreePath(unittest.TestCase):
    def test_tree_path_does_not_contain_the_main_path(self):
        # unity.ps1 の Assert-NotLocked は、コマンドラインに自分のパスを含む Unity を自分の Editor と数える。
        self.assertNotIn(str(tt.ROOT).lower(), str(tt.TREE).lower())
        self.assertNotIn(str(tt.TREE).lower(), str(tt.ROOT).lower())

    def test_tree_is_outside_the_main_tree(self):
        self.assertNotIn(tt.ROOT.resolve(), tt.TREE.resolve().parents)


if __name__ == "__main__":
    unittest.main()
