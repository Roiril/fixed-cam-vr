"""Freeze submitted round26 media and transitive source hashes before round27."""
from pathlib import Path
import hashlib,json,shutil
HERE=Path(__file__).resolve().parent;OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-26')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    assert not (OUT/'round26-freeze.json').exists();m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'));assert all(sha(OUT/x['file'])==x['sha256'] for x in m['outputs'].values());sources=json.loads((OUT/'source-frozen.json').read_text(encoding='utf-8'))['files'];assert all(sha(Path(p))==v for p,v in sources.items());(OUT/'independent-evaluation.json').write_text(json.dumps({'source':'parent transmitted independent blind comparison','scores':{'soft':82,'standard':80,'full':76},'feedback':'参照は細い擦れが密でも白黒断片が締まる。候補は灰色にじみ。standard/fullは線が太い。','self_scoring':False},ensure_ascii=False,indent=2),encoding='utf-8');shutil.copyfile(Path(__file__),OUT/'source-snapshots'/Path(__file__).name);sources[str(Path(__file__).resolve())]=sha(Path(__file__));files={str(p.relative_to(OUT)):sha(p) for p in OUT.rglob('*') if p.is_file()};(OUT/'round26-freeze.json').write_text(json.dumps({'state':'frozen','no_more_round26_writes':True,'file_count':len(files),'files':files,'repo_sources':sources},ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'frozen_files':len(files),'sources':len(sources)}))
if __name__=='__main__':main()
