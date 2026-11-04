from pathlib import Path
import base64,hashlib,json,shutil,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent;SP=H.parent.parent/'spatial'
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');OUT=ROOT/'revision-02/motion/pass-02';OLD=ROOT/'motion/round-07'
OUT.mkdir(parents=True,exist_ok=True);(OUT/'assets').mkdir(exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
expected={'renderer.js':'09df22ccb81c7316a422228d1f34f0529559e35ea9e700e9cde409a01441be1d','renderer-pinned.js':'d5244da75048a25b588d4f5e201366320fb7ed6562970050a2ae19f391e68c22','pinned-field.js':'697fb910f860d1696c36106061f8ad29320b7cd79cbe6a45620b3ef177131dc3','assets.js':'2104bc1ccca252e279c3fcd8da4546acd74566715703e3bff1711a080e478345','geometry.js':'79f17ef2bf3018a9292d8e7f109b872a78de5725366dbbd6ded9da6967aba990'}
for f,digest in expected.items():assert sha(SP/f)==digest,(f,sha(SP/f))
release=json.loads((OLD/'release-manifest.json').read_text(encoding='utf-8'));frozen=[]
for section,folder in [('sourceFiles',OLD/'source'),('effectAssets',OLD/'assets')]:
    for item in release[section]:
        p=folder/item['name'];assert p.exists(),p;assert sha(p)==item['sha256'],p;frozen.append({'path':str(p),'sha256':item['sha256']})
video=OLD/'alternate-b/motion-prototype.mp4';assert sha(video)=='fc99f590045d482b99e5408ed4b6fe7596129199a26984e751d8b32fdd8b5601'
source=ROOT/'sources/screen-20261101-004528.mp4';assert sha(source)=='2c6fd4039eedfbe4d8fd13fe8dc5b818ce355ba6a722accb285c4410f67e4fa8'
m=json.loads((OLD/'assets/effect-assets.json').read_text(encoding='utf-8'));keep=['foil','glyph','white','whiteMotion','redMotion'];m['sequences']={k:m['sequences'][k] for k in keep};m['revisionNote']='Frozen foreground only. No invasion-matte or edge-granules are loaded. Old RGB backgrounds are excluded.'
names=sorted(set(s['file'] for s in m['sequences'].values()));baked={'manifest':m,'images':{}}
for n in names:
    shutil.copyfile(OLD/'assets'/n,OUT/'assets'/n);assert sha(OUT/'assets'/n)==sha(OLD/'assets'/n)
    baked['images'][n]='data:image/png;base64,'+base64.b64encode((OLD/'assets'/n).read_bytes()).decode()
def strip(s):
    s='\n'.join(l for l in s.splitlines() if not l.startswith('import '))
    return s.replace('export async function','async function').replace('export function','function').replace('export const','const')
spatial='\n'.join(strip((SP/f).read_text(encoding='utf-8')) for f in ['assets.js','geometry.js','pinned-field.js','renderer-pinned.js'])
script='const createSpatialScene=(()=>{'+spatial+'\nreturn createSpatialScene;})();\n'
script+='const drawSmokeAt=(()=>{'+strip((H/'smoke-fields.js').read_text(encoding='utf-8'))+'\nreturn drawSmokeAt;})();\n'
script+=strip((H/'effects.js').read_text(encoding='utf-8'))+'\n'
script+='globalThis.__frozenForeground='+json.dumps(baked,separators=(',',':'))+';\n'
script+=strip((H/'motion-revision.js').read_text(encoding='utf-8'))
css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8')
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*REPORT_CSS*/',css).replace('/*BUNDLE*/',script)
html=html.replace('href="../spatial/spatial-preview.html"','href="../../spatial/spatial-preview.html"')
assert '/*BUNDLE*/' not in html and '/*REPORT_CSS*/' not in html
for p in [H/'preview.html',OUT/'preview.html']:
    tmp=p.with_suffix('.html.tmp');tmp.write_text(html,encoding='utf-8');tmp.replace(p)
for name in ['motion-revision.js','effects.js','preview.template.html']:assert sha(H/name)==sha(H.parent/name),name
record={'frozenSources':frozen,'referenceVideo':{'path':str(source),'sha256':sha(source)},'oldVideo':{'path':str(video),'sha256':sha(video)},'spatial':expected,'smokeSourceSHA256':sha(H/'smoke-fields.js'),'htmlSHA256':sha(OUT/'preview.html'),'htmlBytes':(OUT/'preview.html').stat().st_size,'retainedForeground':names,'oldInvasionLoaded':False,'originalSceneRGBReplayed':False,'pass':'02','onlyEffectChange':'smoke-fields.js; compositor, display, scene input and frozen foreground byte-identical to pass01'}
(OUT/'bundle-manifest.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'htmlBytes':record['htmlBytes'],'htmlSHA256':record['htmlSHA256'],'frozenChecks':len(frozen),'retainedForeground':names}))
