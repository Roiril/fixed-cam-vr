# -*- coding: utf-8 -*-
"""異変（非場所依存）と場所（環境依存）の読み書き。**compose と judge の単一の正**。

ここが 2 つに分かれていることが全部の要点:

    異変 anomaly/*.md   何が起きるか。どんな面を要るか。どこにも依存しない
    場所 sites/*.json   その面が「この画像のどこか」。当日ここだけ書く

`compose.py` はこの 2 つからプロンプトを組み、`judge.py` は**同じ箱**で合否を測る。
箱の定義が 2 か所にあると、プロンプトと判定が黙って食い違う。
"""
from __future__ import annotations

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
ANOMALY_DIR = os.path.join(HERE, "anomaly")
SITE_DIR = os.path.join(HERE, "sites")
PROMPT_DIR = os.path.join(HERE, "prompt")

SURFACE_JA = {
    "floor": "床",
    "cloth": "布（カーテン・幕）",
    "wall": "壁",
    "furniture": "什器",
    "ceiling": "天井",
}


# --------------------------------------------------------------------- 読む

def _front_matter(text: str) -> tuple[dict, str]:
    """`---` で挟んだ `key: value` の頭書きと本文へ分ける（PyYAML は使わない）。"""
    meta: dict = {}
    body = text
    if text.startswith("---"):
        end = text.find("\n---", 3)
        if end > 0:
            head = text[3:end]
            body = text[end + 4:].lstrip("\n")
            for line in head.splitlines():
                line = line.strip()
                if not line or line.startswith("#") or ":" not in line:
                    continue
                k, v = line.split(":", 1)
                meta[k.strip()] = v.strip()
    return meta, body


def _num(meta: dict, key: str, default: float) -> float:
    try:
        return float(meta.get(key, default))
    except (TypeError, ValueError):
        return default


def _flag(meta: dict, key: str, default: bool) -> bool:
    v = str(meta.get(key, "")).lower()
    return default if v == "" else v in ("1", "true", "yes", "on")


def load_anomaly(anomaly_id: str) -> dict:
    path = os.path.join(ANOMALY_DIR, f"{anomaly_id}.md")
    with open(path, encoding="utf-8") as f:
        meta, body = _front_matter(f.read())
    refs = [r.strip() for r in meta.get("refs", "").split(",") if r.strip()]
    return dict(
        id=meta.get("id", anomaly_id),
        title=meta.get("title", anomaly_id),
        surface=meta.get("surface", "floor"),
        refs=[os.path.join(REPO, r) if not os.path.isabs(r) else r for r in refs],
        opaque=_flag(meta, "opaque", True),
        min_area_pct=_num(meta, "min_area_pct", 1.0),
        max_area_pct=_num(meta, "max_area_pct", 70.0),
        min_parts=int(_num(meta, "min_parts", 1)),
        persp=_flag(meta, "persp", False),
        body=body.strip(),
        path=path,
    )


def list_anomalies() -> list[str]:
    return sorted(os.path.splitext(f)[0] for f in os.listdir(ANOMALY_DIR) if f.endswith(".md"))


def load_site(site_id: str) -> dict:
    path = os.path.join(SITE_DIR, f"{site_id}.json")
    with open(path, encoding="utf-8") as f:
        site = json.load(f)
    site.setdefault("id", site_id)
    plate = site.get("plate", "")
    site["plate_abs"] = plate if os.path.isabs(plate) else os.path.join(REPO, plate)
    site["path"] = path
    return site


def list_sites() -> list[str]:
    if not os.path.isdir(SITE_DIR):
        return []
    return sorted(os.path.splitext(f)[0] for f in os.listdir(SITE_DIR) if f.endswith(".json"))


def surface_box(site: dict, surface: str, size: tuple[int, int]) -> list[int]:
    """場所が持つ面の箱。

    ⚠ **無い面は画面全体で代用しない。** 黙って全体にすると、布を要る異変が
    床にも壁にも描かれて「その場所には成立しない異変だった」ことが隠れる。
    """
    s = (site.get("surfaces") or {}).get(surface)
    if not s:
        have = ", ".join(sorted((site.get("surfaces") or {}).keys())) or "（面の記述が無い）"
        raise SystemExit(
            f"場所 {site.get('id')} に「{SURFACE_JA.get(surface, surface)}」の面が無い。\n"
            f"  この場所が持つ面: {have}\n"
            f"  → sites/{site.get('id')}.json に足すか、その面を要らない異変を選ぶ")
    return [int(v) for v in s["box"]]


def surface_say(site: dict, surface: str) -> str:
    s = (site.get("surfaces") or {}).get(surface)
    return (s or {}).get("say") or SURFACE_JA.get(surface, surface)


def surface_rules(surface: str) -> str:
    """`prompt/surfaces.md` から `## <surface>` の節を取り出す。"""
    with open(os.path.join(PROMPT_DIR, "surfaces.md"), encoding="utf-8") as f:
        text = f.read()
    want = f"\n## {surface}\n"
    i = text.find(want)
    if i < 0:
        return "（この面に固有の規則は無い）"
    j = text.find("\n## ", i + 1)
    return text[i + len(want):(j if j > 0 else len(text))].strip()


# --------------------------------------------------------------------- 置き場所

def parse_place(place: str, size: tuple[int, int]) -> dict:
    """置いてよい領域と、置かない領域を返す。

    `left-half` / `right-half` / `left-40` / `right-40` / `full` /
    `x0,y0,x1,y1`（画素）。
    """
    w, h = size
    p = (place or "full").strip().lower()
    if p == "full":
        return dict(place=p, box=[0, 0, w, h], keep_out=None, say="画面全体")
    if p.startswith(("left", "right")):
        side, _, frac = p.partition("-")
        f = 0.5 if frac in ("half", "") else max(0.05, min(0.95, float(frac) / 100.0))
        cut = int(round(w * f))
        if side == "left":
            return dict(place=p, box=[0, 0, cut, h], keep_out=[cut, 0, w, h],
                        say=f"画面の左 {int(f * 100)}%（x 0..{cut}）",
                        keep_say=f"画面の右側（x {cut}..{w}）")
        return dict(place=p, box=[cut, 0, w, h], keep_out=[0, 0, cut, h],
                    say=f"画面の右 {int(round((1 - f) * 100))}%（x {cut}..{w}）",
                    keep_say=f"画面の左側（x 0..{cut}）")
    nums = [int(float(v)) for v in p.replace(" ", "").split(",")]
    if len(nums) != 4:
        raise ValueError(f"置き場所を読めない: {place}")
    return dict(place=p, box=nums, keep_out=None,
                say=f"画面の x {nums[0]}..{nums[2]} / y {nums[1]}..{nums[3]}")


def intersect(a, b) -> list[int]:
    return [max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])]


def box_say(box, size) -> str:
    w, h = size
    return (f"x {box[0]}..{box[2]} / y {box[1]}..{box[3]}"
            f"（画面の横 {box[0] * 100 // w}〜{box[2] * 100 // w}% ・"
            f"縦 {box[1] * 100 // h}〜{box[3] * 100 // h}%）")
