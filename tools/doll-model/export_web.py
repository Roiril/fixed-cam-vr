# 人形を**卓（ブラウザ）で描くための最小のメッシュ**として書き出す。
#
#   blender --background --python export_web.py -- <dir>
#   → tools/web-compositor/assets/doll/doll_web.bin / doll_web.json / doll_albedo.png
#
# なぜ glTF ではないか: 卓に glTF パーサを持ち込むと、スキニング・マテリアル・
# アクセサの分岐まで面倒を見ることになる。卓が要るのは**静止ポーズの 1 メッシュ**だけ
# （ハンドトラッキングの入力が無いので腕は動かない）。位置・法線・UV・三角形の 4 本の配列で足りる。
#
# ⚠ これは**幾何の目安**であって、合成の見た目の正は Unity（Preview Show Composite）。
#   卓は陰影を写実へ寄せない（rules/streaming.md）。
import bpy, bmesh, json, os, sys, struct, shutil

OUT = sys.argv[sys.argv.index("--") + 1]
WEB = os.path.abspath(os.path.join(OUT, "..", "web-compositor", "assets", "doll"))
os.makedirs(WEB, exist_ok=True)

bpy.ops.wm.open_mainfile(filepath=os.path.join(OUT, "doll.blend"))
obj = bpy.data.objects["Ichimatsu"]

# 評価済みメッシュ（モディファイア適用後）を三角形化して取り出す。
dg = bpy.context.evaluated_depsgraph_get()
me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
me.transform(obj.matrix_world)
bm = bmesh.new()
bm.from_mesh(me)
bmesh.ops.triangulate(bm, faces=bm.faces[:])
bm.to_mesh(me)
bm.free()
# ⚠ Blender 4.1 で `calc_normals_split()` は削除された（分割法線は自動で用意される）。
#    残っていると AttributeError で落ちる。三角形の一覧だけ作れば `loop.normal` は読める。
me.calc_loop_triangles()

uv_layer = me.uv_layers.active.data if me.uv_layers.active else None

# ⚠ ループ単位で展開する（頂点を共有すると UV の継ぎ目で絵がねじれる）。
#    殻は u を 0/0.5/1 の境界で折り返しているので、ここを共有すると顔と後頭部が混ざる。
pos, nrm, uvs, idx = [], [], [], []
for tri in me.loop_triangles:
    for li in tri.loops:
        lo = me.loops[li]
        v = me.vertices[lo.vertex_index]
        # Blender は Z-up・Unity/卓の course 空間は Y-up。ここで一度だけ変換する。
        pos.extend((v.co.x, v.co.z, v.co.y))
        n = lo.normal
        nrm.extend((n.x, n.z, n.y))
        uv = uv_layer[li].uv if uv_layer else (0.0, 0.0)
        uvs.extend((uv[0], 1.0 - uv[1]))     # 画像は上から数えるので v を反転
        idx.append(len(idx))

n_vert = len(idx)
buf = bytearray()
buf += struct.pack(f"<{len(pos)}f", *pos)
buf += struct.pack(f"<{len(nrm)}f", *nrm)
buf += struct.pack(f"<{len(uvs)}f", *uvs)

xs = pos[0::3]; ys = pos[1::3]; zs = pos[2::3]
meta = dict(
    vertexCount=n_vert,
    offsets=dict(position=0, normal=len(pos) * 4, uv=(len(pos) + len(nrm)) * 4),
    bounds=dict(min=[min(xs), min(ys), min(zs)], max=[max(xs), max(ys), max(zs)]),
    heightM=max(ys) - min(ys),
    albedo="doll_albedo.png",
)
open(os.path.join(WEB, "doll_web.bin"), "wb").write(bytes(buf))
with open(os.path.join(WEB, "doll_web.json"), "w", encoding="utf-8") as f:
    json.dump(meta, f, ensure_ascii=False, indent=1)

# アルベドは**半分に縮めて**置く。卓では人形が 100 画素前後にしか写らないので、
# 1024 幅を配ってもテクセルは一度も使われない（リポジトリと読み込みが重くなるだけ）。
# 実機へ行くのは Assets/Art/Models/Doll/ の原寸の方で、こちらは卓のプレビュー専用。
# ⚠ **Blender 同梱の Python に cv2 は無い**（人形パイプラインの他の段はシステム側の
#    Python で走っているので入っている。ここだけ環境が違う）。Blender 自身の画像機能で縮める。
try:
    img = bpy.data.images.load(os.path.join(OUT, "doll_albedo.png"))
    img.scale(img.size[0] // 2, img.size[1] // 2)
    img.filepath_raw = os.path.join(WEB, "doll_albedo.png")
    img.file_format = 'PNG'
    img.save()
    print(f"[web] albedo {img.size[0]}x{img.size[1]}")
except Exception as e:
    print("[web] 縮小に失敗したので原寸を置く:", e)
    shutil.copy(os.path.join(OUT, "doll_albedo.png"), os.path.join(WEB, "doll_albedo.png"))

print(f"[web] verts={n_vert} bin={len(buf)}B height={meta['heightM']:.3f}m -> {WEB}")
