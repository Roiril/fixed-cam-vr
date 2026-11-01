from pathlib import Path
import ast,hashlib,json,shutil
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');STILL=ROOT/'revision-02/still';OUT=STILL/'pass-07';ATLAS=ROOT/'still/round-23';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in [STILL,*[STILL/f'pass-{i:02d}' for i in range(2,7)]]:
    hashes=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in hashes.items())
def function(path,name):
    source=path.read_text(encoding='utf-8');node=next(n for n in ast.parse(source).body if isinstance(n,ast.FunctionDef) and n.name==name);return ast.get_source_segment(source,node)
assert function(HERE/'authored18.py','authored18')==function(HERE.parent/'pass-06/build.py','authored18')
am=json.loads((ATLAS/'atlas-manifest.json').read_text(encoding='utf-8'))
for item in am['items'][:2]:assert sha(ATLAS/item['file'])==item['sha256']
for name in ['inspection.md','finish.py']:shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
for name in ['atlas-01.npz','atlas-02.npz','atlas-manifest.json']:shutil.copyfile(ATLAS/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));m['input_sha256']=sha(STILL/'pass-03/base-B.png');m['authored18_function_source_exactly_equals06']=True;m['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()};m['real_FWHM_increase_count']=sum(r['FWHM_change']>0 for r in m['real_profiles']);m['visual_acceptance']='unjudged';m['observed_limitations']=['first real normal profile dark FWHM increases0.5px','threshold connectivity components change; no raw-RGB/core-mask exact claim carried over','weak black-object streaks remain untreated','coarse grain reduced on authored board interiors; low-frequency variation and edge-band grain remain'];(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
v=json.loads((OUT/'verification.json').read_text(encoding='utf-8'));v['real_FWHM_increase_count']=m['real_FWHM_increase_count'];v['all_requirements_claimed_passed']=False;v['observed_limitations']=m['observed_limitations'];(OUT/'verification.json').write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
hashes={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'};(OUT/'delivery-sha256.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
print('frozen',len(hashes),'opaque',sha(OUT/'B-pass07-on-black.png'),'manifest',sha(OUT/'manifest.json'));print('old06',sha(STILL/'pass-06/B-pass06-on-black.png'))
