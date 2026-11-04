from pathlib import Path
import cv2,numpy as np,json,math,sys,subprocess
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion/round-07')
OUT.mkdir(exist_ok=True)
if '--metadata-only' in sys.argv:
    data=json.loads((OUT/'motion-registration.json').read_text(encoding='utf-8'))
    for i,row in enumerate(data['rows']):
        row['measured']=i>0
        if i==0:row.update(inliers=None,matches=None,medianErrorPx=None)
    data['trackingStepCount']=len(data['rows'])-1
    data['initialState']='5.0s is an initialization state, not a measured tracking step'
    (ROOT/'motion-track.json').write_text(json.dumps(data,separators=(',',':')),encoding='utf-8')
    (OUT/'motion-registration.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
    print('metadata corrected: 63 states, 62 measured tracking steps; initial anchor has no inlier claim')
    sys.exit(0)
cap=cv2.VideoCapture('C:/Users/kouga/Downloads/screen-20261101-004528.mp4')
def cv_seek_frame(t):
    cap.set(cv2.CAP_PROP_POS_MSEC,t*1000)
    ok,im=cap.read()
    if not ok:raise RuntimeError(t)
    return cv2.resize(im[:,450:1890],(720,540),interpolation=cv2.INTER_AREA)
sequence=OUT/'reference-10fps';sequence.mkdir(exist_ok=True)
if len(list(sequence.glob('frame-*.png')))!=63:
    subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-ss','5','-i','C:/Users/kouga/Downloads/screen-20261101-004528.mp4','-vf','crop=1440:1080:450:0,fps=10,scale=720:540','-frames:v','63','-start_number','0','-y',str(sequence/'frame-%03d.png')],check=True)
frame_cache={}
def frame(t):
    index=round((t-5)*10)
    if index<0 or index>62:raise ValueError('Outside calibrated 10fps sequence')
    if index not in frame_cache:frame_cache[index]=cv2.imdecode(np.frombuffer((sequence/f'frame-{index:03d}.png').read_bytes(),np.uint8),cv2.IMREAD_COLOR)
    return frame_cache[index]
def save(p,im):p.write_bytes(cv2.imencode('.png',im)[1].tobytes())
mask=np.full((540,720),255,np.uint8)
cv2.rectangle(mask,(120,120),(635,265),0,-1)
cv2.rectangle(mask,(220,245),(490,515),0,-1)
mask[:8]=0;mask[-8:]=0;mask[:,:8]=0;mask[:,-8:]=0
calibration=[]
for t in [5,6,8,10,11]:
    cv=cv_seek_frame(t)
    canonical_full=cv2.imdecode(np.frombuffer((OUT.parent/f'ref-{t:06.3f}.png').read_bytes(),np.uint8),cv2.IMREAD_COLOR)
    canonical=cv2.resize(canonical_full[:,450:1890],(720,540),interpolation=cv2.INTER_AREA)
    errors=[float(np.mean(np.abs(cv.astype(float)-frame(5+i*.1).astype(float))[mask>0])) for i in range(63)]
    canonicalErrors=[float(np.mean(np.abs(canonical.astype(float)-frame(5+i*.1).astype(float))[mask>0])) for i in range(63)]
    best=int(np.argmin(errors));canonicalBest=int(np.argmin(canonicalErrors));sequenceError=float(np.mean(np.abs(canonical.astype(float)-frame(t).astype(float))[mask>0]));cvError=float(np.mean(np.abs(canonical.astype(float)-cv.astype(float))[mask>0]))
    calibration.append({'requestedTime':t,'cvClosestSequenceTime':5+best*.1,'cvOffsetTo10fpsSeconds':5+best*.1-t,'cvClosestMeanPixelError':errors[best],'cvVsCanonicalMeanPixelError':cvError,'fixed10fpsVsCanonicalMeanPixelError':sequenceError,'canonicalClosestSequenceTime':5+canonicalBest*.1,'canonicalOffsetTo10fpsSeconds':5+canonicalBest*.1-t,'canonicalClosestMeanPixelError':canonicalErrors[canonicalBest]})
(OUT/'timestamp-calibration.json').write_text(json.dumps({'times':calibration,'sequence':'ffmpeg input -ss 5, crop center 1440x1080, fps=10, scale=720x540, 63 frames','lookupSource':'fixed10fps sequence regardless of CV seek calibration; source times are explicitly 5+index/10'},indent=2),encoding='utf-8')
print('timestamp calibration',calibration,flush=True)
if '--calibration-only' in sys.argv:sys.exit(0)
rows=[];steps=[];cumulative=np.eye(3);previous=None
for t in np.round(np.arange(5.,11.201,.1),3):
    im=frame(float(t));gray=cv2.GaussianBlur(cv2.cvtColor(im,cv2.COLOR_BGR2GRAY),(3,3),0)
    if previous is None:steps.append((float(t),cumulative.copy(),None,None,None))
    else:
        points=cv2.goodFeaturesToTrack(previous,1200,.005,5,mask=mask)
        nxt,status,error=cv2.calcOpticalFlowPyrLK(previous,gray,points,None,winSize=(25,25),maxLevel=4)
        back,backStatus,_=cv2.calcOpticalFlowPyrLK(gray,previous,nxt,None,winSize=(25,25),maxLevel=4)
        good=(status.ravel()>0)&(backStatus.ravel()>0)&(np.linalg.norm(points[:,0]-back[:,0],axis=1)<.8)
        src=points[good,0];dst=nxt[good,0]
        matrix,inliers=cv2.estimateAffinePartial2D(src,dst,method=cv2.RANSAC,ransacReprojThreshold=1.7,maxIters=6000,confidence=.999)
        if matrix is None:raise RuntimeError(f'no local transform at {t}')
        n=int(inliers.sum());pred=cv2.transform(src[None,:,:],matrix)[0];err=float(np.median(np.linalg.norm(pred-dst,axis=1)[inliers.ravel()>0]))
        if n<40 or err>1.2:raise RuntimeError(f'bad local registration {t} inliers={n} err={err}')
        cumulative=np.vstack([matrix,[0,0,1]])@cumulative
        steps.append((float(t),cumulative.copy(),n,err,int(good.sum())))
        if round(t*10)%10==0:
            preview=im.copy()
            for a,b in zip(src[inliers.ravel()>0][::5],dst[inliers.ravel()>0][::5]):cv2.arrowedLine(preview,tuple(np.round(a).astype(int)),tuple(np.round(b).astype(int)),(50,255,70),1)
            save(OUT/f'local-registration-{t:.1f}.png',preview)
    previous=gray
anchor=np.linalg.inv(next(m for t,m,_,_,_ in steps if t==6))
for t,m,n,err,matches in steps:
    matrix=(m@anchor)[:2];a,b=matrix[0,0],matrix[1,0];center=matrix@np.array([360,270,1])-np.array([360,270])
    rows.append({'t':t,'angle':math.atan2(b,a),'scale':math.hypot(a,b),'dx':float(center[0]/720),'dy':float(center[1]/540),'measured':n is not None,'inliers':n,'matches':matches,'medianErrorPx':err,'matrix':matrix.tolist()})
data={'method':'ffmpeg fixed 10fps sequence; masked static-room corners + bidirectional pyramidal Lucas-Kanade + per-step RANSAC partial affine, cumulative and normalized to 6s','timestampCalibration':'timestamp-calibration.json','rejectedMethods':'Direct 6s ORB and SIFT lose correspondence after 10.3s; support thresholds were not further relaxed','referenceTime':6,'size':[720,540],'maskedRegions':[[120,120,635,265],[220,245,490,515]],'rows':rows}
data['trackingStepCount']=len(rows)-1
data['initialState']='5.0s is an initialization state, not a measured tracking step'
(ROOT/'motion-track.json').write_text(json.dumps(data,separators=(',',':')),encoding='utf-8')
(OUT/'motion-registration.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
save(OUT/'tracking-static-mask.png',mask)
print('registration',len(rows),'states;',len(rows)-1,'measured tracking steps; min inliers',min(r['inliers'] for r in rows if r['measured']),'angle degrees',min(r['angle'] for r in rows)*180/math.pi,max(r['angle'] for r in rows)*180/math.pi,'scale',min(r['scale'] for r in rows),max(r['scale'] for r in rows),'dx px',min(r['dx'] for r in rows)*720,max(r['dx'] for r in rows)*720,'dy px',min(r['dy'] for r in rows)*540,max(r['dy'] for r in rows)*540)
canonical=np.array(Image.open(OUT.parent/'ref-13.000.png').convert('RGB'))[:,450:1890]
canonical=cv2.resize(canonical,(720,540),interpolation=cv2.INTER_AREA)
im=cv2.cvtColor(canonical,cv2.COLOR_RGB2BGR);rgb=canonical.astype(float);gray=cv2.cvtColor(im,cv2.COLOR_BGR2GRAY)
neutral=(np.max(rgb,axis=2)-np.min(rgb,axis=2))<30
summary={}
for threshold in [12,25,60,120]:
    binary=((gray>threshold)&neutral).astype(np.uint8)
    count,labels,stats,centroids=cv2.connectedComponentsWithStats(binary,8)
    components=[{'x':int(s[0]),'y':int(s[1]),'w':int(s[2]),'h':int(s[3]),'area':int(s[4]),'diameter':float(2*math.sqrt(s[4]/math.pi))} for s in stats[1:] if s[4]>=1]
    summary[str(threshold)]={'count':len(components),'diameterQuantiles':np.quantile([r['diameter'] for r in components],[.5,.9,.99]).tolist() if components else [],'largest':sorted(components,key=lambda r:r['area'],reverse=True)[:24]}
    save(OUT/f'particles-threshold-{threshold}.png',binary*255)
(OUT/'particle-components.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
save(OUT/'reference-particles-13.0.png',im)
print('particle component quantiles',[(k,v['count'],v['diameterQuantiles']) for k,v in summary.items()])
rng=np.random.default_rng(45181)
points=[]
for i in range(32):
    angle=i*2*math.pi/32;r=float(rng.uniform(.72,1.16));points.append([300+150*math.cos(angle)*r,200+58*math.sin(angle)*r])
for i in range(220):
    angle=float(rng.uniform(0,2*math.pi));r=math.sqrt(float(rng.uniform(0,1)));points.append([300+146*r*math.cos(angle),200+56*r*math.sin(angle)])
sub=cv2.Subdiv2D((0,0,600,400))
for p in points:sub.insert(tuple(p))
triangles=[]
pa=np.array(points)
for tr in sub.getTriangleList():
    coords=tr.reshape(3,2);idx=[int(np.argmin(np.linalg.norm(pa-c,axis=1))) for c in coords]
    if len(set(idx))!=3 or float(rng.random())<.065:continue
    if max(np.linalg.norm(coords[0]-coords[1]),np.linalg.norm(coords[0]-coords[2]),np.linalg.norm(coords[1]-coords[2]))>90:continue
    triangles.append(idx)
mesh={'vertices':[[float(x-300),float(y-200),float(rng.uniform(-6,6))] for x,y in points],'triangles':triangles}
(ROOT/'foil-mesh.json').write_text(json.dumps(mesh,separators=(',',':')),encoding='utf-8')
print('irregular foil mesh',len(points),'vertices',len(triangles),'facets')
plate=np.array(Image.open(OUT.parent/'base.png').convert('RGBA'))
missing=(plate[:,:,3]<250).astype(np.uint8)
_,labels=cv2.distanceTransformWithLabels(missing,cv2.DIST_L2,5,labelType=cv2.DIST_LABEL_PIXEL)
colors=np.zeros((int(labels.max())+1,3),np.uint8)
colors[labels[missing==0]]=plate[:,:,:3][missing==0]
filled=plate.copy();filled[:,:,:3]=colors[labels]
Image.fromarray(filled).save(OUT/'base-fill.png')
