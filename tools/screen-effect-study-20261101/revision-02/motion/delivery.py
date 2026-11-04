from pathlib import Path
import hashlib,json,shutil,sys
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
verification=json.loads((OUT/'verification.json').read_text(encoding='utf-8'))
assert all(verification['checks'].values()) and not verification['errors']
probe=json.loads((OUT/'ffprobe.json').read_text(encoding='utf-8'))
assert len(probe['streams'])==1
stream=probe['streams'][0]
assert (stream['codec_name'],stream['width'],stream['height'],stream['nb_frames'],stream['avg_frame_rate'])==('h264',1170,540,'469','30/1')
assert abs(float(probe['format']['duration'])-469/30)<.001
for name,digest in verification['sourceHashes'].items():assert sha(H.parent/'spatial'/name)==digest
bundle=json.loads((OUT/'bundle-manifest.json').read_text(encoding='utf-8'))
for item in bundle['frozenSources']+[bundle['referenceVideo'],bundle['oldVideo']]:assert sha(Path(item['path']))==item['sha256']
comparison=json.loads((OUT/'reference-comparison-manifest.json').read_text(encoding='utf-8'))
assert len(comparison['entries'])==21
for entry in comparison['entries']:
    for key in ['reference','old','revision']:
        item=entry[key];p=Path(item['path']);assert p.is_file() and sha(p)==item['sha256']
    assert entry['revision']['actualTime']==entry['revision']['frameIndex']/30
stills=sorted((OUT/'stills').glob('*.png'));assert len(stills)==32
images=stills+sorted((OUT/'reference-comparison').glob('*.png'))+sorted(OUT.glob('mode-comparison-*.png'))
for p in images:
    with Image.open(p) as image:image.verify()
source=OUT/'source';source.mkdir(exist_ok=True)
sources=[]
for p in sorted(H.iterdir()):
    if p.is_file() and p.name!='preview.html':
        q=source/p.name;shutil.copyfile(p,q);assert sha(q)==sha(p)
        sources.append({'path':str(p),'snapshot':str(q),'bytes':p.stat().st_size,'sha256':sha(p)})
artifacts=[]
for p in [OUT/'preview.html',OUT/'motion-revision.mp4',OUT/'verification.json',OUT/'ffprobe.json',OUT/'bundle-manifest.json',OUT/'reference-comparison-manifest.json']+images:
    artifacts.append({'path':str(p),'bytes':p.stat().st_size,'sha256':sha(p)})
record={'status':'complete','sources':sources,'artifacts':artifacts,'counts':{'stills':32,'comparisonNativeSheets':21,'comparisonVideoFrames':21,'modeNativeSheets':2,'decodedFinalImages':len(images)},'api':'await motionRevision.ready; motionRevision.renderAt(t,{mode:"local"|"global"|"none",invasion:true,effects:true,strength:1,diagnostic:"beauty"|"surface"|"depth"|"wire"|"holes"|"weight"}); motionRevision.canvas','video':{'codec':'h264','size':[1170,540],'fps':30,'frames':469,'nominalDuration':469/30,'containerDuration':float(probe['format']['duration']),'audio':False},'visualInspection':{'requestedStillsNative':18,'referenceComparisonNativeSheets':21,'modeComparisonNativeSheets':2,'smokeOffNative':2,'fieldDecompositions':'Stage B 130 PNGs decoded; black/neutral/white flats and input before-after viewed'},'limits':['Same B photograph at two deformation times compares exit order; source room content is not reproduced.','The display pass is an independent approximation, not the adopted CRT screenshot.','Estimated chair/shelf geometry and pinned 12px ownership boundaries constrain the deformation; small uncovered holes remain.','Smoke openings are rounder and smoother than the reference; onset around 3.0s and small broken edge shapes still differ.'],'immutableSourcesChecked':len(bundle['frozenSources'])+2,'commit':'none; delegated ownership only'}
tmp=OUT/'delivery-manifest.json.tmp';tmp.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');json.loads(tmp.read_text(encoding='utf-8'));tmp.replace(OUT/'delivery-manifest.json')
print(json.dumps({'sources':len(sources),'artifacts':len(artifacts),'counts':record['counts'],'videoSHA256':sha(OUT/'motion-revision.mp4'),'htmlSHA256':sha(OUT/'preview.html'),'containerDuration':record['video']['containerDuration']},ensure_ascii=False))
