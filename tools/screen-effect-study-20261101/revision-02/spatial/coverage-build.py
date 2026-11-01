from pathlib import Path
import hashlib,json
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/coverage')
O.mkdir(parents=True,exist_ok=True)
s=(H/'renderer-superposed.js').read_text(encoding='utf-8')
def repl(a,b):
 global s
 assert a in s,a
 s=s.replace(a,b)
repl("import {assets} from './assets.js';","import {assets} from './assets.js';\nimport {continuousOwnershipGLSL} from './coverage-ownership.js';")
repl('out vec4 color;\nvec3 palette','out vec4 color;\n${continuousOwnershipGLSL(assets.room)}\nvec3 palette')
repl('int own=int(texture(owner,uv).r*255.+.5);','int own=continuousOwner(uv);')
repl('if(own!=sid)discard;','bool inSourceMask=sid==9?inObject9(uv*vec2(640,480)):(sid==10?inObject10(uv*vec2(640,480)):true);if((originalCamera.z>sourceNearest+.00001||!inSourceMask)&&!sourceBoundaryTie(uv))discard;')
(H/'renderer-coverage.js').write_bytes(s.encode('utf-8'))
parts=[]
for f in ['assets.js','geometry.js','pinned-field.js','superposed-motion.js','coverage-ownership.js','renderer-coverage.js','preview.js']:
 v=(H/f).read_text(encoding='utf-8');v='\n'.join(l for l in v.splitlines() if not l.startswith('import '));v=v.replace('export async function','async function').replace('export function','function').replace('export const','const')
 if f=='preview.js':v=v.replace("mode:'local'","mode:'combined'")
 parts.append(v)
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*BUNDLE*/','\n'.join(parts)).replace('<option value="local">','<option value="combined">参照の運動＋空間の変形</option><option value="local">').replace('床と仕切りが曲がる','連続した面境界で描く').replace('カメラは固定です。','カメラは固定です。worldの局所変形を投影した後へ、参照の計測2D相似を重ねています。面の所有を元UVの連続レイ交差で判定します。').replace('輪郭の一致には限界があります。','輪郭の一致には限界があります。元面境界12pxの拘束は維持しています。純粋な3D復元ではありません。表示ONの全4条件に同じ1.35viewport cropを適用し、glassと走査線は固定です。画素の穴埋めや後段ぼかしはしません。')
(H/'coverage-preview.html').write_bytes(html.encode('utf-8'));(O/'coverage-preview.html').write_bytes(html.encode('utf-8'))
manifest={'variant':'continuous-source-ray-ownership','changes':'nearest pixel ownership replaced by continuous geometric ray ownership; all world field/motion/geometry unchanged','postRenderHoleFill':False,'rendererSha256':hashlib.sha256((H/'renderer-coverage.js').read_bytes()).hexdigest(),'helperSha256':hashlib.sha256((H/'coverage-ownership.js').read_bytes()).hexdigest(),'htmlSha256':hashlib.sha256(html.encode()).hexdigest(),'unchangedSources':{f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','renderer-pinned.js','renderer-combined.js','renderer-superposed.js','combined-proxy.js','superposed-motion.js','geometry.js','assets.js','pinned-field.js']}}
(O/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8');print(json.dumps(manifest))
