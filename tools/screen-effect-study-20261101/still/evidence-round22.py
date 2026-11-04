"""Camera-specific native evidence crops; these boxes never change filter parameters."""
from pathlib import Path
import argparse,json
from PIL import Image
from build import labelled_pair,sha
DEFAULT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-22')
ROIS={'A':{'floor':[480,790,800,920],'edge':[190,180,420,700],'flat':[130,250,190,470]},'B':{'floor':[680,740,1100,900],'edge':[600,100,880,650],'flat':[180,250,500,500]},'C':{'floor':[400,740,620,890],'edge':[300,180,640,650],'flat':[760,260,1050,480]}}

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);args=ap.parse_args();out=args.out;m=json.loads((out/'manifest.json').read_text(encoding='utf-8'));report={'evidence_script_sha256':sha(Path(__file__)),'regions_by_camera':ROIS,'filter_parameters_unchanged':True,'files':{}}
    for camera,regions in ROIS.items():
        if camera not in m['inputs']:continue
        base=out/f'base-{camera}.png';assert sha(base)==m['inputs'][camera]['sha256']
        for profile in m['profiles']:
            p=out/m['outputs'][camera+'-'+profile]['file'];assert sha(p)==m['outputs'][camera+'-'+profile]['sha256']
            for region,box in regions.items():
                name=f'{camera}-{profile}-scene-{region}.png';labelled_pair(Image.open(base),Image.open(p),tuple(box),out/name,p.name,base.name);report['files'][name]=sha(out/name)
    (out/'camera-specific-evidence.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('Saved',len(report['files']),'camera-specific native comparison crops')
if __name__=='__main__':main()
