"""Verify round25 physical artifacts then freeze before waveform replacement round26."""
from pathlib import Path
import hashlib,json,shutil
HERE=Path(__file__).resolve().parent;OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-25')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    assert not (OUT/'round25-freeze.json').exists();m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));v=json.loads((OUT/'verification.json').read_text(encoding='utf-8'));assert all(sha(OUT/x['file'])==x['sha256'] for x in m['outputs'].values());assert all(x['repeat_exact'] and x['inner2px_exact']==7394 for x in v['outputs'].values())
    (OUT/'independent-evaluation.json').write_text(json.dumps({'source':'parent transmitted independent blind comparison','scores':{'zero':65,'partial35':71,'partial65':76},'feedback':'元は細い横線が重なりにじむ。候補は鋭い小片横ずれ・灰色縁取り。faultを抜くほど弱くなった。','self_scoring':False},ensure_ascii=False,indent=2),encoding='utf-8');sources=[HERE/n for n in ['offline-round25.py','verify-round25.py','prediction-round25.md','analysis-round25.md','finalize-round25.py']];snap=OUT/'source-snapshots'
    for p in sources:shutil.copyfile(p,snap/p.name)
    files={str(p.relative_to(OUT)):sha(p) for p in OUT.rglob('*') if p.is_file() and p.name!='artifact-index.json'};(OUT/'artifact-index.json').write_text(json.dumps({'files':files,'file_count':len(files),'not_frozen_yet':False},ensure_ascii=False,indent=2),encoding='utf-8');files={str(p.relative_to(OUT)):sha(p) for p in OUT.rglob('*') if p.is_file()};result={'state':'frozen','no_more_round25_writes':True,'file_count':len(files),'files':files,'repo_sources':{str(p):sha(p) for p in sources}};(OUT/'round25-freeze.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'frozen_files':len(files),'sources':len(sources)}))
if __name__=='__main__':main()
