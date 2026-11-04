from pathlib import Path
import hashlib,json,shutil
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');STILL=ROOT/'revision-02/still';OUT=STILL/'pass-06';ATLAS=ROOT/'still/round-23';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in [STILL,STILL/'pass-02',STILL/'pass-03',STILL/'pass-04',STILL/'pass-05']:
    frozen=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in frozen.items())
am=json.loads((ATLAS/'atlas-manifest.json').read_text(encoding='utf-8'))
for item in am['items'][:2]:assert sha(ATLAS/item['file'])==item['sha256']
for name in ['inspection.md','finish.py']:shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
for name in ['atlas-01.npz','atlas-02.npz','atlas-manifest.json']:shutil.copyfile(ATLAS/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));m['input_sha256']=sha(OUT/'base-B.png');m['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()};m['visual_observation']='Old05 rectangular and whole-y630 horizontal tone seams no longer visible in inspected full/native comparisons; gradual grain-density transition remains around authored board interiors; outside-board unsharp grain/dark crushing remains from04.';m['candidate_visual_acceptance']='unjudged';(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
hashes={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'};(OUT/'delivery-sha256.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
print('freeze files',len(hashes),'opaque',sha(OUT/'B-pass06-on-black.png'),'manifest',sha(OUT/'manifest.json'));print('old04',sha(STILL/'pass-04/B-pass04.png'),'old05',sha(STILL/'pass-05/B-pass05-on-black.png'))
