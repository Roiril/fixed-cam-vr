from pathlib import Path
import base64,hashlib,json,shutil,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent;SP=H.parent.parent/'spatial'
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');OUT=ROOT/'revision-02/motion/pass-05';OLD=ROOT/'motion/round-07'
OUT.mkdir(parents=True,exist_ok=True);(OUT/'assets').mkdir(exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
expected={'renderer.js':'09df22ccb81c7316a422228d1f34f0529559e35ea9e700e9cde409a01441be1d','renderer-pinned.js':'d5244da75048a25b588d4f5e201366320fb7ed6562970050a2ae19f391e68c22','pinned-field.js':'697fb910f860d1696c36106061f8ad29320b7cd79cbe6a45620b3ef177131dc3','assets.js':'2104bc1ccca252e279c3fcd8da4546acd74566715703e3bff1711a080e478345','geometry.js':'79f17ef2bf3018a9292d8e7f109b872a78de5725366dbbd6ded9da6967aba990'}
expected.update({'renderer-combined.js':'c6ad8c42cf16f180955f30b0ce6663e6cb24b4f954ddba0f51556aa53198fa09','combined-proxy.js':'7a50f8240f2d641f4f9947a6e05c597be7828579ea8bb1741d6c22256e5dfa83'})
expected.update({'renderer-superposed.js':'09441c6e8e4c1da2e7534fbe2d4545801c805d458c2a842e454ba361cf15d1bc','superposed-motion.js':'4b4d332b1ac4442eb6c9609dc335dec7aaa4f60ec618a4cdb5c8c63620ea7390'})
expected.update({'renderer-coverage.js':'23350a7da437b3f1336033c49f58aee814615418d6a328decc5efe432eb68e6d','coverage-ownership.js':'580a614e972cda766e83170261426ecdb8874a532ac645bab56269ddd1b00ee9'})
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
spatial='\n'.join(strip((SP/f).read_text(encoding='utf-8')) for f in ['assets.js','geometry.js','pinned-field.js','superposed-motion.js','coverage-ownership.js','renderer-coverage.js'])
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
for name in ['smoke-fields.js','effects.js']:assert sha(H/name)==sha(H.parent/'pass-02'/name),name
record={'frozenSources':frozen,'referenceVideo':{'path':str(source),'sha256':sha(source)},'oldVideo':{'path':str(video),'sha256':sha(video)},'spatial':expected,'smokeSourceSHA256':sha(H/'smoke-fields.js'),'htmlSHA256':sha(OUT/'preview.html'),'htmlBytes':(OUT/'preview.html').stat().st_size,'retainedForeground':names,'oldInvasionLoaded':False,'originalSceneRGBReplayed':False,'pass':'05','onlyEffectChange':'spatial renderer superposed; smoke-fields/effects byte-identical to pass02; sameB two-state compositor and timings retained'}
(OUT/'bundle-manifest.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'htmlBytes':record['htmlBytes'],'htmlSHA256':record['htmlSHA256'],'frozenChecks':len(frozen),'retainedForeground':names}))
