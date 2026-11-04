from pathlib import Path
from PIL import Image,ImageDraw
import subprocess,json,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');OUT=ROOT/'revision-02/motion';TARGET=OUT/'reference-comparison';TARGET.mkdir(exist_ok=True)
video=OUT/'motion-revision.mp4';assert video.is_file()
times=[round(3+i/10,1) for i in range(21)];indices=[round(t*30) for t in times]
filt="select='"+'+'.join(f'eq(n,{n})' for n in indices)+"'"
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-i',str(video),'-vf',filt,'-fps_mode','passthrough','-y',str(TARGET/'revision-%03d.png')],check=True)
evidence=json.loads((OUT/'evidence/manifest.json').read_text(encoding='utf-8'))
entries=[]
for i,(t,index) in enumerate(zip(times,indices),1):
    ref=next(r for r in evidence['frames'] if r['source']=='reference' and r['nominalTime']==t)
    old=next(r for r in evidence['frames'] if r['source']=='old-r07-b' and r['nominalTime']==t)
    p=TARGET/f'revision-{i:03d}.png';paths=[Path(ref['path']),Path(old['path']),p]
    for f in paths:assert f.is_file(),f
    assert hashlib.sha256(paths[0].read_bytes()).hexdigest()==ref['sha256']
    assert hashlib.sha256(paths[1].read_bytes()).hexdigest()==old['sha256']
    images=[Image.open(f).convert('RGB') for f in paths];assert [im.size for im in images]==[(2340,1080),(1170,540),(1170,540)]
    sheet=Image.new('RGB',(2340,1680),(244,244,244));d=ImageDraw.Draw(sheet);sheet.paste(images[0],(0,30));sheet.paste(images[1],(0,1140));sheet.paste(images[2],(1170,1140))
    d.text((8,8),f"reference | nominal {t:.3f}s | actual PTS {ref['actualTime']:.6f}s | {paths[0].name} | 2340x1080 native",fill='black')
    d.text((8,1118),f"old R7 B | actual {old['actualTime']:.6f}s | {paths[1].name} | 1170x540 native",fill='black')
    d.text((1178,1118),f"revision 02 | frame {index} | actual {index/30:.6f}s | {p.name} | native",fill='black')
    target=TARGET/f'native-{t:.3f}.png';sheet.save(target)
    entries.append({'time':t,'reference':{'path':str(paths[0]),'actualTime':ref['actualTime'],'sha256':ref['sha256']},'old':{'path':str(paths[1]),'actualTime':old['actualTime'],'sha256':old['sha256']},'revision':{'path':str(p),'actualTime':index/30,'frameIndex':index,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()},'sheet':str(target),'resampled':False})
(OUT/'reference-comparison-manifest.json').write_text(json.dumps({'entries':entries,'revisionExtraction':'Decode final MP4 from start; select exact integer frame indices. No seek, fps filter or temporal substitution.','referenceExtraction':evidence['method'],'layout':'Native reference top, native old R7 bottom left, native revision bottom right'},ensure_ascii=False,indent=2),encoding='utf-8')
for t in [8.5,10.5]:
    paths=[OUT/'stills'/f'frame-{t:.3f}-{mode}.png' for mode in ['global','local','none']]
    for p in paths:assert p.is_file(),p
    images=[Image.open(p).convert('RGB') for p in paths];assert all(im.size==(1170,540) for im in images)
    sheet=Image.new('RGB',(1170*3,570),(244,244,244));d=ImageDraw.Draw(sheet)
    for i,(p,im) in enumerate(zip(paths,images)):sheet.paste(im,(i*1170,30));d.text((i*1170+8,8),f'{p.name} | 1170x540 native | fixed same display',fill='black')
    sheet.save(OUT/f'mode-comparison-{t:.3f}.png')
print('21 source/old/revision native comparisons and 2 mode comparisons saved')
