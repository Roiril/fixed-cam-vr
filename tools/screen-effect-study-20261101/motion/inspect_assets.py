from pathlib import Path
from PIL import Image,ImageDraw
import numpy as np,json,sys,hashlib
sys.stdout.reconfigure(encoding='utf-8')
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion/round-07');ASSET=OUT/'assets'
manifest=json.loads((ASSET/'effect-assets.json').read_text(encoding='utf-8'));report=[]
for key,meta in manifest['sequences'].items():
    atlas=Image.open(ASSET/meta['file']).convert('RGBA');w,h=meta['cell'];cols=meta['columns'];a=np.array(atlas);visible=a[:,:,3]>0
    if key in ['glyph','white','whiteMotion','edgeGranules','invasion']:assert np.all(a[:,:,:3][visible]==255)
    if key in ['red','redMotion']:assert np.all(a[:,:,:3][visible]==np.array([145,4,12]))
    if key=='foil':assert np.all(a[:,:,0][visible]==a[:,:,1][visible]) and np.all(a[:,:,1][visible]==a[:,:,2][visible])
    report.append({'kind':key,'count':meta['count'],'atlasSize':list(atlas.size),'cell':[w,h],'visiblePixels':int(visible.sum()),'alphaRange':[int(a[:,:,3].min()),int(a[:,:,3].max())],'colorContract':'constant white' if key in ['glyph','white','whiteMotion','edgeGranules','invasion'] else 'constant red' if key in ['red','redMotion'] else 'neutral trace only','sha256':hashlib.sha256((ASSET/meta['file']).read_bytes()).hexdigest()})
    tw,th,columns=(250,140,6) if key=='foil' else (410,90,4) if key=='glyph' else (360,270,4) if key in ['invasion','whiteMotion','redMotion','edgeGranules'] else (110,80,12) if key=='white' else (128,160,8)
    sheet=Image.new('RGB',(tw*columns,(th+28)*((meta['count']+columns-1)//columns)),(24,24,24));draw=ImageDraw.Draw(sheet)
    yy,xx=np.indices((h,w));v=np.where((xx//12+yy//12)%2,110,65).astype(np.uint8);checker=Image.fromarray(np.stack([v,v,v,np.full_like(v,255)],2))
    for i in range(meta['count']):
        tile=atlas.crop((i%cols*w,i//cols*h,i%cols*w+w,i//cols*h+h));bg=checker.copy();bg.alpha_composite(tile);bg.thumbnail((tw,th));x=i%columns*tw;y=i//columns*(th+28);sheet.paste(bg.convert('RGB'),(x,y+28))
        if 'times' in meta:label=f'#{i} {meta["times"][i]:.3f}s {w}x{h}'
        else:label=f'#{i} '+ 'x'.join(str(n) for n in meta['particles'][i]['size'])
        draw.text((x+5,y+7),label,fill='white')
    sheet.save(OUT/f'inspect-all-{key}.png')
    if key in ['foil','glyph']:
        indices=[32,33,34,35] if key=='foil' else [6,9,17,31,43,53,63,67]
        detail=Image.new('RGB',(w*2,(h+28)*((len(indices)+1)//2)),(80,80,80));dd=ImageDraw.Draw(detail)
        for j,i in enumerate(indices):
            tile=atlas.crop((i%cols*w,i//cols*h,i%cols*w+w,i//cols*h+h));x=j%2*w;y=j//2*(h+28);detail.paste(tile,(x,y+28),tile);dd.text((x+4,y+7),f'#{i} {meta["times"][i]:.3f}s {key} only {w}x{h}',fill='white')
        detail.save(OUT/f'{key}-overlap-cleanup-check.png')
(OUT/'asset-color-contracts.json').write_text(json.dumps({'sourceDerived':True,'originalRoomRGBExported':False,'assets':report},indent=2),encoding='utf-8')
print('all effect atlases checked:',[(r['kind'],r['count'],r['colorContract']) for r in report])
