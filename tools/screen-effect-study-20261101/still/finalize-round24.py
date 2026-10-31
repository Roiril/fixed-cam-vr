"""Verify and freeze round24 before round25; never mutate frozen23."""
from pathlib import Path
import hashlib,json,shutil
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-24')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    assert not (OUT/'round24-freeze.json').exists()
    m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));raw=np.array(Image.open(OUT/'base-B.png'));visible=raw[:,:,3]>0;boundary=visible&(cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5)<=2);checks={}
    for name,item in m['outputs'].items():
        p=OUT/item['file'];a=np.array(Image.open(p));assert sha(p)==item['sha256'];assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~visible],raw[~visible]);assert np.array_equal(a[boundary],raw[boundary]);assert item['repeat_exact'];checks[name]={'sha256':sha(p),'alpha_hidden_boundary_exact':True,'boundary_pixels':int(boundary.sum()),'repeat_exact':True}
    assert sha(OUT/'B-OFF.png')==sha(OUT/'base-B.png');assert sha(OUT/'analysis.md')==sha(HERE/'analysis-round24.md')
    f23=OUT.parent/'round-23';freeze=json.loads((f23/'round23-freeze.json').read_text(encoding='utf-8'));assert all(sha(f23/k)==v for k,v in freeze['files'].items());assert all(sha(Path(k))==v for k,v in freeze['repo_sources'].items())
    (OUT/'verification.json').write_text(json.dumps({'outputs':checks,'OFF_byte_exact':True,'round23_frozen_files_unchanged':len(freeze['files']),'round23_sources_unchanged':len(freeze['repo_sources']),'synthetic':m['synthetic_verification']},indent=2),encoding='utf-8')
    (OUT/'independent-evaluation.json').write_text(json.dumps({'source':'parent transmitted independent blind comparison','scores':{'soft':76,'standard':77,'strong':78},'feedback':'元は細い横線が重なりにじむ。候補は硬い矩形欠けが多い。細部white細線が暗い中間調へ沈む。','self_scoring':False},ensure_ascii=False,indent=2),encoding='utf-8')
    sources=[HERE/n for n in ['offline-round24.py','prediction-round24.md','analysis-round24.md','finalize-round24.py']];snap=OUT/'source-snapshots';snap.mkdir(exist_ok=True)
    for p in sources:shutil.copyfile(p,snap/p.name)
    files={str(p.relative_to(OUT)):sha(p) for p in OUT.rglob('*') if p.is_file()};result={'state':'frozen','no_more_round24_writes':True,'file_count':len(files),'files':files,'repo_sources':{str(p):sha(p) for p in sources}};(OUT/'round24-freeze.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'verified_outputs':len(checks),'boundary_pixels':int(boundary.sum()),'frozen_files':len(files)}))
if __name__=='__main__':main()
