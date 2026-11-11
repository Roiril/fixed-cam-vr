"""Unity の実時間の直読みを SimTime / WallClock へ置き換える。既定は dry-run。

    py -3.11 tools/simtime-migrate.py           # 何が置き換わるか見るだけ
    py -3.11 tools/simtime-migrate.py --apply   # 書く

対象は `Assets/Scripts/**/*.cs`（`Editor/` の下も含む）。置き換えは次の 3 つ。

- `Time.unscaledTime`         → `SimTime.Now`
- `Time.unscaledDeltaTime`    → `SimTime.Dt`
- `Time.realtimeSinceStartup` → `SimTime.Realtime`。ただし `WALL_FILES` のファイルは `WallClock.Now`
  （ネットワーク・ファイル I/O の待ち・性能計測。固定刻みでも実時間が要る）

コメント・doc コメント・文字列リテラルの中は書き換えない。補間文字列の `{...}` の中はコードとして扱う。
名前空間が `FixedCamVr.Streaming`（とその下）でないファイルには `using FixedCamVr.Streaming;` を足す。
`.cs` は UTF-8 で読み書きし、BOM と改行コードは元のまま。一時ファイルへ書いて `os.replace` する。

何度流しても同じ結果になる（置き換え済みの読みは対象に出ない）。門は
`Assets/Tests/Streaming/TimeSourceLintTests.cs`。
"""

import argparse
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(PROJ, "Assets", "Scripts")

# Assets/Scripts からの相対パス（区切りは /）。

# 時計そのものを定義するファイル。中の Unity の読みは正しい。
DEFINITION_FILES = {
    "Streaming/SimTime.cs",
    "Streaming/WallClock.cs",
}

# 読むだけで書かないファイル（いまは無い）。TimeSourceLintTests.PendingFiles と同じ一覧。
EXCLUDED_FILES: set = set()

# 他の人の未コミット変更がある。読みがあっても書かず、報告だけする。
REPORT_ONLY_FILES = {
    "Streaming/TakeRunner.cs",
}

# FixedCamVr.Streaming を参照していない asmdef。置き換えるとコンパイルが通らないので報告だけ。
REPORT_ONLY_DIRS = (
    "Input/",
    "Fx/",
)

# realtimeSinceStartup を壁時計へ振り分けるファイル。ここに無いファイルは SimTime.Realtime。
WALL_FILES = {
    "Streaming/ScreenOverlayController.cs": "クリップの取得・Prepare の待ち・所要の計測",
    "OvrBridge/CommsPerformanceProbe.cs": "性能ログ",
    "Diagnostics/CommsPanel.cs": "性能ログ",
    "Streaming/DiscoveryClient.cs": "UDP 発見・配信の鮮度",
    "Streaming/ShowControlClient.cs": "卓との通信の鮮度",
    "Streaming/ShowControlClient.Live.cs": "卓との通信の鮮度",
    "Streaming/ExperienceShotCapture.cs": "保存写真のアプリ時刻",
}

READ = re.compile(r"(?<![\w.])(?:UnityEngine\.)?Time\.(unscaledTime|unscaledDeltaTime|realtimeSinceStartup)\b")
USING_STREAMING = re.compile(r"(?m)^[ \t]*using[ \t]+FixedCamVr\.Streaming[ \t]*;")
USING_LINE = re.compile(r"(?m)^[ \t]*using[ \t]+(?!static\b)([\w.]+)[ \t]*;[ \t]*\r?$")
NAMESPACE = re.compile(r"\bnamespace\s+([\w.]+)")

KIND_NOW, KIND_DT, KIND_REAL, KIND_WALL = "Now", "Dt", "Realtime", "WallClock"
TARGET = {
    KIND_NOW: "SimTime.Now",
    KIND_DT: "SimTime.Dt",
    KIND_REAL: "SimTime.Realtime",
    KIND_WALL: "WallClock.Now",
}


