"""ハーネス（CLAUDE.md / .claude/）の機械の門。

    py -3.11 tools/harness-check.py          # 全部見る。ERROR があれば exit 1
    py -3.11 tools/harness-check.py --quiet  # ERROR / WARN の行だけ出す（hook 用）

2026-10-22 のハーネス見直しで作った。rules/streaming.md が 351KB まで積み増され、
該当コードを触るたびに 18 万字が載っていた。書き方の規律（rules/doc-sync.md）だけでは
止まらなかったので、ここで数える。

見るもの:
  1. rule のサイズ（毎回載る rule ほど厳しく）と、旧仕様を示す語
  2. memory/MEMORY.md の字数・1 行の長さ・本文ファイルとの 1 対 1
  3. CLAUDE.md / rules / skills / reference / canon の相対リンクが実在するか
  4. plans の status が active / done / archived のどれか
  5. canon/LEDGER.md の番号の新しい重複
  6. canon/NOW.md があれば tools/canon-now.py の検査
上限を超えたら要約して縮めない。節ごと memory/ か reference/ へ移し、rule には指す行を残す。
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
CL = ROOT / ".claude"

# paths: の無い rule は全セッション・全サブエージェントに載る
LIMIT_ALWAYS = 6_000
LIMIT_SCOPED = 12_000
# 上限を超えたまま残っている rule。ここに書いた値より育ったら ERROR（縮める方向にだけ動かす）
GRANDFATHER = {
    "sound-design.md": 146_000,
    "visual-verification.md": 18_000,
    "unity-vr.md": 15_500,
}
OLD_WORDS = re.compile(r"旧仕様|旧記録|以下は旧")
MEMORY_LIMIT = 14_000
MEMORY_LINE_LIMIT = 220
PLAN_STATUS = {"active", "done", "archived"}
# 2026-10-22 時点で既に重複していた LEDGER 番号（追記のみなので直さない。新しい重複だけ落とす）
LEDGER_KNOWN_DUP = {
    "0007", "0016", "0017", "0022", "0023", "0024", "0025", "0026", "0027", "0028", "0029",
    "0050", "0075", "0076", "0091", "0094", "0096", "0131", "0155", "0275",
}

errors: list[str] = []
warns: list[str] = []


def rel(p: Path) -> str:
    return p.relative_to(ROOT).as_posix()


def frontmatter(text: str) -> str:
    m = re.match(r"---\r?\n(.*?)\r?\n---", text, re.S)
    return m.group(1) if m else ""


def check_rules() -> None:
    for p in sorted((CL / "rules").glob("*.md")):
        text = p.read_text(encoding="utf-8")
        size = len(text.encode("utf-8"))
        fm = frontmatter(text)
        if re.search(r"^globs:", fm, re.M):
            errors.append(f"{rel(p)}: `globs:` は Claude Code が読まない。`paths:` にする")
        scoped = bool(re.search(r"^paths:", fm, re.M))
        cap = GRANDFATHER.get(p.name)
        if cap is not None:
            if size > cap:
                errors.append(f"{rel(p)}: {size:,}B。据え置きの上限 {cap:,}B を超えた（育てない。節を memory/ か reference/ へ移す）")
            else:
                warns.append(f"{rel(p)}: {size:,}B は上限超えの据え置き。話題別に分けて GRANDFATHER から外す")
            continue
        limit = LIMIT_SCOPED if scoped else LIMIT_ALWAYS
        if size > limit:
            kind = "paths: 付き" if scoped else "常時ロード"
            errors.append(f"{rel(p)}: {size:,}B > {kind}の上限 {limit:,}B。節ごと memory/ か reference/ へ移す（要約しない）")
        for i, line in enumerate(text.splitlines(), 1):
            if OLD_WORDS.search(line):
                errors.append(f"{rel(p)}:{i}: 旧仕様を示す語。rule には現行の契約だけを書き、旧い行は消す")


def check_memory() -> None:
    idx = CL / "memory" / "MEMORY.md"
    text = idx.read_text(encoding="utf-8")
    size = len(text.encode("utf-8"))
    if size > MEMORY_LIMIT:
        errors.append(f"{rel(idx)}: {size:,}B > {MEMORY_LIMIT:,}B。1 行 1 フックに縮める（常時ロード）")
    for i, line in enumerate(text.splitlines(), 1):
        if len(line) > MEMORY_LINE_LIMIT:
            warns.append(f"{rel(idx)}:{i}: {len(line)} 字。フックは 1 行で短く（詳しくは本文へ）")
    links = re.findall(r"\]\(([^)#]+\.md)\)", text)
    seen: dict[str, int] = {}
    for l in links:
        seen[l] = seen.get(l, 0) + 1
    files = {p.name for p in (CL / "memory").glob("*.md") if p.name != "MEMORY.md"}
    for name, n in seen.items():
        if n > 1:
            errors.append(f"{rel(idx)}: {name} が {n} 回載っている")
        if name not in files:
            errors.append(f"{rel(idx)}: {name} は存在しない")
    for name in sorted(files - set(seen)):
        errors.append(f"{rel(idx)}: memory/{name} が索引に無い")


LINK_RE = re.compile(r"\]\(([^)\s]+)\)")


def check_links() -> None:
    targets = [ROOT / "CLAUDE.md", ROOT / "README.md"]
    targets += sorted((CL / "rules").glob("*.md"))
    targets += sorted((CL / "skills").glob("*/SKILL.md"))
    targets += sorted(p for p in (CL / "reference").glob("*.md"))
    # LEDGER / ROUNDS / OPEN-closed は追記のみの記録。書いた日のリンクが後で消えても直さない
    targets += [CL / "canon" / n for n in ("NOW.md", "OPEN.md", "TASTE.md", "INDEX.md")]
    targets.append(CL / "memory" / "MEMORY.md")
    for p in targets:
        if not p.exists():
            continue
        text = p.read_text(encoding="utf-8")
        # コードブロックの中は見ない
        text = re.sub(r"```.*?```", "", text, flags=re.S)
        for m in LINK_RE.finditer(text):
            href = m.group(1).split("#", 1)[0].split(":", 1)[0] if not m.group(1).startswith(("http", "mailto")) else ""
            if not href or href.startswith(("http", "#", "<")):
                continue
            dest = (p.parent / href).resolve()
            if not dest.exists():
                errors.append(f"{rel(p)}: リンク切れ {m.group(1)}")


def check_plans() -> None:
    for p in sorted((CL / "plans").glob("2*.md")):
        fm = frontmatter(p.read_text(encoding="utf-8"))
        m = re.search(r"^status:\s*(.+)$", fm, re.M)
        if m and m.group(1).strip() not in PLAN_STATUS:
            errors.append(f"{rel(p)}: status は active / done / archived のどれか（補足は statusNote:）。いまは「{m.group(1).strip()[:30]}」")


def check_ledger() -> None:
    p = CL / "canon" / "LEDGER.md"
    nums = re.findall(r"^#{2,3} (\d{4}) ", p.read_text(encoding="utf-8"), re.M)
    count: dict[str, int] = {}
    for n in nums:
        count[n] = count.get(n, 0) + 1
    for n, c in sorted(count.items()):
        if c > 1 and n not in LEDGER_KNOWN_DUP:
            errors.append(f"{rel(p)}: 番号 {n} が {c} 回使われている。新しい項目は最後の番号の次を使う")


def check_now() -> None:
    script = ROOT / "tools" / "canon-now.py"
    if not (CL / "canon" / "NOW.md").exists() or not script.exists():
        return
    r = subprocess.run([sys.executable, str(script), "--check"], cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
    for line in r.stdout.splitlines():
        if line.startswith("ERROR"):
            errors.append("canon/NOW.md: " + line[6:].strip())
        elif line.startswith("WARN"):
            warns.append("canon/NOW.md: " + line[5:].strip())


def main() -> int:
    quiet = "--quiet" in sys.argv
    check_rules()
    check_memory()
    check_links()
    check_plans()
    check_ledger()
    check_now()
    for e in errors:
        print("ERROR", e)
    for w in warns:
        print("WARN ", w)
    if not quiet:
        print(f"harness-check: ERROR {len(errors)} / WARN {len(warns)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
