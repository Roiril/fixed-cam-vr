from pathlib import Path
import subprocess
from PIL import Image,ImageDraw
import sys
sys.stdout.reconfigure(encoding='utf-8')
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
SOURCE=Path('C:/Users/kouga/Downloads/screen-20261101-004528.mp4')
OUT.mkdir(parents=True,exist_ok=True)
def batch(times,name,w=468,cols=4):
    frames=[]
    for t in times:
        p=OUT/f'ref-{t:06.3f}.png'
        if not p.exists():
            subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-ss',str(t),'-i',str(SOURCE),'-frames:v','1','-y',str(p)],check=True)
        im=Image.open(p).convert('RGB'); im.thumbnail((w,round(w*1080/2340)))
        frames.append((t,im.copy()))
    h=frames[0][1].height+28
    sheet=Image.new('RGB',(cols*w,((len(frames)+cols-1)//cols)*h),'#202327')
    d=ImageDraw.Draw(sheet)
    for i,(t,im) in enumerate(frames):
        x=i%cols*w;y=i//cols*h;sheet.paste(im,(x,y+28));d.text((x+8,y+5),f'{t:.3f}s reference',fill='white')
    sheet.save(OUT/f'{name}.jpg',quality=92)
batch(range(16),'reference-overview')
batch([11+i*.2 for i in range(24)],'reference-ending')
print(OUT)
