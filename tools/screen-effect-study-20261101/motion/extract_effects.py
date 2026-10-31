"""Legacy full R6 geometry extraction, isolated from the current R7 release recipe.
Source room RGB is never exported. R7 uses prepare_round.py + soften_front.py.
"""
from pathlib import Path
from PIL import Image,ImageDraw
import cv2,numpy as np,subprocess,json,math,sys,hashlib
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07'/'legacy-reextract';ASSET=OUT/'assets';SRC=BASE/'round-04'/'source-10fps'
ASSET.mkdir(parents=True,exist_ok=True);SRC.mkdir(exist_ok=True)
VIDEO='C:/Users/kouga/Downloads/screen-20261101-004528.mp4'
if len(list(SRC.glob('frame-*.png')))!=134:
    SRC=OUT/'source-10fps';SRC.mkdir(exist_ok=True)
    subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-i',VIDEO,'-vf','crop=1440:1080:450:0,fps=10,scale=720:540','-frames:v','134','-start_number','0','-y',str(SRC/'frame-%03d.png')],check=True)
cache={}
def source(t):
    i=round(t*10)
    if i not in cache:cache[i]=np.array(Image.open(SRC/f'frame-{i:03d}.png').convert('RGB'))
    return cache[i]
def rgba(alpha,color=(255,255,255)):
    out=np.zeros((*alpha.shape,4),np.uint8);out[:,:,:3]=color;out[:,:,3]=np.clip(alpha,0,255).astype(np.uint8);return Image.fromarray(out)
