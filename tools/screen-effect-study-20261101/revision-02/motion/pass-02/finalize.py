from pathlib import Path
import hashlib,json,shutil,sys
from PIL import Image,ImageDraw
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-02')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda name:json.loads((OUT/name).read_text(encoding='utf-8'))
v=load('verification.json');assert all(v['checks'].values()) and not v['errors'] and v['videoRendered']
probe=load('ffprobe.json');assert len(probe['streams'])==1;s=probe['streams'][0]
assert (s['codec_name'],s['width'],s['height'],s['nb_frames'],s['avg_frame_rate'])==('h264',1170,540,'469','30/1')
for name,digest in v['sourceHashes'].items():assert sha(H.parent.parent/'spatial'/name)==digest
frozen=load('pass-01-freeze.json')
for row in frozen:assert sha(Path(row['path']))==row['sha256'],row['path']
bundle=load('bundle-manifest.json');assert sha(H/'smoke-fields.js')==bundle['smokeSourceSHA256']
for name in ['motion-revision.js','effects.js','preview.template.html']:assert sha(H/name)==sha(H.parent/name)
for row in bundle['frozenSources']+[bundle['referenceVideo'],bundle['oldVideo']]:assert sha(Path(row['path']))==row['sha256']
comparison=load('reference-comparison-manifest.json');assert len(comparison['entries'])==21
for e in comparison['entries']:
    for k in ['reference','old','revision']:assert sha(Path(e[k]['path']))==e[k]['sha256']
    assert e['revision']['actualTime']==e['revision']['frameIndex']/30
measure=load('smoke-measurements.json')
for r in measure['rows']:
    if 3.6<=r['time']<=4.2:assert r['new']['left35AlphaAreaPixels']==0
stills=sorted((OUT/'stills').glob('*.png'));assert len(stills)==47,len(stills)
same=OUT/'pass-comparison';same.mkdir(exist_ok=True)
for t in [3.2,3.6,4,4.1,4.2,4.4,4.7,5,12.4,12.6,13.6]:
    p1=OUT.parent/'stills'/f'frame-{t:.3f}.png';p2=OUT/'stills'/p1.name
    assert p1.is_file() and p2.is_file()
    a=Image.open(p1).convert('RGB');b=Image.open(p2).convert('RGB');assert a.size==b.size==(1170,540)
    sheet=Image.new('RGB',(2340,570),(244,244,244));sheet.paste(a,(0,30));sheet.paste(b,(1170,30));d=ImageDraw.Draw(sheet)
    d.text((8,8),f'pass01 | {p1.name} | t={t:.3f}s | native1170x540',fill='black');d.text((1178,8),f'pass02 | {p2.name} | t={t:.3f}s | native1170x540',fill='black');sheet.save(same/f'native-{t:.3f}.png')
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
record={'status':'complete','pass':'02','sources':sources,'artifacts':artifacts,'counts':{'stills':47,'stage1NativeTimes':13,'fieldProofTimes':13,'referenceNativeSheets':21,'modeNativeSheets':2,'pass01NativeSheets':11,'allPNGsDecoded':len(pngs),'pass01FrozenFiles':len(frozen)},'api':'motionRevision.renderAt(t,{mode,invasion,effects,strength,diagnostic}); motionRevision.canvas; identical contract to pass01','video':{'codec':'h264','size':[1170,540],'fps':30,'frames':469,'duration':float(probe['format']['duration']),'audio':False},'onlyEffectChange':'smoke-fields.js; compositing, effects, display, scene inputs and spatial source unchanged','changes':['Right-bottom fleck then right lower edge then central lower opening; x<.35 new visibility zero through4.3.','Full soft front width3..9px with advected distance perturbation and fine granular edges.','Broad lower ending support separate from transmission and fragment threshold; no narrow remaining corridor.'],'visualInspection':{'stage1StillsNative':13,'neutralBlackFlatsNative':26,'actualBNewContribution':True,'referenceNativeRevisited':[3.6,3.8,4,4.1,4.2,12.4,12.6]},'limits':['Same B photograph at two deformation times compares exit order; source room content is not reproduced.','Independent fixed display approximates CRT; estimated chair/shelf depth and pinned12px boundaries constrain spatial movement; small uncovered holes remain.','Timing order is constrained, but exact reference silhouette and advection are not recovered. Upper old aperture remains more rectangular than reference.','12.6s actual B contribution is below RGB20 threshold though fine low-level transmission remains; reference retains two brighter lower remnants.'],'videoSHA256':sha(OUT/'motion-revision.mp4'),'htmlSHA256':sha(OUT/'preview.html'),'smokeSHA256':sha(H/'smoke-fields.js'),'commit':'none'}
tmp=OUT/'delivery-manifest.json.tmp';tmp.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');json.loads(tmp.read_text(encoding='utf-8'));tmp.replace(OUT/'delivery-manifest.json')
print(json.dumps({k:record[k] for k in ['counts','video','videoSHA256','htmlSHA256','smokeSHA256']},ensure_ascii=False))
