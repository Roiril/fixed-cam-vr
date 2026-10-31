from pathlib import Path
from PIL import Image,ImageDraw
import cv2,numpy as np,json,sys,math
sys.stdout.reconfigure(encoding='utf-8')
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07'
ref=np.array(Image.open(BASE/'ref-13.000.png').convert('RGB'))[:,450:1890]
ref=cv2.resize(ref,(720,540),interpolation=cv2.INTER_AREA)
candidate=np.array(Image.open(OUT/'prototype-13.000.png').convert('RGB'))[:,225:945]
report={};groups=[]
for name,rgb in [('reference',ref),('round03',candidate)]:
    gray=cv2.cvtColor(rgb,cv2.COLOR_RGB2GRAY);neutral=np.max(rgb,axis=2).astype(float)-np.min(rgb,axis=2)<30
    report[name]={}
    for threshold in [12,25,60,120]:
        n,labels,stats,centroids=cv2.connectedComponentsWithStats(((gray>threshold)&neutral).astype(np.uint8),8)
        rows=[{'x':int(r[0]),'y':int(r[1]),'w':int(r[2]),'h':int(r[3]),'area':int(r[4]),'diameter':float(2*math.sqrt(r[4]/math.pi))} for r in stats[1:]]
        report[name][str(threshold)]={'count':len(rows),'diameterQuantiles':np.quantile([r['diameter'] for r in rows],[.5,.9,.99]).tolist() if rows else [],'largest':sorted(rows,key=lambda r:r['area'],reverse=True)[:12]}
    groups.append((name,rgb,report[name]['12']['largest']))
sheet=Image.new('RGB',(720,12*105),(24,24,24));draw=ImageDraw.Draw(sheet)
for col,(name,rgb,rows) in enumerate(groups):
    im=Image.fromarray(rgb)
    for i,row in enumerate(rows):
        x,y,w,h=[row[k] for k in ['x','y','w','h']];crop=im.crop((max(0,x-5),max(0,y-5),min(720,x+w+5),min(540,y+h+5)));crop.thumbnail((340,76));crop=crop.resize((min(340,crop.width*2),min(76,crop.height*2)))
        xx=col*360;yy=i*105;sheet.paste(crop,(xx+8,yy+26));draw.text((xx+8,yy+5),f'{name} #{i+1} {w}x{h}px area={row["area"]}',fill='white')
sheet.save(OUT/'particle-component-comparison.png')
(OUT/'particle-candidate-comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
rgb=np.array(Image.open(BASE/'ref-06.000.png').convert('RGB'))[:,450:1890]
rgb=cv2.resize(rgb,(720,540),interpolation=cv2.INTER_AREA);gray=cv2.cvtColor(rgb,cv2.COLOR_RGB2GRAY)
n,l,stats,c=cv2.connectedComponentsWithStats((gray[180:225,160:560]>160).astype(np.uint8),8)
good=[r for r in stats[1:] if r[4]>=4 and r[3]>=4]
bounds=[int(min(r[0] for r in good)+160),int(min(r[1] for r in good)+180),int(max(r[0]+r[2] for r in good)+160),int(max(r[1]+r[3] for r in good)+180)]
text={'referenceTime':6,'size':[720,540],'manualROI':[160,180,560,225],'threshold':160,'minimumComponentHeight':4,'minimumComponentArea':4,'components':len(good),'bounds':bounds,'widthFraction':(bounds[2]-bounds[0])/720}
(OUT/'reference-text-width.json').write_text(json.dumps(text,indent=2),encoding='utf-8')
print(json.dumps({k:{th:v[th]['count'] for th in ['12','25','60','120']} for k,v in report.items()}));print('text width fraction',text['widthFraction'])
