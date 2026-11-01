from pathlib import Path
import json,hashlib
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/superposed')
O.mkdir(parents=True,exist_ok=True)
old=(H/'combined-proxy.js').read_text(encoding='utf-8')
motion="import {assets} from './assets.js';\n"+old[old.index('export function referenceMotion'):old.index('export function proxyCamera')]
motion+="export function superposedProject(p,t,strength,geometry,c,project,add,field){return globalPixel(project(add(p,field(p,t,strength*geometry.weightWorld(p))),c),referenceMotion(t,strength));}\n"
(H/'superposed-motion.js').write_bytes(motion.encode('utf-8'))
s=(H/'renderer-pinned.js').read_text(encoding='utf-8')
def repl(a,b):
 global s
 assert a in s,a
 s=s.replace(a,b)
repl("import {assets} from './assets.js';","import {assets} from './assets.js';\nimport {referenceMotion,globalPixel,superposedProject} from './superposed-motion.js';")
repl('uniform float time,strength;','uniform float time,strength;uniform vec4 measuredMotion;uniform bool wholeMotion;')
key='q.z);}`;'
repl(key,"q.z);if(wholeMotion){vec2 px=vec2((gl_Position.x/gl_Position.w+1.)*320.,(1.-gl_Position.y/gl_Position.w)*240.);vec2 d=px-vec2(320,240);float a=measuredMotion.x,cs=cos(a),sn=sin(a);vec2 outpx=vec2(320,240)+measuredMotion.zw*vec2(640,480)+measuredMotion.y*vec2(cs*d.x-sn*d.y,sn*d.x+cs*d.y);gl_Position.xy=vec2(outpx.x/320.-1.,1.-outpx.y/240.)*gl_Position.w;}}`;")
repl("'photo','owner','diagnostic'","'photo','owner','diagnostic','measuredMotion','wholeMotion'")
repl("mode='local'","mode='combined'")
repl("['local','global','none'].includes(mode)","['local','global','none','combined'].includes(mode)")
repl("mode==='local'?strength:0","(mode==='local'||mode==='combined')?strength:0")
repl('gl.uniform1f(loc.time,t);','gl.uniform1f(loc.time,t);gl.uniform4f(loc.measuredMotion,...referenceMotion(t,strength));gl.uniform1i(loc.wholeMotion,+((mode===\'combined\'||mode===\'global\')&&strength!==0));')
repl('vec2 q=vec2(uv.x,1.-uv.y);vec4 s=texture(scene,uv);','vec2 rawUV=display==1?(uv-.5)/1.35+.5:uv;vec2 q=vec2(rawUV.x,1.-rawUV.y);vec4 s=texture(scene,rawUV);')
repl('if(display==1){float a=texture(aperture,q).r;','if(display==1){q=vec2(uv.x,1.-uv.y);float a=texture(aperture,q).r;')
repl("gl.uniform1i(displayLoc.mode,mode==='global'?1:0)","gl.uniform1i(displayLoc.mode,0)")
repl('project:p=>project(p,c),','project:p=>project(p,c),superposedProject:(p,t,s=1)=>superposedProject(p,t,s,geometry,c,project,add,field),globalProject:(p,t,s=1)=>globalPixel(project(p,c),referenceMotion(t,s)),')
(H/'renderer-superposed.js').write_bytes(s.encode('utf-8'))
parts=[]
for f in ['assets.js','geometry.js','pinned-field.js','superposed-motion.js','renderer-superposed.js','preview.js']:
 v=(H/f).read_text(encoding='utf-8');v='\n'.join(l for l in v.splitlines() if not l.startswith('import '));v=v.replace('export async function','async function').replace('export function','function').replace('export const','const')
 if f=='preview.js':v=v.replace("mode:'local'","mode:'combined'")
 parts.append(v)
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*BUNDLE*/','\n'.join(parts)).replace('<option value="local">','<option value="combined">参照の運動＋空間の変形</option><option value="local">').replace('床と仕切りが曲がる','参照の2D運動を局所変形へ重ねる').replace('カメラは固定です。','カメラは固定です。世界空間の局所変形を再投影した後へ、参照から計測した2D相似運動を重ねています。').replace('輪郭の一致には限界があります。','輪郭の一致には限界があります。元の面境界から12pxで局所変形を抑制する投影依存の拘束があります。推定した3Dカメラ移動は使いません。純粋な3D復元ではありません。固定表示ONでは全条件へ同じ1.35倍のviewport cropを適用します。画素の貼戻しや穴埋めはしません。')
(H/'superposed-preview.html').write_bytes(html.encode('utf-8'));(O/'superposed-preview.html').write_bytes(html.encode('utf-8'))
manifest={'variant':'world-local-field-plus-measured-2D-similarity','proxyRigidTransform':False,'qflatQworldBlend':False,'globalAppliedToProjectedClipXY':True,'clipZAndWKeptFromLocalMesh':True,'displayViewportCrop':1.35,'cameraChanged':False,'pure3D':False,'postRenderHoleFill':False,'rendererSha256':hashlib.sha256((H/'renderer-superposed.js').read_bytes()).hexdigest(),'helperSha256':hashlib.sha256((H/'superposed-motion.js').read_bytes()).hexdigest(),'htmlSha256':hashlib.sha256(html.encode()).hexdigest(),'unchangedSources':{f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','renderer-pinned.js','renderer-combined.js','combined-proxy.js','geometry.js','assets.js','pinned-field.js']}}
(O/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8');print(json.dumps(manifest))
