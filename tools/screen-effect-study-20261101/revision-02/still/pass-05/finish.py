from pathlib import Path
import hashlib,json,shutil
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=ROOT/'pass-05';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in [ROOT,ROOT/'pass-02',ROOT/'pass-03',ROOT/'pass-04']:
    hashes=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in hashes.items())
raw=np.array(Image.open(OUT/'base-B.png'));new=np.array(Image.open(OUT/'B-pass05.png'));a=raw[85:560,735:1010,:3].astype(np.float32)/255;b=new[85:560,735:1010,:3].astype(np.float32)/255;weights=np.array([.2126,.7152,.0722],np.float32);connections={}
for threshold in [.04,.08,.12,.20]:
    ma=(a@weights)<threshold;mb=(b@weights)<threshold;na=cv2.connectedComponents(ma.astype(np.uint8),connectivity=8)[0]-1;nb=cv2.connectedComponents(mb.astype(np.uint8),connectivity=8)[0]-1;assert np.array_equal(ma,mb) and na==nb;connections[str(threshold)]=dict(dark_mask_pixels_before=int(ma.sum()),dark_mask_pixels_after=int(mb.sum()),connected_components_before=int(na),connected_components_after=int(nb),dark_mask_exact=True,scope='whole nativecable ROI, not semantic cable segmentation')
(OUT/'cable-connectivity-verification.json').write_text(json.dumps(connections,indent=2),encoding='utf-8')
for name in ['finish.py','inspection.md']:shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));m['cable_connectivity']=connections;m['all_three_vertical_coverage_preserved']=False;m['visible_tone_seam']=True;m['visual_limitations']=['whole nativecable preservation removes cloth-right spatial effect:146 to0rows','rectangular tone seam near core bottom y560 and floor transition remains','wall highpass gain1.290 remains outside core'];m['candidate_all_requirements_met']=False;m['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()};(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
v=json.loads((OUT/'verification.json').read_text(encoding='utf-8'));v['visual_requirements_passed']=False;v['limitations']=m['visual_limitations'];(OUT/'verification.json').write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
hashes={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'};(OUT/'delivery-sha256.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
print('frozen',len(hashes),'opaque',sha(OUT/'B-pass05-on-black.png'),'manifest',sha(OUT/'manifest.json'));print('old04 rgba',sha(ROOT/'pass-04/B-pass04.png'),'old04 manifest',sha(ROOT/'pass-04/manifest.json'));print(json.dumps(connections,indent=2))
