from pathlib import Path
import hashlib,json,shutil
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=ROOT/'pass-04'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
frozen_counts={}
for folder in [ROOT,ROOT/'pass-02',ROOT/'pass-03']:
    frozen=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in frozen.items());frozen_counts[folder.name]=len(frozen)
raw=np.array(Image.open(OUT/'base-B.png'));new=np.array(Image.open(OUT/'B-pass04.png'));report=[]
for center,normal in [((815,267),(.889,.458)),((850,202),(.889,.458)),((906,133),(0,1))]:
    u=np.arange(-10,10.01,.25,dtype=np.float32);mx=(center[0]+u*normal[0])[None,:];my=(center[1]+u*normal[1])[None,:];profiles=[]
    for image in [raw,new]:
        values=cv2.remap(image[:,:,:3].astype(np.float32)/255,mx,my,cv2.INTER_LINEAR)[0]@np.array([.2126,.7152,.0722],np.float32);threshold=(values.min()+values.max())*.5;profiles.append(dict(contrast=float(values.max()-values.min()),dark_FWHM_px=float((values<threshold).sum()*.25),min=float(values.min()),max=float(values.max())))
    report.append(dict(center=center,normal=normal,scope='manually placed normal profile; nearby background and other shapes may contribute',before=profiles[0],after=profiles[1],contrast_ratio=profiles[1]['contrast']/max(1e-12,profiles[0]['contrast']),FWHM_change=profiles[1]['dark_FWHM_px']-profiles[0]['dark_FWHM_px']))
(OUT/'real-cable-profiles.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
for name in ['inspection.md','finish.py']:shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
real_failures=[r for r in report if r['FWHM_change']>0]
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));m['real_cable_profiles']=report;m['real_cable_FWHM_increase_count']=len(real_failures);m['all_numeric_checks_passed']=not bool(real_failures or m['synthetic_FWHM_failures']);m['prior_frozen_artifact_counts_verified']=frozen_counts;m['visual_limitation']='Wall/floor/cable highpass increased; grain amplification remains. Real manually sampled dark-profile FWHM increases at two locations and contrast decreases. Synthetic FWHM pass is not a visual acceptance.';m['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()};(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
verification=json.loads((OUT/'verification.json').read_text(encoding='utf-8'));verification['real_cable_FWHM_increase_count']=len(real_failures);verification['all_numeric_checks_passed']=m['all_numeric_checks_passed'];verification['scope_of_real_profile']='manual profile includes background and other shapes; increase is reported, not silently passed';(OUT/'verification.json').write_text(json.dumps(verification,indent=2),encoding='utf-8')
files={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'};(OUT/'delivery-sha256.json').write_text(json.dumps(files,indent=2),encoding='utf-8')
print('freeze files',len(files),'manifest',sha(OUT/'manifest.json'),'candidate',sha(OUT/'B-pass04.png'));print(json.dumps(report,indent=2))
