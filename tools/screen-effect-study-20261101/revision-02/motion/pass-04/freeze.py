from pathlib import Path
import hashlib,json,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion');OUT=BASE/'pass-04';P3=BASE/'pass-03'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
v=json.loads((P3/'verification.json').read_text(encoding='utf-8'));assert v['videoRendered'] and v['frameCount']==469
experiment={'status':'rejected-experiment-frozen','reason':'Parent viewed actual8.5 and rejected excessive near-depth stretch, sawtooth folds and wall steps. No additional parameter search or final comparison/finishing added.','video':{'path':str(P3/'motion-revision.mp4'),'sha256':sha(P3/'motion-revision.mp4')},'html':{'path':str(P3/'preview.html'),'sha256':sha(P3/'preview.html')},'verification':v,'next':'pass04 superposes measured global2D similarity after calibrated local world deformation; smoke/foreground unchanged'}
(P3/'experiment-record.json').write_text(json.dumps(experiment,ensure_ascii=False,indent=2),encoding='utf-8')
frozen=[]
for root in [H.parent/'pass-02',BASE/'pass-02',H.parent/'pass-03',P3]:
    frozen.extend({'path':str(p),'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted(root.rglob('*')) if p.is_file())
(OUT/'prior-passes-freeze.json').write_text(json.dumps(frozen,ensure_ascii=False,indent=2),encoding='utf-8')
previous=json.loads((BASE/'pass-02/pass-01-freeze.json').read_text(encoding='utf-8'))
for r in previous+frozen:assert sha(Path(r['path']))==r['sha256'],r['path']
print(json.dumps({'priorFrozenFiles':len(frozen),'pass01FrozenFiles':len(previous),'pass03ExperimentVideoSHA':experiment['video']['sha256']}))
