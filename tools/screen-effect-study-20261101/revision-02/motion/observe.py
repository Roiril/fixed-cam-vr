"""Read frozen videos sequentially; preserve native pixels and decode timestamp evidence."""
from pathlib import Path
import subprocess, re, json, hashlib, sys
from PIL import Image, ImageDraw
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study')
OUT=ROOT/'revision-02/motion'; OUT.mkdir(parents=True,exist_ok=True)
sources={'reference':ROOT/'sources/screen-20261101-004528.mp4',
         'old-r07-b':ROOT/'motion/round-07/alternate-b/motion-prototype.mp4'}
selected=[0,10,20,*range(26,51),*range(110,133,2)]
manifest={'method':'Decode from beginning, showinfo before fps=10 and after select; match unchanged YUV checksums for actual source PTS. No input seek and no OpenCV seek.', 'frames':[]}
for key,source in sources.items():
    target=OUT/'evidence'/key;target.mkdir(parents=True,exist_ok=True)
    filt='showinfo,fps=10,select='+"'"+'+'.join(f'eq(n,{n})' for n in selected)+"'"+',showinfo'
    run=subprocess.run(['ffmpeg','-hide_banner','-loglevel','info','-i',str(source),'-vf',filt,'-fps_mode','passthrough','-y',str(target/'frame-%03d.png')],capture_output=True,encoding='utf-8',errors='replace')
    (target/'decode.log').write_text(run.stderr,encoding='utf-8');run.check_returncode()
    parsed=[]
    for line in run.stderr.splitlines():
        match=re.search(r'Parsed_showinfo_(\d+).*? n:\s*(\d+).*?pts_time:([\d.-]+).*?checksum:([A-F\d]+)',line)
        if match:parsed.append((int(match[1]),int(match[2]),float(match[3]),match[4]))
    origin=[r for r in parsed if r[0]==0];outputs=[r for r in parsed if r[0]!=0]
    assert len(outputs)==len(selected),(key,len(outputs),len(selected))
    for i,row in enumerate(outputs,1):
        matches=[r for r in origin if r[3]==row[3]]
        actual=min(matches,key=lambda x:abs(x[2]-row[2]))
        p=target/f'frame-{i:03d}.png'; im=Image.open(p)
        manifest['frames'].append({'source':key,'nominalTime':row[2],'actualTime':actual[2],'actualFrame':actual[1], 'path':str(p),'size':list(im.size),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
    manifest[key]={'source':str(source),'sha256':hashlib.sha256(source.read_bytes()).hexdigest()}
    thumbs=[]
    for row in [r for r in manifest['frames'] if r['source']==key]:
        im=Image.open(row['path']).convert('RGB');im.thumbnail((468,216))
        tile=Image.new('RGB',(468,244),'#e6e2d9');tile.paste(im,(0,26));ImageDraw.Draw(tile).text((6,6),f"{row['nominalTime']:.1f}s / actual {row['actualTime']:.5f}s",fill='black');thumbs.append(tile)
    sheet=Image.new('RGB',(468*5,244*((len(thumbs)+4)//5)),'#e6e2d9')
    for i,im in enumerate(thumbs):sheet.paste(im,(i%5*468,i//5*244))
    sheet.save(target/'index-only.png')
(OUT/'evidence/manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
assert manifest['reference']['sha256']=='2c6fd4039eedfbe4d8fd13fe8dc5b818ce355ba6a722accb285c4410f67e4fa8'
assert manifest['old-r07-b']['sha256']=='fc99f590045d482b99e5408ed4b6fe7596129199a26984e751d8b32fdd8b5601'
print(json.dumps({'frames':len(manifest['frames']),'sources':{k:v for k,v in manifest.items() if k not in ['frames','method']}},ensure_ascii=False))
