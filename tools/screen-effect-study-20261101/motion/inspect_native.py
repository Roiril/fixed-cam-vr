"""Preserve native pixels in reference/A/B pre-export comparisons; missing files fail."""
from pathlib import Path
from PIL import Image,ImageDraw
import subprocess,json
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07';REF=OUT/'native-reference';REF.mkdir(exist_ok=True)
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-i','C:/Users/kouga/Downloads/screen-20261101-004528.mp4','-vf','fps=10,select=between(n\\,120\\,127)','-fps_mode','passthrough','-start_number','120','-y',str(REF/'ref-%03d.png')],check=True)
rows=[]
for i in range(120,128):
    t=i/10;paths=[REF/f'ref-{i}.png',OUT/f'prototype-{t:06.3f}.png',OUT/'alternate-b'/f'prototype-{t:06.3f}.png']
    images=[Image.open(p).convert('RGB') for p in paths]
    assert [im.size for im in images]==[(2340,1080),(1170,540),(1170,540)]
    sheet=Image.new('RGB',(2340,1680),(245,245,245));d=ImageDraw.Draw(sheet)
    sheet.paste(images[0],(0,30));sheet.paste(images[1],(0,1140));sheet.paste(images[2],(1170,1140))
    d.text((8,8),f'{t:.3f}s reference {paths[0].name} 2340x1080 native',fill='black')
    for x,p in [(0,paths[1]),(1170,paths[2])]:d.text((x+8,1118),f'{t:.3f}s {p.parent.name}/{p.name} 1170x540 native',fill='black')
    target=OUT/f'native-compare-{t:06.3f}.png';sheet.save(target)
    rows.append({'t':t,'source':str(paths[0]),'comparison':str(target),'noResampling':True})
(OUT/'native-comparison-manifest.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print('8 native pixel comparisons saved; source and A/B remain at original dimensions')
