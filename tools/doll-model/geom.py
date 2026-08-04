# 殻の幾何を**1 箇所**に置く。shell.py（メッシュを作る）と texture.py（テクスチャを焼く）が
# 同じ式を使うための共有モジュール。
#
# ⚠ ここを分けて 2 箇所に書くと、**沈黙して食い違う**（このリポジトリが何度も踏んでいる形）。
#    テクスチャは「モデルのこの点が写真のどこに写っているか」を知らないと焼けないので、
#    幾何とテクスチャは同じ関数から導出する。
#
# 提供するもの:
#   Shell        … 行 j / 列 i から 3D 位置・法線・UV を返す（shell.py と同じ式）
#   Shell.uv_to_surface … アトラスのテクセル (u,v) から逆に表面点を引く（texture.py が使う）
#   photo_map()  … 高さ t ごとに写真の左右端を返す位置合わせ（profile.json 由来）
import cv2, numpy as np, json, os, math

HERE = os.path.dirname(os.path.abspath(__file__))

ROWS, COLS = 144, 34         # 縦の刻み / 各行の横分割
SLEEVE_THIN = 0.50           # 胴から張り出した部分（袖）の厚みを胴の何倍まで落とすか
BODY_HALF_N = 0.135          # 胴の半幅（全高 = 1 に正規化）。これより外を「張り出し」と見る
DOLL_H = 0.40
HAIR_BULGE = 0.24            # 髪は顔より前後に張り出す
FOOT_CUT = 0.014             # 足元の最下部を切る割合（台の潰れた面が乱れて見えるため）
TOP_ROUND = 0.085            # 頭頂を球で閉じる区間（全高に対する割合）

# アトラスの寸法。texture.py が atlas.json へ書き出し、shell.py が v の詰まりに使う。
# **ここが唯一の定義**（旧: texture.py と shell.py が別々に持っていて対で直す必要があった）。
HALF_W, BODY_H = 512, 1024
ARM_H = 160                       # 下段（手のストリップ）の高さ
TEX_W, TEX_H = HALF_W * 2, BODY_H + ARM_H
BODY_V0 = ARM_H / TEX_H
BODY_VS = 1.0 - BODY_V0
HALF_TEXEL_U = 0.5 / TEX_W
HALF_TEXEL_V = 0.5 / TEX_H


def _load_gray(name):
    p = os.path.join(HERE, name)
    im = cv2.imread(p, cv2.IMREAD_GRAYSCALE)
    if im is None:
        raise FileNotFoundError(p)
    return im


def hair_region():
    """**髪らしさ**を正面写真から直に起こす（0..1 の、正規化した (nx, t) 空間のマップ）。

    ⚠ 旧実装は `doll_albedo.png`（texture.py の出力）を shell.py が読んでいた。
       テクスチャを幾何から焼くようになると循環するので、写真から直接決める。
       ついでに「前の実行のアルベドを読む」という実行順依存も消える。

    髪は**暗くて彩度が低い**。着物は暗くても彩度が高い（S 217）ので S で切り分ける。
    """
    src = cv2.imread(os.path.join(HERE, "front.jpg"))
    m = _load_gray("mask_front.png")
    hsv = cv2.cvtColor(src, cv2.COLOR_BGR2HSV)
    v = hsv[:, :, 2].astype(np.float32)
    s = hsv[:, :, 1].astype(np.float32)
    hair = (((v < 102) & (s < 125)) & (m > 0)).astype(np.uint8) * 255
    hair = cv2.morphologyEx(hair, cv2.MORPH_OPEN, np.ones((25, 25), np.uint8))
    hair = cv2.morphologyEx(hair, cv2.MORPH_CLOSE, np.ones((41, 41), np.uint8))
    return hair


