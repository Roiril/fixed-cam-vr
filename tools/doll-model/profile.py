# マスクを仕上げて、寸法を数値で読み出す。
#  - 頭上に残る細い縦柱（黒シャツ）を横長カーネルの open で落とす
#  - 各高さ t（0=頭頂, 1=足元）での 左端 / 右端 / 幅 / 中心 を比率で出す
#  - front = 左右幅、sideB = 前後の奥行き
# 出力: profile.json（Blender へ渡す）＋ 目視用の等高線オーバーレイ
import cv2, numpy as np, os, json

HERE = os.path.dirname(os.path.abspath(__file__))
N = 48   # サンプルする高さの段数


def clean(m, W):
    # 細い縦柱を落とす（横長カーネルの open）。髪の幅は W の 25% 以上あるので消えない。
    kw = max(5, int(W * 0.17)) | 1
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((1, kw), np.uint8))
    # ⚠ 最大成分だけ残してはいけない。背面は帯が頭と胴を分断するので頭が丸ごと消える（実測）。
    n, lab, stats, _ = cv2.connectedComponentsWithStats((m > 0).astype(np.uint8), 8)
    total = m.shape[0] * m.shape[1]
    out = np.zeros_like(m)
    for i in range(1, n):
        if stats[i, cv2.CC_STAT_AREA] >= total * 0.01:
            out[lab == i] = 255
    return out


def rows(name):
    m = cv2.imread(os.path.join(HERE, f"mask_{name}.png"), cv2.IMREAD_GRAYSCALE)
    H, W = m.shape
    m = clean(m, W)
    cv2.imwrite(os.path.join(HERE, f"mask_{name}.png"), m)

    ys, xs = np.nonzero(m)
    y0, y1 = int(ys.min()), int(ys.max())
    span = max(1, y1 - y0)

    out = []
    for i in range(N + 1):
        t = i / N
        y = min(H - 1, int(y0 + t * span))
        band = m[max(0, y - span // (N * 2)): y + max(1, span // (N * 2)) + 1]
        cols = np.nonzero(band.max(axis=0))[0]
        if len(cols) == 0:
            out.append(dict(t=round(t, 4), half=0.0, cx=0.5, gap=0.0))
            continue
        xl, xr = int(cols.min()), int(cols.max())
        # 左右に分かれている行（腕の高さで袖と胴が離れる）の最大空隙も測る
        gap = 0
        if len(cols) > 1:
            d = np.diff(cols)
            gap = int(d.max()) - 1 if len(d) else 0
        out.append(dict(t=round(t, 4),
                        half=round((xr - xl) / 2 / span, 4),      # 全高を 1 とした半幅
                        cx=round(((xr + xl) / 2 - xl) / span, 4),  # 使わないが保持
                        left=round((xl - xs.min()) / span, 4),
                        right=round((xr - xs.min()) / span, 4),
                        gap=round(gap / span, 4)))
    return dict(name=name, y0=y0, y1=y1, span=span, W=W, H=H,
                x0=int(xs.min()), x1=int(xs.max()),
                aspect=round((xs.max() - xs.min()) / span, 4), rows=out)


data = {n: rows(n) for n in ["front", "back", "sideA", "sideB"]}
with open(os.path.join(HERE, "profile.json"), "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False, indent=1)

# 数値を読むための表（front = 左右幅 / sideB = 前後厚み）
print(f"{'t':>5} {'front半幅':>9} {'sideB半幅':>9} {'front空隙':>9}")
for a, b in zip(data["front"]["rows"], data["sideB"]["rows"]):
    print(f"{a['t']:5.2f} {a['half']:9.4f} {b['half']:9.4f} {a['gap']:9.4f}")
print("\naspect(横/縦):", {k: v["aspect"] for k, v in data.items()})
