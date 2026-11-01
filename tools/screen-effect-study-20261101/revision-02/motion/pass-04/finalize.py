from pathlib import Path
import hashlib,json,shutil,sys
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-04')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda name:json.loads((OUT/name).read_text(encoding='utf-8'))
v=load('verification.json');assert all(v['checks'].values()) and not v['errors'] and v['videoRendered']
probe=load('ffprobe.json');assert len(probe['streams'])==1;s=probe['streams'][0]
assert (s['codec_name'],s['width'],s['height'],s['nb_frames'],s['avg_frame_rate'])==('h264',1170,540,'469','30/1')
for name,digest in v['sourceHashes'].items():assert sha(H.parent.parent/'spatial'/name)==digest
frozen=load('prior-passes-freeze.json')
for row in frozen:assert sha(Path(row['path']))==row['sha256'],row['path']
previous=json.loads((OUT.parent/'pass-02/pass-01-freeze.json').read_text(encoding='utf-8'))
for row in previous:assert sha(Path(row['path']))==row['sha256'],row['path']
bundle=load('bundle-manifest.json');assert sha(H/'smoke-fields.js')==bundle['smokeSourceSHA256']
for name in ['smoke-fields.js','effects.js']:assert sha(H/name)==sha(H.parent/'pass-02'/name)
for p in (OUT/'field-proof').glob('*.rgba'):assert sha(p)==sha(OUT.parent/'pass-02/field-proof'/p.name),p.name
for row in bundle['frozenSources']+[bundle['referenceVideo'],bundle['oldVideo']]:assert sha(Path(row['path']))==row['sha256']
comparison=load('reference-comparison-manifest.json');assert len(comparison['entries'])==21
for e in comparison['entries']:
    for k in ['reference','old','revision']:assert sha(Path(e[k]['path']))==e[k]['sha256']
    assert e['revision']['actualTime']==e['revision']['frameIndex']/30
stills=sorted((OUT/'stills').glob('*.png'));assert len(stills)==49,len(stills)
source=OUT/'source';source.mkdir(exist_ok=True);sources=[]
for p in sorted(H.iterdir()):
    if p.is_file() and p.name!='preview.html':
        q=source/p.name;shutil.copyfile(p,q);assert sha(q)==sha(p);sources.append({'path':str(p),'snapshot':str(q),'sha256':sha(p),'bytes':p.stat().st_size})
artifacts=[];pngs=[]
for p in sorted(OUT.rglob('*')):
    if p.is_file() and 'source' not in p.relative_to(OUT).parts and p.suffix in ['.png','.mp4','.html','.json','.md'] and p.name!='delivery-manifest.json':
        if p.suffix=='.png':
            with Image.open(p) as im:im.verify()
            pngs.append(p)
        artifacts.append({'path':str(p),'sha256':sha(p),'bytes':p.stat().st_size})
record={'status':'complete','pass':'04','sources':sources,'artifacts':artifacts,'counts':{'stills':49,'stage1NativeTimes':13,'fieldProofTimes':13,'referenceNativeSheets':21,'fourModeNativeSheets':2,'allPNGsDecoded':len(pngs),'priorPassesFrozenFiles':len(frozen),'pass01FrozenFiles':len(previous)},'api':'motionRevision.renderAt(t,{mode:"combined"|"local"|"global"|"none",invasion,effects,strength,diagnostic}); motionRevision.canvas','video':{'codec':'h264','size':[1170,540],'fps':30,'frames':469,'duration':float(probe['format']['duration']),'audio':False},'onlyEffectChange':'Spatial superposed renderer; smoke-fields/effects byte-identical to pass02; same-B t/t+1.7 compositing and timing retained','display':'Every display=true mode shares 1.35 crop and fixed independent display pass; raw geometry calibration is a separate display=false result. All-off baseline uses the same new crop, not pass02 pixel identity.','limits':['Same B photograph at two deformation times compares exit order; source room content is not reproduced.','Measured global2D similarity is applied after calibrated local world deformation. Estimated shapes and12px boundary constraints remain; this is not pure3D reconstruction.','Independent display approximates CRT; glass and scanlines stay fixed.','Smoke exact reference silhouette/advection remain approximate; 3.6 fleck is small and12.6 transmission is dimmer than reference.'],'videoSHA256':sha(OUT/'motion-revision.mp4'),'htmlSHA256':sha(OUT/'preview.html'),'smokeSHA256':sha(H/'smoke-fields.js'),'commit':'none'}
record['limits'].append('The depth translation/rotation proxy and qflat blend from rejected pass03 are absent. Small unobserved gaps may remain because no RGB hole filling is used.')
record['visualInspection']={'mainNativeStills':20,'mobileUI':True,'largeGlobalMotionAndInternalCurves':'Observed together at6.5/8.5/10.5; actual8.5/10.5 checked by parent before469frame export','sameSmokeRawFieldsByteEqualPass02':104}
tmp=OUT/'delivery-manifest.json.tmp';tmp.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');json.loads(tmp.read_text(encoding='utf-8'));tmp.replace(OUT/'delivery-manifest.json')
print(json.dumps({k:record[k] for k in ['counts','video','videoSHA256','htmlSHA256','smokeSHA256']},ensure_ascii=False))