def code_mask(src: str) -> str:
    """コメント・文字列・文字リテラル・プリプロセッサ行を空白に潰した同じ長さの文字列。
    改行は残すので、位置と行番号は元の文字列とそのまま対応する。補間文字列の穴はコードとして残す。"""
    n = len(src)
    out = list(src)

    def blank(a: int, b: int) -> None:
        for k in range(a, min(b, n)):
            if out[k] not in "\r\n":
                out[k] = " "

    def at_line_start(i: int) -> bool:
        j = i - 1
        while j >= 0 and src[j] in " \t":
            j -= 1
        return j < 0 or src[j] == "\n"

    def lex_regular(i: int) -> int:  # src[i] == '"'
        j = i + 1
        while j < n:
            c = src[j]
            if c == "\\":
                j += 2
                continue
            if c == '"':
                j += 1
                break
            if c == "\n":
                break
            j += 1
        blank(i, j)
        return j

    def lex_verbatim(start: int, i: int) -> int:  # src[i] == '"'、start は @ の位置
        j = i + 1
        while j < n:
            if src[j] == '"':
                if j + 1 < n and src[j + 1] == '"':
                    j += 2
                    continue
                j += 1
                break
            j += 1
        blank(start, j)
        return j

    def lex_char(i: int) -> int:  # src[i] == "'"
        j = i + 1
        if j < n and src[j] == "\\":
            j += 2
        else:
            j += 1
        while j < n and src[j] not in "'\n":
            j += 1
        if j < n and src[j] == "'":
            j += 1
        blank(i, j)
        return j

    def lex_interpolated(start: int, i: int, verbatim: bool) -> int:  # src[i] == '"'
        blank(start, i + 1)
        j = i + 1
        while j < n:
            c = src[j]
            if not verbatim and c == "\\":
                blank(j, j + 2)
                j += 2
                continue
            if c == '"':
                if verbatim and j + 1 < n and src[j + 1] == '"':
                    blank(j, j + 2)
                    j += 2
                    continue
                blank(j, j + 1)
                return j + 1
            if c == "{":
                if j + 1 < n and src[j + 1] == "{":
                    blank(j, j + 2)
                    j += 2
                    continue
                blank(j, j + 1)
                j = lex_code(j + 1, in_hole=True)  # 穴を閉じる '}' の位置
                if j < n:
                    blank(j, j + 1)
                    j += 1
                continue
            if c == "\n" and not verbatim:
                return j
            blank(j, j + 1)
            j += 1
        return j

    def lex_code(i: int, in_hole: bool) -> int:
        depth = 0
        while i < n:
            c = src[i]
            nx = src[i + 1] if i + 1 < n else ""
            if c == "/" and nx == "/":
                j = src.find("\n", i)
                j = n if j < 0 else j
                blank(i, j)
                i = j
                continue
            if c == "/" and nx == "*":
                j = src.find("*/", i + 2)
                j = n if j < 0 else j + 2
                blank(i, j)
                i = j
                continue
            if c == "#" and not in_hole and at_line_start(i):
                j = src.find("\n", i)
                j = n if j < 0 else j
                blank(i, j)
                i = j
                continue
            if c == "$" and nx == '"':
                i = lex_interpolated(i, i + 1, verbatim=False)
                continue
            if (c == "$" and nx == "@") or (c == "@" and nx == "$"):
                if i + 2 < n and src[i + 2] == '"':
                    i = lex_interpolated(i, i + 2, verbatim=True)
                    continue
            if c == "@" and nx == '"':
                i = lex_verbatim(i, i + 1)
                continue
            if c == '"':
                i = lex_regular(i)
                continue
            if c == "'":
                i = lex_char(i)
                continue
            if in_hole:
                if c in "([{":
                    depth += 1
                elif c in ")]":
                    depth -= 1
                elif c == "}":
                    if depth == 0:
                        return i
                    depth -= 1
                elif c == ":" and depth == 0 and nx != ":":
                    # 書式指定（{x:F2}）は文字列の一部
                    j = i
                    while j < n and src[j] not in "}\n":
                        j += 1
                    blank(i, j)
                    i = j
                    continue
            i += 1
        return i

    lex_code(0, in_hole=False)
    return "".join(out)


def rel_of(path: str) -> str:
    return os.path.relpath(path, SCRIPTS).replace(os.sep, "/")


def kind_of(rel: str, member: str) -> str:
    if member == "unscaledTime":
        return KIND_NOW
    if member == "unscaledDeltaTime":
        return KIND_DT
    return KIND_WALL if rel in WALL_FILES else KIND_REAL


def line_of(src: str, pos: int) -> int:
    return src.count("\n", 0, pos) + 1


def needs_using(mask: str) -> bool:
    m = NAMESPACE.search(mask)
    ns = m.group(1) if m else ""
    if ns == "FixedCamVr.Streaming" or ns.startswith("FixedCamVr.Streaming."):
        return False
    return USING_STREAMING.search(mask) is None


