"""Measure changed effect output and preserve the previous release; no similarity score."""
from pathlib import Path
from PIL import Image,ImageDraw
import json,hashlib,numpy as np,sys,cv2
from background_metric import occlusion_fraction
sys.stdout.reconfigure(encoding='utf-8')
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion');OLD=BASE/'round-06';OUT=BASE/'round-07'
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
release=json.loads((OLD/'release-manifest.json').read_text(encoding='utf-8'))
frozen=[]
for row in release['sourceFiles']:
    p=OLD/'source'/row['name'];assert sha(p)==row['sha256'];frozen.append(str(p))
for row in release['effectAssets']:
    p=OLD/'assets'/row['name'];assert sha(p)==row['sha256'];frozen.append(str(p))
for row in release['deliverables']:
    p=OLD/row['path'];assert sha(p)==row['sha256'];frozen.append(str(p))
manifests=[json.loads((folder/'assets/effect-assets.json').read_text(encoding='utf-8')) for folder in [OLD,OUT]]
def tiles(folder,meta):
    atlas=np.array(Image.open(folder/'assets'/meta['file']));w,h=meta['cell'];return [atlas[(i//meta['columns'])*h:(i//meta['columns']+1)*h,(i%meta['columns'])*w:(i%meta['columns']+1)*w] for i in range(meta['count'])]
foil=[];mattes=[]
for folder,m in zip([OLD,OUT],manifests):
    ft=tiles(folder,m['sequences']['foil']);foil.append([{'t':t,'visibleAlphaPixels':int((z[:,:,3]>32).sum()),'blackSupportPixels':int(((z[:,:,0]<8)&(z[:,:,3]>32)).sum()),'reflectionPixels':int(((z[:,:,0]>60)&(z[:,:,3]>32)).sum())} for t,z in zip(m['sequences']['foil']['times'],ft)])
    mattes.append([float((z[:,:,3]>127).mean()) for z in tiles(folder,m['sequences']['invasion'])])
white=tiles(OUT,manifests[1]['sequences']['whiteMotion']);changes=[]
for i,(a,b) in enumerate(zip(white,white[1:])):
    changes.append({'from':12.6+i/30,'to':12.6+(i+1)/30,'changedAlphaPixels':int((a[:,:,3]!=b[:,:,3]).sum()),'absoluteAlphaDifference':int(np.abs(a[:,:,3].astype(int)-b[:,:,3].astype(int)).sum())})
field=manifests[1]['densityField']['frames'];vectors=np.array([[p[4],p[5]] for row in field if 12.6<=row['t']<=13.1 for p in row['points']]);speed=np.linalg.norm(vectors,axis=1)
proof={'previousReleaseFilesUnchanged':len(frozen),'foil':{'round06':foil[0],'round07':foil[1]},'matteMaxAbsoluteCoverageDelta':float(np.max(np.abs(np.array(mattes[0])-mattes[1]))),'endingWhiteAlphaTemporalDifferences':changes,'finePointLK':{'endingPoints':len(vectors),'supported':int((speed>0).sum()),'supportedSpeedPxPerSecP50P90P99':np.percentile(speed[speed>0],[50,90,99]).tolist(),'note':'Low tracking support. The principal ending bokeh uses measured 30fps alpha frames instead of LK extrapolation.'},'unchangedAssets':[row['name'] for row in release['effectAssets'] if (OUT/'assets'/row['name']).exists() and sha(OUT/'assets'/row['name'])==row['sha256']]}
islands={}
for folder,m in zip([OLD,OUT],manifests):
    rows=[]
    for t,z in zip(m['sequences']['invasion']['times'],tiles(folder,m['sequences']['invasion'])):
        if not 12<=t<=12.6:continue
        _,_,stats,_=cv2.connectedComponentsWithStats((z[:,:,3]<128).astype(np.uint8),8)
        rows.append({'t':t,'islandsAtLeast100px':int(sum(s[4]>=100 for s in stats[1:])),'areas':sorted([int(s[4]) for s in stats[1:] if s[4]>=100],reverse=True)})
    islands[folder.name]=rows
proof['visibleIslandComponents']=islands
proof['inputProvenance']=manifests[1]['inputs']
inputChecks={}
for key,row in manifests[1]['inputs'].items():
    original=Path(row['sourcePath']);output=OUT/'assets'/f'base-{key}.png'
    assert sha(original)==row['originalSHA256'] and sha(output)==row['outputSHA256']
    originalPixels=np.array(Image.open(original).convert('RGBA'));outputPixels=np.array(Image.open(output).convert('RGBA'))
    assert originalPixels.shape==outputPixels.shape and np.array_equal(originalPixels[:,:,3],outputPixels[:,:,3])
    inputChecks[key]={'actualOriginalSHA256':sha(original),'actualOutputSHA256':sha(output),'exactAlphaMatch':True,'size':list(Image.open(original).size)}
assert inputChecks['b']['actualOriginalSHA256']=='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9'
proof['independentInputChecks']=inputChecks
proof['perceptualCameraGain']={'roll':1.35,'scaleDeviationFromOne':1.35,'translation':1.30,'note':'Perceptual adjustment to measured 2D lookup; not a new measured motion estimate'}
proof['perceptualProjectiveAdjustment']={'yawLimit':.085,'pitchLimit':.075,'source':'measured affine angle and translation used as drivers','measured3DMotion':False}
proof['backgroundOnlyDissolve']={'brightnessAt115':1.,'brightnessAt124':.5,'brightnessAt127':.21,'contrastAt127':.68,'particlesAndGlyphAffected':False}
control=np.full((20,20),100.,dtype=float);t=12.4
x=np.clip((t-11.5)/1.2,0,1);contrast=1-.32*x*x*(3-2*x)
expected=(control*contrast+.045*255*(1-contrast))*.5
assert occlusion_fraction(expected,control,t)==0 and occlusion_fraction(np.zeros_like(control),control,t)==1
proof['occlusionMeterCalibration']={'dimmedButUnoccluded':0.,'fullyOccluded':1.,'brightnessSeparatedFromOcclusion':True}
(OUT/'effect-revision-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
# Full-size alpha inspection on black, with unique times rather than rounded 0.1s labels.
indices=[0,3,6,9,12,15];sheet=Image.new('RGB',(1440,568*3),(15,15,15));d=ImageDraw.Draw(sheet)
for j,i in enumerate(indices):
    tile=Image.fromarray(white[i]);bg=Image.new('RGBA',(720,540),(0,0,0,255));bg.alpha_composite(tile);x=j%2*720;y=j//2*568;sheet.paste(bg.convert('RGB'),(x,y+28));d.text((x+8,y+7),f'#{i} {12.6+i/30:.3f}s white effect only 720x540',fill='white')
sheet.save(OUT/'ending-white-alpha-on-black.png')
print('previous release unchanged:',len(frozen),'files; max matte coverage delta:',proof['matteMaxAbsoluteCoverageDelta'],'; ending alpha changed intervals:',sum(c['changedAlphaPixels']>0 for c in changes),'/',len(changes))