def pack(name,frames,cell,cols=8):
    w,h=cell;atlas=Image.new('RGBA',(w*cols,h*math.ceil(len(frames)/cols)))
    for i,im in enumerate(frames):atlas.paste(im,(i%cols*w,i//cols*h))
    atlas.save(ASSET/f'{name}.png')
    return {'file':f'{name}.png','cell':[w,h],'columns':cols,'count':len(frames),'size':list(atlas.size)}
def prepare_inputs(target):
    adopted=BASE.parent/'still'/'round-11';records={}
    for key in ['a','b']:
        original=adopted/f'base-{key.upper()}.png';data=original.read_bytes();array=np.array(Image.open(original).convert('RGBA'));alpha=array[:,:,3].copy()
        missing=(alpha<250).astype(np.uint8);_,labels=cv2.distanceTransformWithLabels(missing,cv2.DIST_L2,5,labelType=cv2.DIST_LABEL_PIXEL)
        colors=np.zeros((int(labels.max())+1,3),np.uint8);colors[labels[missing==0]]=array[:,:,:3][missing==0];array[:,:,:3]=colors[labels]
        assert np.array_equal(alpha,array[:,:,3]);output=ASSET/f'base-{key}.png';Image.fromarray(array).save(output)
        records[key]={'sourcePath':str(original),'originalSHA256':hashlib.sha256(data).hexdigest(),'outputSHA256':hashlib.sha256(output.read_bytes()).hexdigest(),'size':list(Image.open(original).size),'alphaPreserved':True,'operation':'nearest-valid RGB extension only; adopted input unchanged'}
    target['inputs']=records
def checker(size):
    y,x=np.indices((size[1],size[0]));v=np.where((x//12+y//12)%2,110,65).astype(np.uint8);a=np.stack([v,v,v,np.full_like(v,255)],2);return Image.fromarray(a)
def inspect(name,frames,labels,thumb=(360,135),cols=3):
    w,h=thumb;sheet=Image.new('RGB',(w*cols,(h+28)*math.ceil(len(frames)/cols)),(24,24,24));draw=ImageDraw.Draw(sheet)
    for i,im in enumerate(frames):
        bg=checker(im.size);bg.alpha_composite(im);bg.thumbnail((w,h));x=i%cols*w;y=i//cols*(h+28);sheet.paste(bg.convert('RGB'),(x,y+28));draw.text((x+6,y+7),labels[i],fill='white')
    sheet.save(OUT/f'inspect-{name}.png')
manifest={'version':'6.0.0','sourceDerived':True,'sourceRGBBackgroundIncluded':False,'source':'screen-20261101-004528.mp4','notes':['Glyph and foil RGB are rebuilt as neutral traces, never source room RGB','Invasion is a scalar exterior-connected matte','Particle alpha is extracted on black; sprite RGB is rebuilt as white or red'],'sequences':{}}
manifest['parts']={'sourceDerived':['white/red sprite alpha shapes','30fps ending bokeh alpha sequence','10fps red-fragment alpha sequence','small-white-point positions, peak luminance and bidirectional LK motion','glyph one-line alpha sequence','foil reflective trace and measured placement sequence','invasion darkening/front scalar sequence','background camera motion lookup'],'procedural':['replacement white/red colors','local support around reflective foil strands and exclusion regions','coverage-preserving signed-distance front reconstruction and multiscale breakup','short keyframe transitions','pre-ending bokeh parallax, growth and direction change','CRT scanlines, horizontal flutter, grain, and vignette','black-hold envelope and closing-caption text','replaceable input photo composition']}
# White glyph trace, kept in one line. Top-hat suppresses slowly varying room illumination.
texts=[];textTimes=[round(5.7+i*.1,1) for i in range(71)];textROI=[155,164,565,230]
for t in textTimes:
    rgb=source(t);gray=cv2.cvtColor(rgb,cv2.COLOR_RGB2GRAY);x0,y0,x1,y1=textROI
    g=gray[y0:y1,x0:x1];neutral=(np.max(rgb[y0:y1,x0:x1],2).astype(float)-np.min(rgb[y0:y1,x0:x1],2))<45
    top=cv2.morphologyEx(g,cv2.MORPH_TOPHAT,cv2.getStructuringElement(cv2.MORPH_RECT,(9,7)))
    a=np.clip((g.astype(float)-100)*2.,0,255)*np.clip(top.astype(float)/35,0,1)*neutral
    grouped=cv2.dilate((a>25).astype(np.uint8),np.ones((2,3),np.uint8))
    n,lab,stats,_=cv2.connectedComponentsWithStats(grouped,8);keep=np.zeros_like(g,np.uint8)
    eligible=[(j,s) for j,s in enumerate(stats[1:],1) if 2<=s[2]<=410 and 6<=s[3]<=58 and 10<=s[4]<=15000]
    for j,s in eligible:
        if s[3]>=12 or s[2]/s[3]<5:keep[lab==j]=1
    a[max(0,225-y0):]=0
    a*=keep
    # The foil tip overlaps the lowest central letter strokes. Prefer an incomplete
    # glyph edge to exporting a reflective foil streak inside the glyph-only asset.
    yy,xx=np.indices(a.shape);a[(yy+y0>=217)&(xx+x0>=325)&(xx+x0<=405)]=0
    n,lab,stats,_=cv2.connectedComponentsWithStats((a>25).astype(np.uint8),8)
    for j,s in enumerate(stats[1:],1):
        if s[1]>50 and s[3]<4:a[lab==j]=0
        if s[1]>50 and s[3]<11 and 315<s[0]+x0<420:a[lab==j]=0
    texts.append(rgba(a))
meta=pack('glyph-trace',texts,(410,66));meta.update(times=textTimes,roi=textROI);manifest['sequences']['glyph']=meta
inspect('glyph',texts[::5],[f'{textTimes[i]:.1f}s glyph alpha 410x66' for i in range(0,len(texts),5)],(410,100),3)
# Thin foil: white reflective trace plus black inferred silhouette. No original RGB is copied.
foils=[];foilTimes=[round(i*.1,1) for i in range(125)];foilROIs=[];foilStats=[]
for t in foilTimes:
    rgb=source(t);gray=cv2.cvtColor(rgb,cv2.COLOR_RGB2GRAY)
    if t<5.4:
        cx=380+40*math.exp(-t/.5) if t<3 else float(np.interp(t,[3,3.6,5.4],[380,350,350]));cy=190+130*math.exp(-t/.35) if t<3 else float(np.interp(t,[3,3.6,5.4],[190,250,250]));roi=[max(355 if t<.3 else 0,round(cx-105)),round(cy-47),round(cx+105),round(cy+47)]
    else:roi=[250,232,500,317]
    x0,y0,x1,y1=roi;g=gray[y0:y1,x0:x1];c=rgb[y0:y1,x0:x1];neutral=np.max(c,2).astype(float)-np.min(c,2)<40
    trace=np.clip((g.astype(float)-110)*2.2,0,255)*neutral
    if t<3.25:
        yy,xx=np.indices(g.shape);diamond=(np.abs(xx+x0-cx)/100+np.abs(yy+y0-cy)/48)<1.05
        b=cv2.morphologyEx(((g<100)&neutral&diamond).astype(np.uint8),cv2.MORPH_CLOSE,np.ones((3,3),np.uint8))
    else:
        top=cv2.MORPH_TOPHAT;detail=cv2.morphologyEx(g,top,cv2.getStructuringElement(cv2.MORPH_RECT,(9,7)))
        trace*=np.clip(detail.astype(float)/30,0,1)
        yy,xx=np.indices(g.shape)
        excluded=((xx+x0<375)&(yy+y0>275))
        # At 3.3--3.6 the character ears sit above the later head exclusion.
        # Remove them before hull construction so neither trace nor silhouette uses them.
        if 3.25<=t<=3.6:excluded|=(xx+x0<345)&(yy+y0>254)
        guide=((np.abs(xx+x0-350)/110+np.abs(yy+y0-260)/55)<1.2)&~excluded
        trace[excluded]=0
        b=cv2.dilate(((trace>25)&guide).astype(np.uint8),np.ones((3,3),np.uint8))
    n,lab,stats,_=cv2.connectedComponentsWithStats(b,8)
    candidates=[(j,s) for j,s in enumerate(stats[1:],1) if 30<=s[2]<=230 and 9<=s[3]<=125 and s[4]>=80]
    if t>=3.25:
        union=np.zeros_like(b)
        for j,s in enumerate(stats[1:],1):
            if s[4]>=3:union[lab==j]=1
        if union.sum()>35:
            x,y,w,h=cv2.boundingRect(union);lab=union.astype(np.int32);candidates=[(1,np.array([x,y,w,h,int(union.sum())]))]
    if candidates:
        j,s=max(candidates,key=lambda z:z[1][4]*(z[1][2]/max(1,z[1][3]))**.25)
        region=(lab==j).astype(np.uint8)
        if t<3.25:region=cv2.dilate(region,np.ones((7,7),np.uint8))
        region=cv2.morphologyEx(region,cv2.MORPH_CLOSE,np.ones((5,5),np.uint8))
        # Limit source trace to the selected component support, not neighboring letters or face.
        permitted=cv2.dilate(region,np.ones((3,3),np.uint8));wh=trace*permitted
        # Local support follows reflective strands. No convex hull or flat black core.
        strands=(wh>12).astype(np.uint8)
        distance=cv2.distanceTransform(1-strands,cv2.DIST_L2,5)
        sil=(np.clip((3.3-distance)/2.2,0,1)*210).astype(np.uint8)
        sil=np.maximum(sil,np.clip(wh,0,255).astype(np.uint8))
        sil=cv2.GaussianBlur(sil,(3,3),.45)
        # A weak concave face connects nearby folds without a convex black plate.
        face=cv2.dilate(strands,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(9,7)))
        face=cv2.morphologyEx(face,cv2.MORPH_CLOSE,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(27,19)))
        face=cv2.morphologyEx(face,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(3,3)))
        if t<3.25:face=np.maximum(face,region)
        fy,fx=np.indices(face.shape);tear=np.sin(fx*.147+fy*.201+t*2.1)+.65*np.sin(fx*.073-fy*.239-t*1.3)
        face=((face>0)&~((tear>1.53)&(distance>2.))).astype(np.uint8)
        faceAlpha=cv2.GaussianBlur((face*145).astype(np.uint8),(3,3),.6)
        sil=np.maximum(sil,faceAlpha)
        if np.count_nonzero(strands)<8 or np.count_nonzero(wh>60)<20:sil[:]=0;wh[:]=0
        z=np.zeros((*g.shape,4),np.uint8);z[:,:,:3]=np.clip(wh,0,255).astype(np.uint8)[:,:,None];z[:,:,3]=sil
        im=Image.fromarray(z);box=im.getbbox();crop=im.crop(box) if box else Image.new('RGBA',(1,1));pos=[x0+(box[0] if box else 0),y0+(box[1] if box else 0)];foilStats.append({'t':t,'selectedArea':int(s[4]),'bounds':[int(v) for v in s[:4]]})
    else:crop=Image.new('RGBA',(1,1));pos=[0,0];foilStats.append({'t':t,'missing':True})
    canvas=Image.new('RGBA',(250,140));crop.thumbnail((250,140));canvas.paste(crop,(0,0));foils.append(canvas);foilROIs.append({'xy':pos,'size':list(crop.size)})
meta=pack('foil-trace',foils,(250,140));meta.update(times=foilTimes,placements=foilROIs);manifest['sequences']['foil']=meta
inspect('foil',foils[::5],[f'{foilTimes[i]:.1f}s foil only 250x140' for i in range(0,len(foils),5)],(300,140),4)
(OUT/'foil-extraction.json').write_text(json.dumps(foilStats,indent=2),encoding='utf-8')
if '--foil-only' in sys.argv:
    saved=json.loads((ASSET/'effect-assets.json').read_text(encoding='utf-8'));saved['sequences']['foil']=meta
    temporary=ASSET/'effect-assets.json.tmp';temporary.write_text(json.dumps(saved,separators=(',',':')),encoding='utf-8');temporary.replace(ASSET/'effect-assets.json')
    print('foil only regenerated; weak reflection support removed');raise SystemExit(0)
# Exterior-connected ink front. Store only scalar opacity, never source room color.
mattes=[];edgeFrames=[];matteTimes=[round(2.5+i*.1,1) for i in range(31)]+[round(10.9+i*.1,1) for i in range(25)]
track=json.loads((ROOT/'motion-track.json').read_text(encoding='utf-8'))['rows']
def camera_matrix(t):
    row=min(track,key=lambda r:abs(r['t']-min(11.2,max(5,t))))
    return np.vstack([np.array(row['matrix']),[0,0,1]])
for t in matteTimes:
    rgb=source(t).astype(float);gray=cv2.cvtColor(rgb.astype(np.uint8),cv2.COLOR_RGB2GRAY)
    baseTime=2.5 if t<4.2 else 5.7 if t<5.6 else 10.8
    reference=cv2.cvtColor(source(baseTime),cv2.COLOR_RGB2GRAY)
    if baseTime>=5:
        transform=camera_matrix(t)@np.linalg.inv(camera_matrix(baseTime));reference=cv2.warpAffine(reference,transform[:2],(720,540),borderMode=cv2.BORDER_REFLECT_101)
    ratio=np.clip((reference.astype(np.float32)+8-gray)/(reference.astype(np.float32)+8),0,1)
    unknown=cv2.dilate((reference<35).astype(np.uint8),np.ones((9,9),np.uint8))*255
    structure=cv2.dilate(cv2.Canny(reference,35,85),np.ones((25,25),np.uint8))
    unknown=np.maximum(unknown,structure)
    yy,xx=np.indices(gray.shape);unknown=np.maximum(unknown,(((yy>300)&(reference>100)).astype(np.uint8)*255))
    _,nearest=cv2.distanceTransformWithLabels((unknown>0).astype(np.uint8),cv2.DIST_L2,5,labelType=cv2.DIST_LABEL_PIXEL)
    values=np.zeros(int(nearest.max())+1,np.float32);values[nearest[unknown==0]]=ratio[unknown==0];ratio=values[nearest]
    ratio=cv2.GaussianBlur(ratio,(5,5),1.)
    candidate=(ratio>.60).astype(np.uint8)
    candidate=cv2.morphologyEx(candidate,cv2.MORPH_CLOSE,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(11,11)))
    candidate=cv2.morphologyEx(candidate,cv2.MORPH_OPEN,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(17,17)))
    n,lab,stats,_=cv2.connectedComponentsWithStats(candidate,8);ink=np.zeros_like(candidate)
    for j,s in enumerate(stats[1:],1):
        if s[0]<3 or s[1]<3 or s[0]+s[2]>717 or s[1]+s[3]>537:ink[lab==j]=1
    ink=cv2.morphologyEx(ink,cv2.MORPH_CLOSE,np.ones((9,9),np.uint8))
    yy,xx=np.indices(ink.shape);dist=np.minimum.reduce([xx,719-xx,yy,539-yy])
    limit=float(np.interp(t,[2.5,2.8,3.2,3.7,4.2,4.6,5.1,5.5],[0,15,100,350,350,180,45,0])) if t<6 else float(np.interp(t,[10.9,11.25,11.5,11.8,12.2,12.5,12.8],[0,8,65,155,260,350,350]))
    gate=np.clip((limit-dist)/25,0,1)
    ink=(ink*gate>.5).astype(np.uint8)
    n,lab,stats,_=cv2.connectedComponentsWithStats(1-ink,8)
    visible=[j for j,s in sorted(enumerate(stats[1:],1),key=lambda z:z[1][4],reverse=True)[:2 if t>6 else 1] if s[4]>500]
    if ink.mean()>.5:ink=(~np.isin(lab,visible)).astype(np.uint8)
    if 3.35<=t<=4.4:
        blue=(rgb[:,:,2]>80)&(rgb[:,:,2]>rgb[:,:,0]*1.2)
        window=((gray>50)|blue)&(xx>245)&(xx<475)&(yy>75)&(yy<225)
        n,wl,ws,_=cv2.connectedComponentsWithStats(window.astype(np.uint8),8)
        candidates=[(j,s) for j,s in enumerate(ws[1:],1) if s[4]>700]
        if candidates:
            j,s=max(candidates,key=lambda z:z[1][4]);ink[wl==j]=0
    # Rebuild the measured front as a smooth signed-distance field, then break it
    # at multiple scales. Quantile correction retains measured global coverage.
    coverage=float(ink.mean())
    if 0<coverage<1:
        sdf=cv2.distanceTransform(ink,cv2.DIST_L2,5)-cv2.distanceTransform(1-ink,cv2.DIST_L2,5)
        sdf=cv2.GaussianBlur(sdf,(0,0),4.)
        noise=np.zeros_like(sdf)
        for cell,amplitude,seed in [(72,7.,310),(24,4.,311),(5,1.8,312)]:
            rng=np.random.default_rng(seed);small=rng.uniform(-1,1,(max(3,540//cell+2),max(3,720//cell+2))).astype(np.float32)
            large=cv2.resize(small,(720,540),interpolation=cv2.INTER_CUBIC)
            noise+=np.roll(large,(round(t*19),round(t*27)),axis=(0,1))*amplitude
        field=sdf+noise
        if 11.95<=t<12.7:
            progress=np.clip((t-11.95)/.4,0,1);progress=progress*progress*(3-2*progress)
            cracks=np.zeros_like(ink,dtype=bool)
            for k,center in enumerate([300,495]):
                rng=np.random.default_rng(460+k);knots=np.arange(0,600,32)
                lateral=np.interp(np.arange(540),knots,rng.uniform(-36,36,len(knots)))[:,None]
                widths=np.interp(np.arange(540),knots,rng.uniform(-1,1,len(knots)))[:,None]
                curve=center+lateral+noise*.8+(t-12.3)*(11 if k else -8)
                width=progress*np.clip(17+widths*15+noise*.8,3,39)
                cracks|=np.abs(xx-curve)<width
            for cx,cy,rx,ry in [(365,315,22,32),(460,425,29,20),(245,405,16,25)]:
                cracks|=((xx-cx)/(rx*max(.02,progress)))**2+((yy-cy)/(ry*max(.02,progress)))**2<1+noise*.065
            field=np.where(cracks,field+500,field)
        threshold=float(np.quantile(field,1-coverage))
        a=(np.clip((field-threshold)/1.8+.5,0,1)*255).astype(np.uint8)
    else:a=np.full_like(ink,255 if coverage else 0)
    if t>=12.8:a=np.full_like(a,255)
    visible=(a<128).astype(np.uint8)
    edgeDistance=cv2.distanceTransform(visible,cv2.DIST_L2,5)-cv2.distanceTransform(1-visible,cv2.DIST_L2,5)
    grain=(np.sin(xx*127.1+yy*311.7+math.floor(t*30)*73.1)*43758.5453)%1
    edgeAlpha=(np.clip(1-np.abs(edgeDistance)/7,0,1)*np.clip((grain-.935)/.065,0,1)*115).astype(np.uint8)
    if not 11.8<=t<=12.7:edgeAlpha[:]=0
    edgeFrames.append(rgba(edgeAlpha))
    mattes.append(rgba(a,(255,255,255)).resize((512,384),Image.Resampling.LANCZOS))
meta=pack('invasion-matte',mattes,(512,384));meta.update(times=matteTimes);manifest['sequences']['invasion']=meta
inspect('invasion',[rgba(np.array(im)[:,:,3]) for im in mattes[::3]],[f'{matteTimes[i]:.1f}s scalar alpha 512x384' for i in range(0,len(mattes),3)],(360,270),4)
edgeMeta=pack('edge-granules',edgeFrames,(720,540),4);edgeMeta.update(times=matteTimes,sourceDerived=False,method='procedural fine grains concentrated at the reconstructed scalar front')
manifest['sequences']['edgeGranules']=edgeMeta
if '--geometry-only' in sys.argv:
    saved=json.loads((ASSET/'effect-assets.json').read_text(encoding='utf-8'));saved['version']='6.0.0'
    for key in ['foil','invasion','edgeGranules']:saved['sequences'][key]=manifest['sequences'][key]
    saved['parts']['procedural']=list(dict.fromkeys(saved['parts']['procedural']+['weak concave foil face with local holes','foil vertical display calibration up to 1.35 after 5.15 seconds','perceptual camera gain: roll/zoom 1.35 and translation 1.30','aperiodic island divisions with global coverage correction','fine white grains concentrated around disappearing islands']))
    prepare_inputs(saved)
    temporary=ASSET/'effect-assets.json.tmp';temporary.write_text(json.dumps(saved,separators=(',',':')),encoding='utf-8');temporary.replace(ASSET/'effect-assets.json')
    print('foil, fragmented matte and edge grains regenerated; other round05 assets retained');raise SystemExit(0)
# Actual white particles on a black frame, and flow sampled from neighboring effect-only frames.
canonical=np.array(Image.open(BASE/'ref-13.000.png').convert('RGB'))[:,450:1890]
canonical=cv2.resize(canonical,(720,540),interpolation=cv2.INTER_AREA)
gray=cv2.cvtColor(canonical,cv2.COLOR_RGB2GRAY);neutral=np.max(canonical,2).astype(float)-np.min(canonical,2)<35
previous=cv2.cvtColor(source(12.9),cv2.COLOR_RGB2GRAY);following=cv2.cvtColor(source(13.1),cv2.COLOR_RGB2GRAY)
flow=cv2.calcOpticalFlowFarneback(np.clip(previous.astype(float)*4,0,255).astype(np.uint8),np.clip(following.astype(float)*4,0,255).astype(np.uint8),None,.5,4,25,4,7,1.5,0)/.2
white=(gray>11)&neutral;n,labels,stats,centroids=cv2.connectedComponentsWithStats(white.astype(np.uint8),8)
sprites=[];particles=[]
for j,s in enumerate(stats[1:],1):
    x,y,w,h,area=[int(v) for v in s]
    if not(1<=w<=60 and 1<=h<=60 and 1<=area<=1800):continue
    margin=5 if max(w,h)>8 else 2;x0=max(0,x-margin);y0=max(0,y-margin);x1=min(720,x+w+margin);y1=min(540,y+h+margin)
    selected=(labels[y0:y1,x0:x1]==j).astype(np.uint8);selected=cv2.dilate(selected,np.ones((5,5),np.uint8))
    alpha=gray[y0:y1,x0:x1].astype(float)*neutral[y0:y1,x0:x1]*selected
    im=rgba(alpha);cell=Image.new('RGBA',(72,72));cell.paste(im,(0,0));idx=len(sprites);sprites.append(cell)
    cx,cy=centroids[j];vx,vy=np.median(flow[y:y+h,x:x+w][labels[y:y+h,x:x+w]==j],axis=0)
    particles.append({'sprite':idx,'xy':[float(cx),float(cy)],'size':list(im.size),'centerInSprite':[float(cx-x0),float(cy-y0)],'velocityPxPerSec':[float(np.clip(vx,-120,120)),float(np.clip(vy,-120,120))],'area':area,'peak':int(alpha.max())})
meta=pack('white-sprites',sprites,(72,72),16);meta.update(particles=particles,anchorTime=13.,flowSampleTimes=[12.9,13.1]);manifest['sequences']['white']=meta
inspect('white-sprites',sprites,[f'white #{i} source crop {particles[i]["size"]}' for i in range(len(sprites))],(110,90),12)
field=[]
for t in [round(2.7+i*.1,1) for i in range(28)]+[round(10.9+i*.1,1) for i in range(24)]:
    c=source(t);g=cv2.cvtColor(c,cv2.COLOR_RGB2GRAY);background=cv2.GaussianBlur(g,(31,31),6)
    good=(g.astype(float)>background+8)&(background<32)&((np.max(c,2).astype(float)-np.min(c,2))<45)
    if t<12.6:good[164:232,150:570]=False;good[225:320,235:510]=False
    if t<11.1:good[280:520,210:390]=False
    n,l,stats,centroids=cv2.connectedComponentsWithStats(good.astype(np.uint8),8);points=[]
    for j,s in enumerate(stats[1:],1):
        x,y,w,h,area=[int(v) for v in s]
        if area>50 or w>12 or h>12:continue
        cx,cy=centroids[j];peak=int(g[y:y+h,x:x+w][l[y:y+h,x:x+w]==j].max());points.append([round(float(cx),2),round(float(cy),2),peak,area])
    supported=0
    if points and t<13.2 and t!=5.4:
        first=np.clip(g.astype(float)*4,0,255).astype(np.uint8);following=cv2.cvtColor(source(round(t+.1,1)),cv2.COLOR_RGB2GRAY);following=np.clip(following.astype(float)*4,0,255).astype(np.uint8)
        p0=np.array([[p[:2]] for p in points],np.float32)
        p1,status,_=cv2.calcOpticalFlowPyrLK(first,following,p0,None,winSize=(25,25),maxLevel=3)
        back,statusBack,_=cv2.calcOpticalFlowPyrLK(following,first,p1,None,winSize=(25,25),maxLevel=3)
        for k,p in enumerate(points):
            valid=status[k,0] and statusBack[k,0] and np.linalg.norm(back[k,0]-p0[k,0])<2.5 and np.linalg.norm(p1[k,0]-p0[k,0])<120
            velocity=(p1[k,0]-p0[k,0])*10 if valid else np.zeros(2)
            p.extend([round(float(velocity[0]),2),round(float(velocity[1]),2)]);supported+=bool(valid)
    else:
        for p in points:p.extend([0.,0.])
    field.append({'t':t,'points':points,'lkSupported':supported})
manifest['densityField']={'method':'small neutral bright components on locally black regions; positions, luminance and bidirectional LK vectors','sourceRGBIncluded':False,'frames':field}
reds=[];redItems=[]
for t in [3.7,4.,4.3,11.8,12.2,12.4,12.8,13.]:
    rgb=source(t).astype(float);strength=np.clip((rgb[:,:,0]-np.maximum(rgb[:,:,1]*1.8,rgb[:,:,2]*1.3)-8)*2.3,0,255)
    strength*=cv2.cvtColor(rgb.astype(np.uint8),cv2.COLOR_RGB2GRAY)<45
    n,l,stats,c=cv2.connectedComponentsWithStats((strength>20).astype(np.uint8),8)
    for j,s in sorted(enumerate(stats[1:],1),key=lambda z:z[1][4],reverse=True)[:36]:
        x,y,w,h,area=[int(v) for v in s]
        if not(3<=w<=140 and 6<=h<=180 and 12<=area<=5000 and area/(w*h)<.7):continue
        a=strength[y:y+h,x:x+w]*(l[y:y+h,x:x+w]==j);im=rgba(a,(145,4,12));im.thumbnail((128,160));z=np.array(im);z[:,:,:3]=[145,4,12];im=Image.fromarray(z);cell=Image.new('RGBA',(128,160));cell.paste(im,(0,0));idx=len(reds);reds.append(cell);redItems.append({'sprite':idx,'sourceTime':t,'xy':[x,y],'size':list(im.size)})
meta=pack('red-sprites',reds,(128,160),8);meta.update(particles=redItems);manifest['sequences']['red']=meta
inspect('red-sprites',reds,[f'red #{i} {redItems[i]["size"]} t={redItems[i]["sourceTime"]}' for i in range(len(reds))],(128,180),8)
# Full effect-only red alpha retains large foreground fragments and actual passage.
redFrames=[];redTimes=[f['t'] for f in field]
for t in redTimes:
    c=source(t).astype(float);a=np.clip((c[:,:,0]-np.maximum(c[:,:,1]*1.8,c[:,:,2]*1.3)-8)*2.3,0,255)
    a*=cv2.cvtColor(c.astype(np.uint8),cv2.COLOR_RGB2GRAY)<45
    n,l,stats,_=cv2.connectedComponentsWithStats((a>20).astype(np.uint8),8);keep=np.zeros_like(a)
    for j,s in enumerate(stats[1:],1):
        if s[4]>=12 and s[2]>=3 and s[3]>=5:keep[l==j]=1
    redFrames.append(rgba(a*keep,(145,4,12)))
meta=pack('red-motion',redFrames,(720,540),4);meta.update(times=redTimes);manifest['sequences']['redMotion']=meta
# 30fps ending white alpha, not RGB footage. Components are isolated before packing.
endSource=OUT/'source-ending-30fps';endSource.mkdir(exist_ok=True)
if len(list(endSource.glob('frame-*.png')))!=21:
    subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-i',VIDEO,'-vf','trim=start=12.6:end=13.3,setpts=PTS-STARTPTS,crop=1440:1080:450:0,fps=30,scale=720:540','-frames:v','21','-start_number','0','-y',str(endSource/'frame-%03d.png')],check=True)
ending=[];endingStats=[]
for i in range(21):
    c=np.array(Image.open(endSource/f'frame-{i:03d}.png').convert('RGB'));g=cv2.cvtColor(c,cv2.COLOR_RGB2GRAY)
    backdrop=cv2.GaussianBlur(g,(31,31),6);neutral=np.max(c,2).astype(float)-np.min(c,2)<35
    maskIndex=min(range(len(matteTimes)),key=lambda j:abs(matteTimes[j]-(12.6+i/30)))
    measuredMask=np.array(mattes[maskIndex])[:,:,3]
    measuredMask=cv2.resize(measuredMask,(720,540),interpolation=cv2.INTER_LINEAR)
    safeBlack=cv2.erode((measuredMask>245).astype(np.uint8),np.ones((9,9),np.uint8))>0
    candidate=(g>6)&neutral&(backdrop<32)&safeBlack
    n,l,stats,_=cv2.connectedComponentsWithStats(candidate.astype(np.uint8),8);a=np.zeros_like(g);kept=0
    for j,s in enumerate(stats[1:],1):
        x,y,w,h,area=s
        if not(1<=w<=65 and 1<=h<=65 and 15<area<=1800 and max(w,h)/max(1,min(w,h))<3.5 and area/(w*h)>.22):continue
        support=cv2.dilate((l==j).astype(np.uint8),np.ones((5,5),np.uint8))>0
        a[support]=np.maximum(a[support],(g*neutral)[support]);kept+=1
    ending.append(rgba(a));endingStats.append({'t':12.6+i/30,'components':kept,'alphaArea':int((a>0).sum())})
meta=pack('white-motion',ending,(720,540),4);meta.update(times=[12.6+i/30 for i in range(21)],method='neutral white components on locally black background; RGB replaced by constant white');manifest['sequences']['whiteMotion']=meta
(OUT/'ending-particle-extraction.json').write_text(json.dumps(endingStats,indent=2),encoding='utf-8')
# Alternate input keeps original screen alpha but changes the underlying photo.
prepare_inputs(manifest)
(ASSET/'effect-assets.json').write_text(json.dumps(manifest,separators=(',',':')),encoding='utf-8')
print('assets:',{k:v['count'] for k,v in manifest['sequences'].items()},flush=True)