class Shell:
    """正面マスクを前後へ押し出した殻。shell.py の式をそのまま持つ。"""

    def __init__(self):
        m = _load_gray("mask_front.png")
        self.mask = m
        H, W = m.shape
        ys, xs = np.nonzero(m)
        self.x0, self.x1 = int(xs.min()), int(xs.max())
        self.y0, self.y1 = int(ys.min()), int(ys.max())
        self.bw = self.x1 - self.x0
        self.bh = self.y1 - self.y0
        self.scale = DOLL_H / self.bh
        self.foot_px = self.bh * FOOT_CUT
        self.y1_mesh = self.y1 - self.foot_px
        self.bh_mesh = self.bh - self.foot_px
        self.body_half_px = BODY_HALF_N * self.bh
        self.cxm = (self.x0 + self.x1) / 2

        side = json.load(open(os.path.join(HERE, "profile.json"), encoding="utf-8"))["sideB"]["rows"]
        self.side_t = np.array([r["t"] for r in side])
        sh = np.array([r["half"] for r in side])
        # ⚠ 48 行を 144 行へ補間する前に均す。そのままだと 8mm ごとに折れ、上から照らした
        #    ときに**横縞**として出る。
        self.side_h = np.convolve(np.pad(sh, 2, mode="edge"), np.ones(5) / 5.0, mode="valid")

        # 行ごとの左右端（上下 1 行を足した中央値で均す）
        raw = []
        for j in range(ROWS + 1):
            py = int(round(self.y1 - self.bh * j / ROWS))
            cols = np.nonzero(m[min(H - 1, max(0, py))])[0]
            raw.append((float(cols.min()), float(cols.max())) if len(cols) else None)
        for j in range(ROWS + 1):
            if raw[j] is not None:
                continue
            for k in range(1, ROWS + 2):
                if j - k >= 0 and raw[j - k] is not None:
                    raw[j] = raw[j - k]; break
                if j + k <= ROWS and raw[j + k] is not None:
                    raw[j] = raw[j + k]; break
            if raw[j] is None:
                raw[j] = (self.cxm, self.cxm)
        span = []
        for j in range(ROWS + 1):
            lo, hi = max(0, j - 1), min(ROWS, j + 1)
            ls = sorted(raw[k][0] for k in range(lo, hi + 1))
            rs = sorted(raw[k][1] for k in range(lo, hi + 1))
            span.append((ls[len(ls) // 2], rs[len(rs) // 2]))
        self.span = span

        # 髪マップ（写真の画素座標で引く）
        hr = hair_region()
        self.hair_blur = cv2.GaussianBlur(hr.astype(np.float32) / 255.0, (0, 0), 41.0)
        self._arc_cache = {}

    # --- 行の幾何 ---------------------------------------------------------
    def row(self, jf):
        """行 j（実数可）の 中心 cx / 半幅 half_w / 深さ depth / 端の閉じ cap / 頭頂の絞り top_k。"""
        j0 = int(np.clip(math.floor(jf), 0, ROWS))
        j1 = min(ROWS, j0 + 1)
        a = jf - j0
        xl = self.span[j0][0] * (1 - a) + self.span[j1][0] * a
        xr = self.span[j0][1] * (1 - a) + self.span[j1][1] * a
        cx = (xl + xr) / 2
        half_w = max(1.0, (xr - xl) / 2)
        top_u = (ROWS - jf) / max(1.0, TOP_ROUND * ROWS)
        top_k = 1.0 if top_u >= 1.0 else math.sqrt(max(0.0, 1.0 - (1.0 - top_u) ** 2))
        half_w *= max(top_k, 0.02)
        t = 1.0 - jf / ROWS
        depth = float(np.interp(t, self.side_t, self.side_h)) * DOLL_H
        cap = min(1.0, min(jf, ROWS - jf) / 1.15)
        return cx, half_w, depth, cap, max(top_k, 0.02)

    def _depth_at(self, px, cx, half_w, depth, cap, top_k):
        """深さ。**スカラでも numpy 配列でも同じ式**（テクスチャは行ごとにベクトルで焼く）。"""
        px = np.asarray(px, dtype=np.float64)
        dx = np.abs(px - cx)
        # ⚠ 内側の楕円の半径は **その行の半幅と胴の半幅の小さい方**。
        #    固定の body_half（54.0mm）だけを使うと、それより細い行（**頭は 42〜47mm**）は
        #    楕円の「てっぺん」しか使わない ＝ 断面が**角の丸い箱**になる。
        #    実測（修正前）: 頭は半幅の 99% の地点でまだ深さの 56〜64% を保ち、最後の 1% で 0 へ落ちていた。
        #    これが「側面が角の丸い茶色い箱に見える」の**形の側の原因**（テクスチャの問題ではない）。
        #    README が胴について書いていた失敗（幅の狭い行が楕円のてっぺんだけを使う）が、
        #    そのまま頭で起きていた。
        a_body = min(self.body_half_px, half_w)
        # 縁でも少し厚みを残す係数は、**外に袖がある行だけ**（胴と袖の境目が折れるのを防ぐため）。
        # 袖の無い行（頭・裾）で残すと、それがそのまま箱の角になる。
        flat = 0.92 if half_w > self.body_half_px else 1.0
        qb = dx / a_body
        db = depth * np.sqrt(np.maximum(0.0, 1.0 - flat * qb * qb))
        # 袖（外側の楕円）。⚠ 4 乗にして袖口の近くまで厚みを保つ（2 乗だと腕が布から出る）
        outer = max(1.0, half_w - a_body)
        qs = np.minimum(1.0, (dx - a_body) / outer)
        ds = depth * SLEEVE_THIN * np.sqrt(np.maximum(0.0, 1.0 - qs ** 4))
        d = np.where(dx <= a_body, db, ds)
        # ⚠ 行の左右端では必ず 0（前後が閉じる）。端の**ごく近く**だけで落とす
        edge = np.sqrt(np.maximum(0.0, 1.0 - np.minimum(1.0, dx / half_w) ** 2))
        out = d * np.minimum(1.0, edge * 7.0) * cap * top_k
        return out if out.ndim else float(out)

    # --- UV は**断面の弧長**で配る ------------------------------------------
    # ⚠ 旧実装は u ∝ px（正面から見た x の平面投影）だった。押し出した殻はシルエットへ
    #    向かって急に奥へ回り込むので、この配り方だと**真横を向く面に列がほとんど回らない**。
    #    実測（頭 t=0.12）: 真横寄り（傾き>60°）の面は**弧長の 38% を占めるのに列は 3%**
    #    ＝ 一様配分の 0.07 倍（14.4 倍の不足）。しかもこの行はパネル 512 列のうち 179 列
    #    （35%）しか使っておらず、残りは人形の外＝捨てていた。
    #    → 側面が「1 列を引き伸ばした横縞」になっていた（何を描いても縞になる、の正体）。
    #
    #    弧長で配り、かつ**各行がパネル全幅を使う**ようにすると、側面のテクセルは約 41 倍。
    #    シルエットは必ず u=0.5（パネル境界）に来るので、前後の継ぎ目の位置が全行で揃う。
    #    顔も「パネル全幅を使う」ぶんだけ得をする（捨てていた列が回ってくる）。
    ARC_N = 512

    def _arc(self, jf):
        key = round(float(jf), 4)
        hit = self._arc_cache.get(key)
        if hit is not None:
            return hit
        cx, half_w, depth, cap, top_k = self.row(jf)
        px = np.linspace(cx - half_w, cx + half_w, self.ARC_N)
        py = self.y1_mesh - self.bh_mesh * jf / ROWS
        d = self._depth_at(px, cx, half_w, depth, cap, top_k)
        d = d * (1.0 + HAIR_BULGE * self.hair_at(px, py))
        seg = np.hypot(np.diff(px) * self.scale, np.diff(d))
        s = np.concatenate([[0.0], np.cumsum(seg)])
        # 深さが全部 0 の行（頭頂・足元のフタ）では弧長 = |x| になり、旧来の配り方へ自然に縮退する
        s = s / s[-1] if s[-1] > 1e-9 else np.linspace(0.0, 1.0, self.ARC_N)
        self._arc_cache[key] = (px, s)
        return px, s

    def row_of_py(self, py):
        return (self.y1_mesh - py) * ROWS / self.bh_mesh

    # u は断面を**一周する**ように配る:
    #   前面シート  s: 0(-X のシルエット) → 1(+X のシルエット)  を u: 0   → 0.5 へ
    #   背面シート  s: 0(-X のシルエット) → 1(+X のシルエット)  を u: 1.0 → 0.5 へ（**逆向き**）
    #
    # ⚠ 背面を逆向きにしないと、u=0.5 に「前面の +X 端」と「背面の -X 端」＝**人形の反対側どうし**
    #    が重なる。旧来の平面投影 UV では頭がパネル幅の 35% しか使わず両端が衝突しなかったので
    #    表面化していなかったが、弧長で配ると必ず衝突する（実際、側面に左右対称の扇が出た）。
    #    逆向きにすれば u=0.5 が +X 端・u=0/1 が -X 端で、周回として連続する。
    _U_SPAN = 0.5 - 2.0 * HALF_TEXEL_U

    def panel_u(self, s, back):
        return (1.0 - HALF_TEXEL_U - s * self._U_SPAN) if back else (HALF_TEXEL_U + s * self._U_SPAN)

    def uv_of(self, px, py):
        """写真の画素 (px, py) → 断面の弧長 s∈[0,1] と v。u は panel_u(s, back) で作る。"""
        tx, ts = self._arc(np.clip(self.row_of_py(py), 0, ROWS))
        s = np.interp(px, tx, ts)
        vv = BODY_V0 + min((self.y1 - py) / self.bh, 1.0) * (BODY_VS - HALF_TEXEL_V)
        return (s if np.ndim(s) else float(s)), vv

    def s_of_u(self, u):
        """アトラスの列 u → 断面の弧長 s と、背面シートかどうか。"""
        back = np.asarray(u) >= 0.5
        s = np.where(back, (1.0 - HALF_TEXEL_U - np.asarray(u)) / self._U_SPAN,
                     (np.asarray(u) - HALF_TEXEL_U) / self._U_SPAN)
        return np.clip(s, 0.0, 1.0), back

    def px_of_s(self, jf, s):
        """弧長 s → 正面写真の x。bake が使う（uv_of の逆）。"""
        tx, ts = self._arc(np.clip(jf, 0, ROWS))
        return np.interp(np.clip(s, 0.0, 1.0), ts, tx)

    def hair_at(self, px, py):
        h, w = self.hair_blur.shape
        yi = np.clip(np.asarray(py), 0, h - 1).astype(np.int32)
        xi = np.clip(np.asarray(px), 0, w - 1).astype(np.int32)
        out = self.hair_blur[yi, xi]
        return out if np.ndim(out) else float(out)

    def surface_row(self, jf, px):
        """行 jf の、写真 x 座標 px（配列可）における 深さ d と XY 法線 (nx, ny)。

        法線は d(px) の傾きから。前面シート y = -d の外向き法線 ∝ (-d', -1)。
        4 枚とも水平から撮っているので、どの写真を使うかは XY 成分だけで決まる。
        """
        py = self.y1_mesh - self.bh_mesh * jf / ROWS
        cx, half_w, depth, cap, top_k = self.row(jf)
        px = np.asarray(px, dtype=np.float64)
        args = (cx, half_w, depth, cap, top_k)

        def dep(p):
            return self._depth_at(p, *args) * (1.0 + HAIR_BULGE * self.hair_at(p, py))

        d = dep(px)
        h = max(1.0, half_w * 0.02)
        lo, hi = cx - half_w, cx + half_w
        pa, pb = np.maximum(lo, px - h), np.minimum(hi, px + h)
        dd = np.where(pb - pa < 1e-6, 0.0, (dep(pb) - dep(pa)) / np.maximum(1e-9, (pb - pa) * self.scale))
        nx, ny = -dd, np.full_like(np.asarray(dd, dtype=np.float64), -1.0)
        n = np.hypot(nx, ny)
        return d, nx / n, ny / n, py, cx, half_w

    def point(self, jf, s):
        """行 jf・断面の弧長 s∈[0,1] → (x, y_depth, z, px, py, nx, ny)。ny<0 が正面向き。

        ⚠ 頂点は **px の等分ではなく弧長の等分**で置く。px 等分だと、シルエットへ向かって
        surface が急に奥へ回り込む区間に列がほとんど割かれず、実測で **34 列のうち最後の 2 列が
        テクスチャの 25% を担当**していた（＝そこだけ面が粗く、折れて見える）。
        弧長で置けば、断面のまわりで頂点間隔が均一になり、UV も u = s と一次で対応する。
        """
        px = float(self.px_of_s(jf, s))
        d, nx, ny, py, _, _ = self.surface_row(jf, px)
        x = (px - self.cxm) * self.scale
        z = (self.y1_mesh - py) * self.scale
        return x, float(d), z, px, py, float(nx), float(ny)

    def uv_to_surface(self, u, v):
        """アトラスの (u, v) → 表面点。u は前面パネルなら [0,0.5)、背面なら [0.5,1)。"""
        back = u >= 0.5
        uu = u - 0.5 if back else u
        px = self.x0 + (uu - HALF_TEXEL_U) / (0.5 - 2.0 * HALF_TEXEL_U) * self.bw
        py = self.y1 - (v - BODY_V0) / (BODY_VS - HALF_TEXEL_V) * self.bh
        jf = (self.y1_mesh - py) * ROWS / self.bh_mesh
        cx, half_w, _, _, _ = self.row(np.clip(jf, 0, ROWS))
        xl, xr = cx - half_w, cx + half_w
        uf = (px - xl) / max(1e-6, xr - xl)
        return jf, uf, back, (0.0 <= uf <= 1.0 and 0.0 <= jf <= ROWS)


def photo_map(name):
    """写真 `name` の、高さ t ごとの 左端 / 右端（画素）。位置合わせに使う。

    profile.json は span（人形の全高）で正規化した値を持つので、画素へ戻す。
    **写真ごとに実測した輪郭へ合わせる**のが要点 — bbox 全体を引き伸ばす旧方式では、
    実測で頭の幅が front と back で 25〜28% 食い違っていた（＝二重像の原因）。
    """
    d = json.load(open(os.path.join(HERE, "profile.json"), encoding="utf-8"))[name]
    span, xs0 = d["span"], d["x0"]
    t = np.array([r["t"] for r in d["rows"]])
    L = np.array([xs0 + r.get("left", 0.0) * span for r in d["rows"]])
    R = np.array([xs0 + r.get("right", 0.0) * span for r in d["rows"]])
    # 端の行は輪郭が痩せて暴れるので軽く均す
    k = np.ones(3) / 3.0
    L = np.convolve(np.pad(L, 1, mode="edge"), k, mode="valid")
    R = np.convolve(np.pad(R, 1, mode="edge"), k, mode="valid")
    return dict(t=t, L=L, R=R, y0=d["y0"], span=span, W=d["W"], H=d["H"])
