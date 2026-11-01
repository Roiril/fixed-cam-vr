from pathlib import Path
H=Path(__file__).resolve().parent
for old,new in [('superposed-capture.mjs','coverage-capture.mjs'),('superposed-finalize.py','coverage-finalize.py'),('superposed-gpu-calibration.mjs','coverage-gpu-calibration.mjs')]:
 s=(H/old).read_text(encoding='utf-8').replace('revision-02/spatial/superposed','revision-02/spatial/coverage').replace("'superposed-preview.html'","'coverage-preview.html'").replace("'./renderer-superposed.js'","'./renderer-coverage.js'")
 (H/new).write_bytes(s.encode('utf-8'))
