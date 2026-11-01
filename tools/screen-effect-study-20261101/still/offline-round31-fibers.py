"""Round31: surface input, coherent scan envelope and signed four-tap fibers."""
from pathlib import Path
import argparse, base64, importlib.util, json, shutil, sys
sys.dont_write_bytecode = True
import cv2
import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
cv2.setNumThreads(1)
HERE = Path(__file__).resolve().parent


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


r19 = load('round31_surface', 'offline-round19.py')
r30 = load('round31_utilities', 'offline-round30-reconstruct.py')
SOURCE, EXPECTED, WEIGHTS = r30.SOURCE, r30.EXPECTED, r30.WEIGHTS
DEFAULT = SOURCE.parent.parent / 'round-31'
FROZEN = SOURCE.parent.parent / 'round-30'
CROPS = r30.CROPS
PROFILES = {
    'calm': {'iterations': 4, 'clamp_window': 3, 'envelope': False, 'taps': [[0, 1.15], [-3, -.25], [-8, .10]]},
    'coherent': {'iterations': 8, 'clamp_window': 5, 'envelope': True, 'taps': [[0, 1.15], [-3, -.25], [-8, .10]]},
    'fibers': {'iterations': 8, 'clamp_window': 5, 'envelope': True, 'taps': [[0, 1.50], [-2, -.70], [-4, .30], [-8, -.10]]}}


def envelope(height, width):
    rng = np.random.default_rng(300024)
    grid = rng.uniform(.15, 1, (int(np.ceil(height / 24)), int(np.ceil(width / 128))))
    return cv2.resize(grid, (width, height), interpolation=cv2.INTER_LINEAR)


def shock(y0, profile):
    p = PROFILES[profile]
    kernel = np.ones((p['clamp_window'], p['clamp_window']), np.uint8)
    low = cv2.erode(y0, kernel, borderType=cv2.BORDER_REFLECT_101)
    high = cv2.dilate(y0, kernel, borderType=cv2.BORDER_REFLECT_101)
    current = y0.copy()
    violations = []
    for _ in range(p['iterations']):
        smooth = cv2.GaussianBlur(current, (0, 0), .8, borderType=cv2.BORDER_REFLECT_101)
        lap = cv2.Laplacian(smooth, cv2.CV_64F, ksize=1, borderType=cv2.BORDER_REFLECT_101)
        pad = np.pad(current, 1, mode='reflect')
        dx = (pad[1:-1, 2:] - pad[1:-1, :-2]) / 2
        dy = (pad[2:, 1:-1] - pad[:-2, 1:-1]) / 2
        current = np.clip(current - .15 * np.sign(lap) * np.sqrt(dx * dx + dy * dy), low, high)
        violations.append(float(max(np.max(low - current), np.max(current - high), 0)))
    return current, violations


