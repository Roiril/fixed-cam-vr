from pathlib import Path
import json,hashlib
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/combined')
O.mkdir(parents=True,exist_ok=True)
s=(H/'renderer-pinned.js').read_text(encoding='utf-8')
def repl(a,b):
 global s
 assert a in s,a
 s=s.replace(a,b)
repl("import {assets} from './assets.js';","import {assets} from './assets.js';\nimport {referenceMotion,combinedProject,globalPixel} from './combined-proxy.js';")
repl('uniform float time,strength;','uniform float time,strength;uniform vec4 rigidMotion;uniform bool combined;')
repl('vec3 q=camera(position+strength*pinWeight*F(position));','vec3 q=camera(position+strength*pinWeight*F(position));if(combined){float a=rigidMotion.x,sc=rigidMotion.y,cs=cos(a),sn=sin(a),zr=2./sc;vec3 qw=vec3(cs*q.x+sn*q.y+rigidMotion.z*640./lens.x*zr,-sn*q.x+cs*q.y-rigidMotion.w*480./lens.y*zr,q.z+2.*(1./sc-1.));vec2 uv=vec2(lens.x*originalCamera.x/originalCamera.z+320.,240.-lens.y*originalCamera.y/originalCamera.z);vec2 d=uv-vec2(320,240);vec2 ug=vec2(320,240)+rigidMotion.zw*vec2(640,480)+sc*vec2(cs*d.x-sn*d.y,sn*d.x+cs*d.y);vec3 qflat=vec3((ug.x-320.)/lens.x*qw.z,(240.-ug.y)/lens.y*qw.z,qw.z);q=mix(qflat,qw,pinWeight);}')
repl("'photo','owner','diagnostic'","'photo','owner','diagnostic','rigidMotion','combined'")
repl("mode='local'","mode='combined'")
repl("['local','global','none'].includes(mode)","['local','global','none','combined'].includes(mode)")
repl("mode==='local'?strength:0","(mode==='local'||mode==='combined')?strength:0")
repl('gl.uniform1f(loc.time,t);','gl.uniform1f(loc.time,t);gl.uniform4f(loc.rigidMotion,...referenceMotion(t,strength));gl.uniform1i(loc.combined,+(mode===\'combined\'&&strength!==0));')
repl('vec2 q=vec2(uv.x,1.-uv.y);vec4 s=texture(scene,uv);','vec2 rawUV=display==1?(uv-.5)/1.35+.5:uv;vec2 q=vec2(rawUV.x,1.-rawUV.y);vec4 s=texture(scene,rawUV);')
repl('if(display==1){float a=texture(aperture,q).r;','if(display==1){q=vec2(uv.x,1.-uv.y);float a=texture(aperture,q).r;')
repl("mode==='global'?1:0","mode==='global'&&strength!==0?1:0")
repl('project:p=>project(p,c),','project:p=>project(p,c),combinedProject:(p,t,s=1)=>combinedProject(p,t,s,geometry,c),globalProject:(p,t,s=1)=>globalPixel(project(p,c),referenceMotion(t,s)),')
(H/'renderer-combined.js').write_bytes(s.encode('utf-8'))
parts=[]
for f in ['assets.js','geometry.js','pinned-field.js','combined-proxy.js','renderer-combined.js','preview.js']:
 v=(H/f).read_text(encoding='utf-8');v='\n'.join(l for l in v.splitlines() if not l.startswith('import '));v=v.replace('export async function','async function').replace('export function','function').replace('export const','const')
 if f=='preview.js':v=v.replace("mode:'local'","mode:'combined'")
 parts.append(v)
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*BUNDLE*/','\n'.join(parts)).replace('<option value="local">','<option value="combined">参照の運動＋空間の変形</option><option value="local">').replace('床と仕切りが曲がる','参照の運動と空間の変形').replace('カメラは固定です。','元カメラは固定です。参照の2D相似運動を基準奥行2mの代理roll・移動へ変え、面の奥行差を加えています。').replace('輪郭の一致には限界があります。','輪郭の一致には限界があります。元UV境界ではglobal相似へ戻す投影依存拘束を使います。純粋な3D再投影ではありません。固定表示ONでは全条件へ同じ1.35倍のviewport cropを適用します。画素の貼戻しや穴埋めはしません。')
(H/'combined-preview.html').write_bytes(html.encode('utf-8'));(O/'combined-preview.html').write_bytes(html.encode('utf-8'))
manifest={'variant':'combined-proxy-depth-plus-pinned-field','zRefMeters':2,'displayViewportCrop':1.35,'cameraChanged':False,'pure3D':False,'boundaryConstraint':'mix inverse-projected global UV at qworld.z with qworld, pinweight','postRenderHoleFill':False,'motionTrackIsMeasured2DSimilarity':True,'rendererSha256':hashlib.sha256((H/'renderer-combined.js').read_bytes()).hexdigest(),'htmlSha256':hashlib.sha256(html.encode()).hexdigest(),'unchangedSources':{f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','renderer-pinned.js','geometry.js','assets.js','pinned-field.js']}}
(O/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8');print(json.dumps(manifest))