def add_using(src: str, mask: str) -> str:
    """`using FixedCamVr.Streaming;` を、最初の namespace / 型より前の using の並びへ足す。
    FixedCamVr.* があればその中の名前順の位置、無ければ最後の System.* の後、それも無ければ最初の using の前。"""
    first_ns = NAMESPACE.search(mask)
    limit = first_ns.start() if first_ns else len(src)
    usings = [m for m in USING_LINE.finditer(mask) if m.start() < limit]
    newline = "\r\n" if "\r\n" in src else "\n"
    line = "using FixedCamVr.Streaming;"
    if not usings:
        # using が 1 つも無い。#nullable 等の直後（先頭のプリプロセッサ行の後）へ置く。
        pos = 0
        for m in re.finditer(r"(?m)^[ \t]*#.*\r?\n", src):
            if m.start() != pos:
                break
            pos = m.end()
        return src[:pos] + line + newline + src[pos:]
    own = [m for m in usings if m.group(1).startswith("FixedCamVr.")]
    if own:
        after = [m for m in own if m.group(1) < "FixedCamVr.Streaming"]
        anchor_end = (after[-1] if after else None)
        if anchor_end is None:
            pos = own[0].start()
            return src[:pos] + line + newline + src[pos:]
        pos = src.find("\n", anchor_end.start()) + 1
        return src[:pos] + line + newline + src[pos:]
    system = [m for m in usings if m.group(1) == "System" or m.group(1).startswith("System.")]
    if system:
        pos = src.find("\n", system[-1].start()) + 1
        return src[:pos] + line + newline + src[pos:]
    pos = usings[0].start()
    return src[:pos] + line + newline + src[pos:]


def read_cs(path: str):
    with open(path, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
    return text, bom


def write_cs(path: str, text: str, bom: bool) -> None:
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    tmp = path + ".simtime-tmp"
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, path)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--apply", action="store_true", help="ファイルへ書く（既定は dry-run）")
    args = ap.parse_args()

    totals = {KIND_NOW: 0, KIND_DT: 0, KIND_REAL: 0, KIND_WALL: 0}
    wall_sites = []
    excluded_counts = {}
    report_only = []
    using_added = []
    changed_files = 0

    files = []
    for root, _dirs, names in os.walk(SCRIPTS):
        for name in names:
            if name.endswith(".cs"):
                files.append(os.path.join(root, name))
    files.sort()

    for path in files:
        rel = rel_of(path)
        if rel in DEFINITION_FILES:
            continue
        src, bom = read_cs(path)
        mask = code_mask(src)
        hits = list(READ.finditer(mask))
        if not hits:
            continue
        if rel in EXCLUDED_FILES:
            excluded_counts[rel] = len(hits)
            continue
        if rel in REPORT_ONLY_FILES or rel.startswith(REPORT_ONLY_DIRS):
            for m in hits:
                report_only.append(f"{rel}:{line_of(src, m.start())} {m.group(0)}")
            continue

        print(f"{rel}")
        pieces = []
        last = 0
        for m in hits:
            kind = kind_of(rel, m.group(1))
            totals[kind] += 1
            ln = line_of(src, m.start())
            print(f"  {ln:5d}  {m.group(0)} -> {TARGET[kind]}")
            if kind == KIND_WALL:
                wall_sites.append(f"{rel}:{ln}  {WALL_FILES[rel]}")
            pieces.append(src[last:m.start()])
            pieces.append(TARGET[kind])
            last = m.end()
        pieces.append(src[last:])
        new_src = "".join(pieces)
        new_mask = code_mask(new_src)
        if needs_using(new_mask):
            new_src = add_using(new_src, new_mask)
            using_added.append(rel)
            print("         + using FixedCamVr.Streaming;")
        changed_files += 1
        if args.apply:
            write_cs(path, new_src, bom)

    pending = sum(totals.values())
    verb = "置き換えた" if args.apply else "置き換える"
    print()
    print(f"{verb}読み: {pending} 件 / {changed_files} ファイル"
          f"（Now {totals[KIND_NOW]} / Dt {totals[KIND_DT]} / Realtime {totals[KIND_REAL]} / WallClock {totals[KIND_WALL]}）")
    if using_added:
        print(f"using を足すファイル: {len(using_added)}")
        for rel in using_added:
            print(f"  {rel}")
    if wall_sites:
        print("WallClock へ振り分けた読み:")
        for s in wall_sites:
            print(f"  {s}")
    print(f"除外ファイルの残り（書かない）: {sum(excluded_counts.values())} 件")
    for rel in sorted(EXCLUDED_FILES):
        print(f"  {rel}: {excluded_counts.get(rel, 0)}")
    if report_only:
        print(f"置き換えずに残した読み（asmdef が Streaming を参照しない / 他の人の編集中）: {len(report_only)} 件")
        for s in report_only:
            print(f"  {s}")
    else:
        print("置き換えずに残した読み（asmdef が Streaming を参照しない / 他の人の編集中）: 0 件")
    if not args.apply and pending:
        print("dry-run。書くなら --apply")
    return 0


if __name__ == "__main__":
    sys.exit(main())
