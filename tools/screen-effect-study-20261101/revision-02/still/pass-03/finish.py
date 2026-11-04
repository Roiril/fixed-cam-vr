from pathlib import Path
import hashlib,json,shutil
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still')
OUT=ROOT/'pass-03'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in [ROOT,ROOT/'pass-02']:
    frozen=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'))
    assert all(sha(folder/name)==digest for name,digest in frozen.items())
raw=np.array(Image.open(OUT/'base-B.png'));new=np.array(Image.open(OUT/'B-authored.png'));report=[]
for center,normal in [((815,267),(.889,.458)),((850,202),(.889,.458)),((906,133),(0,1))]:
    u=np.arange(-10,10.01,.25,dtype=np.float32);mx=(center[0]+u*normal[0])[None,:];my=(center[1]+u*normal[1])[None,:]
    profiles=[]
    for image in [raw,new]:
        values=cv2.remap(image[:,:,:3].astype(np.float32)/255,mx,my,cv2.INTER_LINEAR)[0]@np.array([.2126,.7152,.0722],np.float32)
        threshold=(values.min()+values.max())*.5
        profiles.append(dict(contrast=float(values.max()-values.min()),dark_FWHM_px=float((values<threshold).sum()*.25),min=float(values.min()),max=float(values.max())))
    assert profiles[0]==profiles[1]
    report.append(dict(center=center,normal=normal,sampling_step_px=.25,scope='manual normal profile; profile equality follows whole cable ROI RGBA equality',before=profiles[0],after=profiles[1]))
(OUT/'cable-profile-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
for name in ['inspection.md','finish.py']:
    shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
manifest=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));manifest['cable_profile_verification']=report
manifest['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()}
(OUT/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
files={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'}
(OUT/'delivery-sha256.json').write_text(json.dumps(files,indent=2),encoding='utf-8')
print('freeze files',len(files),'manifest',sha(OUT/'manifest.json'),'output',sha(OUT/'B-authored.png'))
print(json.dumps(report,indent=2))
