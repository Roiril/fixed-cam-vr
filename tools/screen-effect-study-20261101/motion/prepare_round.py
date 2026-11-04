"""Reproduce round-07 from frozen round-06, replacing only the scalar front."""
from pathlib import Path
import hashlib,json,subprocess,sys
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
if (BASE/'capture.lock').exists():raise RuntimeError('A capture is active; do not replace assets during rendering')
previous=BASE/'round-06';target=BASE/'round-07/assets';target.mkdir(parents=True,exist_ok=True)
release=json.loads((previous/'release-manifest.json').read_text(encoding='utf-8'))
for name in ['motion-registration.json','timestamp-calibration.json']:
    source=previous/name;destination=target.parent/name
    if not source.is_file():raise FileNotFoundError(source)
    destination.write_bytes(source.read_bytes());assert hashlib.sha256(destination.read_bytes()).hexdigest()==hashlib.sha256(source.read_bytes()).hexdigest()
for row in release['effectAssets']:
    original=previous/'assets'/row['name'];data=original.read_bytes()
    assert hashlib.sha256(data).hexdigest()==row['sha256'],original
    destination=target/row['name'];temporary=destination.with_name(destination.name+'.tmp');temporary.write_bytes(data);temporary.replace(destination)
    assert hashlib.sha256(destination.read_bytes()).hexdigest()==row['sha256']
subprocess.run([sys.executable,str(ROOT/'soften_front.py')],check=True)
print('round07 assets: frozen round06 inputs/foil/glyph/particles and softened round05 front')
