"""Freeze a complete round-07 release and describe its provenance without scoring it."""
from pathlib import Path
from PIL import Image,ImageDraw
import hashlib,json,subprocess,sys
import numpy as np
from background_metric import occlusion_fraction
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07'
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def atomic_json(path,value):
    temp=path.with_name(path.name+'.tmp');temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8');json.loads(temp.read_text(encoding='utf-8'));temp.replace(path)
probes={}
for variant,folder in [('a',OUT),('b',OUT/'alternate-b')]:
    movie=folder/'motion-prototype.mp4'
    probe=json.loads(subprocess.check_output(['ffprobe','-v','error','-show_entries','stream=codec_name,width,height,nb_frames,r_frame_rate,duration:format=duration','-of','json',str(movie)],text=True))
    stream=probe['streams'][0]
    assert stream['codec_name']=='h264' and stream['width']==1170 and stream['height']==540 and int(stream['nb_frames'])==469
    assert len(probe['streams'])==1
    black=np.array(Image.open(folder/'prototype-13.600.png').convert('RGB'))[:,225:945]
    assert int(black.max())==0
    closure={}
    for t in [12.4,12.6,14.4]:
        label=f'{t:06.3f}';a=np.array(Image.open(folder/f'background-{label}.png').convert('L'),dtype=float)[:,225:945];b=np.array(Image.open(folder/f'control-{label}.png').convert('L'),dtype=float)[:,225:945];valid=b>32
        closure[str(t)]=occlusion_fraction(a,b,t)
    assert closure['12.4']<.99 and closure['12.6']>.90 and closure['14.4']<.01
    probe['closureChecks']={'centralBlack136MaxChannel':int(black.max()),'occlusion':closure}
    probe['sha256']=digest(movie);probes[variant]=probe
    for t in [6,12.4,13,13.6,14.4]:
        subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-ss',str(t),'-i',str(movie),'-frames:v','1','-y',str(folder/f'mp4-check-{t:06.3f}.png')],check=True)
atomic_json(OUT/'ffprobe-verification.json',probes)
times=[0,3.5,6,11.8,12.4,13,13.6,14.4,15]
sheet=Image.new('RGB',(1170,298*len(times)),(245,245,245));draw=ImageDraw.Draw(sheet)
for row,t in enumerate(times):
    for col,(label,folder) in enumerate([('input A',OUT),('input B',OUT/'alternate-b')]):
        path=folder/f'prototype-{t:06.3f}.png'
        if not path.is_file():raise FileNotFoundError(path)
        im=Image.open(path).convert('RGB');im.thumbnail((585,270));x=col*585;y=row*298;sheet.paste(im,(x,y+28));draw.text((x+8,y+7),f'{t:.3f}s | {label} | {path.name}',fill='black')
sheet.save(OUT/'alternate-input-comparison.jpg',quality=93)
snapshot=OUT/'source';snapshot.mkdir(exist_ok=True)
sources=[]
for path in sorted(ROOT.iterdir()):
    if not path.is_file() or path.suffix not in ['.js','.mjs','.py','.html','.json','.md']:continue
    target=snapshot/path.name
    if target.exists():assert target.read_bytes()==path.read_bytes(),f'Frozen source changed: {target}'
    else:target.write_bytes(path.read_bytes())
    assert digest(path)==digest(target)
    sources.append({'name':path.name,'sha256':digest(path),'bytes':path.stat().st_size})
(OUT/'analysis.md').write_bytes((ROOT/'analysis.md').read_bytes())
assets=[]
for path in sorted((OUT/'assets').iterdir()):
    if path.is_file():assets.append({'name':path.name,'sha256':digest(path),'bytes':path.stat().st_size})
files=[]
frameHashes=[]
for path in sorted(OUT.rglob('*')):
    if path.is_file() and path.suffix.lower() in ['.png','.jpg'] and 'assets' not in path.parts:
        frameHashes.append({'path':str(path.relative_to(OUT)).replace('\\','/'),'sha256':digest(path),'bytes':path.stat().st_size})
atomic_json(OUT/'frame-hashmanifest.json',frameHashes)
for path in [OUT/'motion-prototype.mp4',OUT/'motion-prototype.html',OUT/'alternate-b/motion-prototype.mp4',OUT/'asset-color-contracts.json',OUT/'background-occlusion.json',OUT/'pixel-metrics.json',OUT/'verification.json',OUT/'alternate-b/verification.json',OUT/'ffprobe-verification.json',OUT/'standalone-verification.json',OUT/'effect-revision-proof.json']:
    files.append({'path':str(path.relative_to(OUT)).replace('\\','/'),'sha256':digest(path),'bytes':path.stat().st_size})
for name in ['frame-hashmanifest.json','soft-front-metrics.json','native-comparison-manifest.json','motion-registration.json','timestamp-calibration.json']:
    path=OUT/name;files.append({'path':name,'sha256':digest(path),'bytes':path.stat().st_size})
atomic_json(OUT/'release-manifest.json',{'version':'7.0.0','sourceDerivedEffectAssets':True,'originalSceneRGBReplayedAsBackground':False,'sourceFiles':sources,'effectAssets':assets,'deliverables':files,'visualInspection':['soft scalar matte and edge-grain atlases on checkerboard; retained assets SHA matched to R6','native reference/A/B 12.0..12.7 viewed before video export','reference/candidate full sequence and full-frame details','both current adopted inputs use the same round07 effect assets'],'limitations':['Foil includes an inferred weak concave face; it is not a ground-truth object alpha','Lowest central glyph strokes are clipped where the foil tip overlaps','Soft matte and microholes approximate hidden foreground; island count is not a target','Camera uses explicit perceptual gain and small affine-driven projective adjustment; not measured depth or true parallax','Inputs remain byte-identical to current adopted round11 A/B used in R6','No original room/character/plant RGB is used as replacement background','No audio; independent similarity audit remains external']})
print('round-07 frozen:',len(sources),'source files;',len(assets),'assets; A/B H264 1170x540 469 frames')
