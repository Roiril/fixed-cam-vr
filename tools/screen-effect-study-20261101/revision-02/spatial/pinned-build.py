from pathlib import Path
import json,hashlib,shutil
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/pinned')
O.mkdir(parents=True,exist_ok=True)
source=(H/'renderer.js').read_text(encoding='utf-8')
def replace(a,b):
 global source
 assert a in source,a
 source=source.replace(a,b)
replace("import {assets} from './assets.js';","import {assets} from './assets.js';\nimport {makePinnedGeometry} from './pinned-field.js';")
replace('in vec3 position;in float surface;in vec3 bary;','in vec3 position;in float surface;in vec3 bary;in float pinWeight;out float weight;')
replace('vec3 q=camera(position+strength*F(position));','weight=pinWeight;vec3 q=camera(position+strength*pinWeight*F(position));')
replace('in vec3 originalCamera;in vec3 edge;flat in int sid;in float depth;','in vec3 originalCamera;in vec3 edge;flat in int sid;in float depth;in float weight;')
replace('if(diagnostic==4)rgb=vec3(.02);','if(diagnostic==4)rgb=vec3(.02);if(diagnostic==5)rgb=vec3(weight);')
replace('geometry=makeGeometry(assets.camera,assets.room)','geometry=makePinnedGeometry(assets.camera,assets.room)')
replace(" async function image(url)"," const pinBuffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,pinBuffer);gl.bufferData(gl.ARRAY_BUFFER,geometry.vertexWeights,gl.STATIC_DRAW);const pinLoc=gl.getAttribLocation(p,'pinWeight');gl.enableVertexAttribArray(pinLoc);gl.vertexAttribPointer(pinLoc,1,gl.FLOAT,false,0,0);\n async function image(url)")
replace("['beauty','surface','depth','wire','holes'].indexOf(diagnostic)","['beauty','surface','depth','wire','holes','weight'].indexOf(diagnostic)")
replace('displace:(p,t,s=1)=>add(p,field(p,t,s))','displace:(p,t,s=1)=>add(p,field(p,t,s*geometry.weightWorld(p)))')
(H/'renderer-pinned.js').write_bytes(source.encode('utf-8'))
parts=[]
for f in ['assets.js','geometry.js','pinned-field.js','renderer-pinned.js','preview.js']:
 s=(H/f).read_text(encoding='utf-8');s='\n'.join(l for l in s.splitlines() if not l.startswith('import '));s=s.replace('export async function','async function').replace('export function','function').replace('export const','const');parts.append(s)
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*BUNDLE*/','\n'.join(parts)).replace('床と仕切りが曲がる','床と仕切りが曲がる・輪郭を固定').replace('<option value="holes">','<option value="weight">変位の強さ（白1／黒0）</option><option value="holes">').replace('輪郭の一致には限界があります。','輪郭の一致には限界があります。この版は元写真の面境界と外周から12px以内で世界空間の変位をゼロへ戻します。推定物体の輪郭が固定される制約があります。既知の床と仕切りの接合もこの版では拘束しています。')
(H/'spatial-pinned-preview.html').write_bytes(html.encode('utf-8'));(O/'spatial-pinned-preview.html').write_bytes(html.encode('utf-8'))
manifest={'variant':'boundary-pinned-12px','rule':'original ownership boundaries + perimeter → smoothstep(0,12,distance) × world XYZ field; silhouette fixed, all ID boundaries constrained','sourceGeometryIsMeasured':False,'postRenderHoleFill':False,'rendererSha256':hashlib.sha256((H/'renderer-pinned.js').read_bytes()).hexdigest(),'htmlSha256':hashlib.sha256(html.encode()).hexdigest(),'unchangedSources':{f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','geometry.js','assets.js']}}
(O/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps(manifest))
