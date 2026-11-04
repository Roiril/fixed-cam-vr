from pathlib import Path
import hashlib,json,shutil
HERE=Path(__file__).resolve().parent;STILL=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=STILL/'pass-08';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in [STILL,*[STILL/f'pass-{i:02d}' for i in range(2,8)]]:
    hashes=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in hashes.items())
for name in ['finish.py','inspection.md']:shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md');shutil.copyfile(STILL/'initial-reference-edge-native.png',OUT/'reference-white-window-edge-native.png')
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));m['reference_original_sha256']=sha(Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png'));m['reference_native_view_sha256']=sha(OUT/'reference-white-window-edge-native.png');m['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()};m['visual_acceptance']='unjudged; central-only width/fragments revision, no score or selection search';(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
hashes={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'};(OUT/'delivery-sha256.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
print('frozen',len(hashes),'opaque',sha(OUT/'B-pass08-on-black.png'),'manifest',sha(OUT/'manifest.json'));print('old07',sha(STILL/'pass-07/B-pass07-on-black.png'))