def pipeline(raw, profile, surface_rgb=None, enabled=True):
    if not enabled:
        return {'final': raw.copy()}
    h, w = raw.shape[:2]
    if surface_rgb is None:
        surface_rgb = r19.surface(raw)
    raw_y = r30.extend_alpha(raw) @ WEIGHTS
    surface_y = surface_rgb.astype(np.float64) @ WEIGHTS
    sharp, violations = shock(surface_y, profile)
    delta = r30.displacement(h, w)
    env = envelope(h, w)
    if PROFILES[profile]['envelope']:
        delta = (delta * env).astype(np.float32)
    x, y = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    warped = cv2.remap(sharp, x + delta, y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    target = np.zeros_like(warped)
    for lag, coefficient in PROFILES[profile]['taps']:
        target += coefficient * warped[:, np.clip(np.arange(w) + lag, 0, w - 1)]
    target = np.clip(target, 0, 1)
    stages = {'raw': raw.copy()}
    values = {'raw': raw_y, 'surface': surface_y, 'shock': sharp, 'warped': warped, 'tail': target}
    count = None
    for stage, value in values.items():
        if stage != 'raw':
            stages[stage], count = r30.protect(raw, raw[:, :, :3] / 255 + (value - raw_y)[:, :, None])
    return {'final': stages['tail'], 'stages': stages, 'Y': values,
            'clamp_violations': violations, 'boundary_count': count, 'delta': delta, 'envelope': env}


def freeze_inventory():
    manifest = json.loads((FROZEN / 'manifest.json').read_text(encoding='utf-8'))
    assert len(manifest['outputs']) == 68
    assert all(r30.sha(FROZEN / name) == digest for name, digest in manifest['outputs'].items())
    return {**manifest['outputs'], 'manifest.json': r30.sha(FROZEN / 'manifest.json')}


def variance_response(raw, result):
    stats = {}
    for name in ['flat', 'floor', 'black']:
        x0, y0, x1, y1 = CROPS[name]
        q = {}
        for stage, a in result['stages'].items():
            v = np.array(r30.opaque(a))[y0:y1, x0:x1].astype(float) @ WEIGHTS / 255
            q[stage] = {'mean': float(v.mean()), 'variance': float(v.var()),
                        'horizontal_gradient_RMS': float(np.sqrt(np.mean(np.diff(v, axis=1) ** 2)))}
        before = raw[y0:y1, x0:x1, :3].astype(float)
        after = result['final'][y0:y1, x0:x1, :3].astype(float)
        q['changed_fraction'] = float(np.any(before != after, axis=2).mean())
        q['RGB_delta_RMS_byte'] = float(np.sqrt(np.mean((after - before) ** 2)))
        stats[name] = q
    return stats


def calibrate(out):
    folder = out / 'calibration'
    folder.mkdir(exist_ok=True)
    h, w = 128, 384
    report = {}
    for profile in PROFILES:
        stats = {'flat': {}, 'steps': {}}
        for level in [51, 102, 179]:
            raw = np.full((h, w, 4), 255, np.uint8)
            raw[:, :, :3] = level
            result = pipeline(raw, profile)
            changed = int(np.any(result['final'] != raw, axis=2).sum())
            assert changed == 0
            stats['flat'][str(level)] = {'changed_pixels': changed,
                'maximum_float_error': float(np.max(np.abs(result['Y']['tail'] - result['Y']['raw'])))}
            r30.opaque(raw).save(folder / f'{profile}-flat-{level}-input.png')
            r30.opaque(result['final']).save(folder / f'{profile}-flat-{level}-final.png')
        steps = {}
        for sign in ['positive', 'negative']:
            raw = np.full((h, w, 4), 255, np.uint8)
            levels = np.where(np.arange(w) < w // 2, 77, 153)
            if sign == 'negative':
                levels = 230 - levels
            raw[:, :, :3] = levels[None, :, None]
            result = pipeline(raw, profile)
            steps[sign] = result
            target = result['Y']['tail']
            warped = result['Y']['warped']
            residual = target - warped
            positive_core = int((residual > .01).sum())
            negative_core = int((residual < -.01).sum())
            far = np.abs(np.arange(w) - w // 2) > 18
            far_error = float(np.max(np.abs(target[:, far] - result['Y']['raw'][:, far])))
            transitions = np.count_nonzero(np.diff((warped > 115 / 255).astype(np.int8), axis=1), axis=1)
            assert np.all(transitions == 1) and positive_core > 0 and negative_core > 0 and far_error < 1e-6
            assert max(result['clamp_violations']) == 0
            stats['steps'][sign] = {'white_core_pixels': positive_core, 'dark_core_pixels': negative_core,
                'one_transition_per_row_before_tails': True, 'far_plateau_error': far_error,
                'tail_min': float(target.min()), 'tail_max': float(target.max()),
                'clamp_violation': result['clamp_violations']}
            for stage, a in result['stages'].items():
                r30.opaque(a).save(folder / f'{profile}-{sign}-{stage}.png')
            r30.pair(r30.opaque(raw), r30.opaque(result['final']), (0, 0, w, h),
                     folder / f'{profile}-{sign}-comparison.png', f'{sign} input', f'{profile} tail')
        symmetry = float(np.max(np.abs(steps['positive']['Y']['tail'] + steps['negative']['Y']['tail'] - 230 / 255)))
        assert symmetry < 1e-6
        positive_residual = steps['positive']['Y']['tail'] - steps['positive']['Y']['warped']
        negative_residual = steps['negative']['Y']['tail'] - steps['negative']['Y']['warped']
        core_error = float(np.max(np.abs(positive_residual + negative_residual)))
        assert core_error < 1e-6
        stats['positive_negative_complement_error'] = symmetry
        stats['opposite_signed_core_error'] = core_error
        raw = np.full((h, w, 4), 255, np.uint8)
        step = cv2.GaussianBlur(np.where(np.arange(w)[None, :] < w // 2, 77., 153.), (0, 0), 2)
        raw[:, :, :3] = np.rint(step).astype(np.uint8)[:, :, None]
        result = pipeline(raw, profile)
        lo, hi = 77 / 255, 153 / 255
        widths = {stage: int(np.count_nonzero((v[h // 2] > lo + .1 * (hi - lo)) &
                                             (v[h // 2] < lo + .9 * (hi - lo))))
                  for stage, v in result['Y'].items()}
        assert widths['shock'] <= widths['surface']
        stats['softstep_10to90_width_px'] = widths
        for stage, a in result['stages'].items():
            r30.opaque(a).save(folder / f'{profile}-softstep-{stage}.png')
        r30.pair(r30.opaque(raw), r30.opaque(result['final']), (0, 0, w, h),
                 folder / f'{profile}-softstep-comparison.png', 'blurred step', f'{profile} final')
        report[profile] = stats
    yy, xx = np.indices((h, w))
    raw = np.full((h, w, 4), 255, np.uint8)
    raw[:, :, :3] = np.where(xx[:, :, None] < w // 2, 77, 153)
    d = np.minimum.reduce([xx - 16, w - 17 - xx, yy - 16, h - 17 - yy])
    raw[:, :, 3] = np.clip(d * 64, 0, 255)
    hidden = raw[:, :, 3] == 0
    raw[hidden, :3] = [255, 0, 230]
    alternate = raw.copy()
    alternate[hidden, :3] = [0, 255, 0]
    report['hidden_RGB_invariance'] = {}
    for profile in PROFILES:
        result = pipeline(raw, profile)
        other = pipeline(alternate, profile)
        assert np.array_equal(result['final'][~hidden], other['final'][~hidden])
        assert np.array_equal(result['final'][hidden], raw[hidden])
        report['hidden_RGB_invariance'][profile] = True
        r30.pair(r30.opaque(raw), r30.opaque(result['final']), (0, 0, w, h),
                 folder / f'{profile}-alpha-comparison.png', 'hostile hidden RGB', f'{profile} final')
    return report


def preview(out):
    names = ['base-B-on-black.png']
    for profile in PROFILES:
        names += [f'B-{profile}-{s}-on-black.png' for s in ['surface', 'shock', 'warped', 'tail']]
        names += [f'B-{profile}-comparison-{c}.png' for c in CROPS]
    names += ['calibration/' + p.name for p in sorted((out / 'calibration').glob('*comparison.png'))]
    images = {name: 'data:image/png;base64,' + base64.b64encode((out / name).read_bytes()).decode() for name in names}
    options = ''.join(f'<option>{n}</option>' for n in names)
    page = '''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>R31 境界と明暗の尾を比較</title><style>CSS
select,button{font:inherit;padding:var(--sp-2);max-width:100%;color:var(--ink);background:var(--surface)}.controls{display:flex;flex-wrap:wrap;gap:var(--sp-2)}.image{max-width:1280px;margin:auto;padding:var(--sp-4)}img{display:block;width:100%;height:auto}.native{overflow:auto}.native img{width:auto;max-width:none}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><main><div class="wrap"><h1>境界と明暗の尾を比較</h1><p class="meta">2026-11-01 / R31 / 入力B</p><p>calm は粒を抑えた入力です。coherent は境界をさらに詰め、横ずれが弱い区間を加えています。fibers は明暗の尾を短い間隔で重ねています。</p><div class="controls"><select id="mode" aria-label="表示画像">OPTIONS</select><button id="native" aria-pressed="false">原寸で見る</button></div><p id="label" role="status"></p></div><div class="image" id="frame"><img id="image" alt="R31 段階別出力と原寸比較"></div><div class="wrap"><p>評価画像はRGB 18の背景へalpha合成しています。原寸比較の左は加工前です。</p><p>静止画像による3候補の比較です。参照の原因は未確定です。採点と本編への統合は行っていません。</p></div></main><script>const images=IMAGES,mode=document.getElementById('mode');function show(){document.getElementById('image').src=images[mode.value];document.getElementById('label').textContent=mode.value}mode.onchange=show;document.getElementById('native').onclick=function(){const active=document.getElementById('frame').classList.toggle('native');this.setAttribute('aria-pressed',String(active));this.textContent=active?'幅に合わせる':'原寸で見る'};show();</script></html>'''
    (out / 'preview.html').write_text(page.replace('CSS', r30.CSS.read_text(encoding='utf-8')).replace('OPTIONS', options).replace('IMAGES', json.dumps(images)), encoding='utf-8')
    return len(images)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', type=Path, default=DEFAULT)
    args = ap.parse_args()
    out = args.out
    out.mkdir(parents=True, exist_ok=True)
    frozen = freeze_inventory()
    dependencies = {name: r30.sha(HERE / name) for name in ['offline-round19.py', 'offline-round30-reconstruct.py', 'build.py']}
    assert r30.sha(SOURCE) == EXPECTED
    raw = np.array(Image.open(SOURCE).convert('RGBA'))
    assert raw.shape == (1030, 1280, 4)
    for name in ['base-B.png', 'B-OFF.png']:
        shutil.copyfile(SOURCE, out / name)
        assert r30.sha(out / name) == EXPECTED
    shutil.copyfile(Path(__file__), out / Path(__file__).name)
    r30.opaque(raw).save(out / 'base-B-on-black.png')
    surface = r19.surface(raw)
    assert np.array_equal(surface, r19.surface(raw))
    delta = r30.displacement(*raw.shape[:2])
    assert np.array_equal(delta, np.load(FROZEN / 'displacement.npy'))
    env = envelope(*raw.shape[:2])
    assert np.array_equal(env, envelope(*raw.shape[:2]))
    assert .15 <= env.min() <= env.max() <= 1
    np.save(out / 'source-displacement.npy', delta)
    np.save(out / 'envelope.npy', env)
    verification = {'OFF_PNG_byte_exact': True, 'input_SHA_unchanged': True,
        'surface_repeat_exact': True, 'delta_matches_R30': True, 'envelope_repeat_exact': True,
        'envelope_range': [float(env.min()), float(env.max())],
        'envelope_bilinear_grid_shape': [int(np.ceil(raw.shape[0] / 24)), int(np.ceil(raw.shape[1] / 128))],
        'source_delta_RMS_px': float(np.sqrt(np.mean(delta ** 2))), 'candidates': {}}
    visible = raw[:, :, 3] > 0
    for profile in PROFILES:
        result = pipeline(raw, profile, surface)
        again = pipeline(raw, profile, surface)
        assert np.array_equal(result['final'], again['final'])
        assert np.array_equal(pipeline(raw, profile, enabled=False)['final'], raw)
        assert result['boundary_count'] == 7394 and max(result['clamp_violations']) == 0
        assert np.all(np.abs(result['delta']) <= np.abs(delta) + 1e-7)
        for stage, image in result['stages'].items():
            Image.fromarray(image).save(out / f'B-{profile}-{stage}.png')
            r30.opaque(image).save(out / f'B-{profile}-{stage}-on-black.png')
        final = result['final']
        Image.fromarray(final).save(out / f'B-{profile}.png')
        r30.opaque(final).save(out / f'B-{profile}-on-black.png')
        for name, box in CROPS.items():
            r30.opaque(final).crop(box).save(out / f'B-{profile}-crop-{name}.png')
            r30.pair(r30.opaque(raw), r30.opaque(final), box,
                     out / f'B-{profile}-comparison-{name}.png', 'base-B.png', f'{profile}.png')
        stats = {'alpha_exact': bool(np.array_equal(final[:, :, 3], raw[:, :, 3])),
                 'hidden_RGB_exact': bool(np.array_equal(final[~visible], raw[~visible])),
                 'inner2px_exact_count': result['boundary_count'], 'repeat_exact': True, 'OFF_exact': True,
                 'clamp_violation': result['clamp_violations'],
                 'delta_range_px': [float(result['delta'].min()), float(result['delta'].max())],
                 'delta_RMS_px': float(np.sqrt(np.mean(result['delta'] ** 2))),
                 'changed_visible_fraction': float(np.any(final[:, :, :3] != raw[:, :, :3], axis=2)[visible].mean()),
                 'variance_response': variance_response(raw, result),
                 'RGBA_sha256': r30.sha(out / f'B-{profile}.png'),
                 'opaque_sha256': r30.sha(out / f'B-{profile}-on-black.png')}
        verification['candidates'][profile] = stats
        print(profile, json.dumps({k: v for k, v in stats.items() if k != 'variance_response'}), flush=True)
    verification['calibration'] = calibrate(out)
    verification['preview_embedded_images'] = preview(out)
    page = (out / 'preview.html').read_text(encoding='utf-8')
    assert page.count('data:image/png;base64,') == verification['preview_embedded_images']
    assert 'src="http' not in page and 'href="http' not in page and 'OPTIONS' not in page
    assert r30.sha(SOURCE) == EXPECTED
    assert all(r30.sha(HERE / name) == digest for name, digest in dependencies.items())
    assert all(r30.sha(FROZEN / name) == digest for name, digest in frozen.items())
    verification['R30_frozen_inventory'] = {'output_files_unchanged': 68, 'manifest_unchanged': True,
                                          'manifest_sha256': frozen['manifest.json']}
    (out / 'verification.json').write_text(json.dumps(verification, ensure_ascii=False, indent=2), encoding='utf-8')
    manifest = {'round': 31, 'source': str(SOURCE), 'source_sha256': EXPECTED,
        'source_code_sha256': r30.sha(Path(__file__)), 'dependency_sha256': dependencies,
        'surface': 'r19.surface(raw) only: nearest-valid RGB extension; NLM h5.5 template7 search21; bilateral d9 sigmaColor.05 sigmaSpace3; +20% residual clipped .03',
        'encoded_luma_weights': WEIGHTS.tolist(), 'profiles': PROFILES,
        'shock': {'dt': .15, 'gaussian_sigma': .8, 'laplacian_ksize': 1, 'gradient': 'central /2 reflected boundary',
                  'clamp': 'fixed min/max of initial surfaceY; window per profile'},
        'displacement': {'source': 'R30 seed300023, x-grid128, y-AR.25, preclip RMS2px, clip±4',
                         'envelope_seed': 300024, 'envelope_coarse_cell_px': [128, 24],
                         'envelope_uniform': [.15, 1], 'envelope_smoothing': 'bilinear resize only; no extra kernel',
                         'envelope_coarse_shape': verification['envelope_bilinear_grid_shape'],
                         'remap': 'INTER_LINEAR same integer y, BORDER_REPLICATE'},
        'reconstruction': 'rawRGB+(O-rawY); original alpha and hiddenRGB; inner2px exact; smoothstep fade2..20px; RGB clip',
        'opaque_background_RGB': [18, 18, 18], 'crops_xyxy': CROPS,
        'not_used': ['stage12', 'r12.faults', '23 atlas', '28 kernel', 'tone curve', 'gamma', 'color replacement', 'drawn floor mesh', 'new props'],
        'limitations': ['Static input B only; no temporal or Unity verification.',
                        'Encoded RGB arithmetic can clip per channel; scalar difference preserves raw chroma until clipping.',
                        'Surface, stronger shock and envelope changes are grouped between calm and coherent; they are not isolated separately.',
                        'No score or adoption decision is assigned.'],
        'verification': verification, 'opencv': cv2.__version__, 'numpy': np.__version__}
    manifest['outputs'] = {str(p.relative_to(out)): r30.sha(p) for p in sorted(out.rglob('*')) if p.is_file() and p.name != 'manifest.json'}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'out': str(out), 'source_code_sha256': manifest['source_code_sha256'],
                      'calibration': verification['calibration'], 'R30_frozen': verification['R30_frozen_inventory']}, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
