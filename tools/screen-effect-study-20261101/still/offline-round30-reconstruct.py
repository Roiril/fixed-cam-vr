"""Round30: bounded shock reconstruction, continuous scan displacement, signed tails."""
from pathlib import Path
import argparse, base64, hashlib, html, json, shutil, sys
sys.dont_write_bytecode = True
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding='utf-8')
cv2.setNumThreads(1)
HERE = Path(__file__).resolve().parent
SOURCE = Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-11/base-B.png')
DEFAULT = SOURCE.parent.parent / 'round-30'
EXPECTED = 'db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9'
CSS = Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css')
WEIGHTS = np.array([.2126, .7152, .0722])
PROFILES = ('proposal', 'denoised')
CROPS = {'floor': (680, 740, 1100, 900), 'edge': (600, 100, 880, 650),
         'flat': (180, 250, 500, 500), 'black': (820, 450, 880, 530)}
PARAMS = {'shock_iterations': 4, 'shock_dt': .15, 'gaussian_sigma': .8,
          'laplacian_ksize': 1, 'gradient': 'central difference / 2; reflected boundary',
          'shock_clamp': 'fixed input Y0 3x3 min/max after optional bilateral',
          'bilateral': {'diameter': 5, 'sigmaColor': .035, 'sigmaSpace': 1.2},
          'scan_seed': 300023, 'grid_spacing_x_px': 128, 'AR_y': .25,
          'AR_innovation_scale': float(np.sqrt(1 - .25 ** 2)), 'scan_preclip_RMS_px': 2,
          'scan_clip_px': [-4, 4], 'remap': 'INTER_LINEAR / BORDER_REPLICATE / same integer y',
          'tails': [[0, 1.15], [-3, -.25], [-8, .10]],
          'encoded_RGB_luma_weights': WEIGHTS.tolist(), 'alpha_extension_valid_threshold': 250,
          'protection': 'original alpha, hidden RGB, inner 2px exact; smoothstep fade 2..20px',
          'opaque_background_RGB': [18, 18, 18]}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def u8(a):
    return np.clip(np.rint(a * 255), 0, 255).astype(np.uint8)


def extend_alpha(raw):
    valid = raw[:, :, 3] >= 250
    if not valid.any():
        valid = raw[:, :, 3] > 0
    if not valid.any():
        raise ValueError('Input needs at least one visible pixel')
    _, labels = cv2.distanceTransformWithLabels((~valid).astype(np.uint8), cv2.DIST_L2,
                                               cv2.DIST_MASK_5, labelType=cv2.DIST_LABEL_PIXEL)
    lut = np.zeros((int(labels.max()) + 1, 3), np.uint8)
    lut[labels[valid]] = raw[:, :, :3][valid]
    rgb = lut[labels]
    rgb[valid] = raw[:, :, :3][valid]
    return rgb.astype(np.float64) / 255


def displacement(height, width):
    rng = np.random.default_rng(300023)
    positions = np.arange(0, width + 128, 128)
    noise = rng.standard_normal((height, len(positions)))
    grid = np.empty_like(noise)
    grid[0] = noise[0]
    for y in range(1, height):
        grid[y] = .25 * grid[y - 1] + np.sqrt(1 - .25 ** 2) * noise[y]
    delta = np.stack([np.interp(np.arange(width), positions, row) for row in grid])
    delta *= 2 / np.sqrt(np.mean(delta ** 2))
    return np.clip(delta, -4, 4).astype(np.float32)


def shock(y0):
    kernel = np.ones((3, 3), np.uint8)
    low = cv2.erode(y0, kernel, borderType=cv2.BORDER_REFLECT_101)
    high = cv2.dilate(y0, kernel, borderType=cv2.BORDER_REFLECT_101)
    current = y0.copy()
    violation = []
    for _ in range(4):
        smooth = cv2.GaussianBlur(current, (0, 0), .8, borderType=cv2.BORDER_REFLECT_101)
        lap = cv2.Laplacian(smooth, cv2.CV_64F, ksize=1, borderType=cv2.BORDER_REFLECT_101)
        p = np.pad(current, 1, mode='reflect')
        dx = (p[1:-1, 2:] - p[1:-1, :-2]) * .5
        dy = (p[2:, 1:-1] - p[:-2, 1:-1]) * .5
        current = np.clip(current - .15 * np.sign(lap) * np.sqrt(dx * dx + dy * dy), low, high)
        violation.append(float(max(np.max(low - current), np.max(current - high), 0)))
    return current, violation


def protect(raw, rgb):
    visible = raw[:, :, 3] > 0
    distance = cv2.distanceTransform(visible.astype(np.uint8), cv2.DIST_L2, 5)
    t = np.clip((distance - 2) / 18, 0, 1)
    fade = t * t * (3 - 2 * t)
    original = raw[:, :, :3].astype(np.float64) / 255
    out = raw.copy()
    out[:, :, :3] = u8(original + (rgb - original) * fade[:, :, None])
    out[~visible] = raw[~visible]
    boundary = visible & (distance <= 2)
    assert np.array_equal(out[:, :, 3], raw[:, :, 3])
    assert np.array_equal(out[~visible], raw[~visible])
    assert np.array_equal(out[boundary], raw[boundary])
    return out, int(boundary.sum())


def pipeline(raw, profile, delta=None, enabled=True):
    if not enabled:
        return {'final': raw.copy()}
    rgb = extend_alpha(raw)
    raw_y = rgb @ WEIGHTS
    y0 = raw_y.copy()
    if profile == 'denoised':
        y0 = cv2.bilateralFilter(y0.astype(np.float32), 5, .035, 1.2,
                                 borderType=cv2.BORDER_REFLECT_101).astype(np.float64)
    sharp, bounds = shock(y0)
    h, w = raw_y.shape
    if delta is None:
        delta = displacement(h, w)
    x, y = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    warped = cv2.remap(sharp, x + delta, y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    lag3 = warped[:, np.clip(np.arange(w) - 3, 0, w - 1)]
    lag8 = warped[:, np.clip(np.arange(w) - 8, 0, w - 1)]
    tails = np.clip(1.15 * warped - .25 * lag3 + .10 * lag8, 0, 1)
    stages = {'raw': raw.copy()}
    for name, value in [('shock', sharp), ('warped', warped), ('tail', tails)]:
        stages[name] = protect(raw, raw[:, :, :3] / 255 + (value - raw_y)[:, :, None])[0]
    final, boundary_count = protect(raw, raw[:, :, :3] / 255 + (tails - raw_y)[:, :, None])
    return {'final': final, 'stages': stages, 'Y': {'raw': raw_y, 'shock': sharp, 'warped': warped, 'tail': tails},
            'shock_clamp_violation_by_iteration': bounds, 'boundary_count': boundary_count,
            'delta': delta, 'pre_shock': y0}


def opaque(a):
    bg = Image.new('RGBA', (a.shape[1], a.shape[0]), (18, 18, 18, 255))
    bg.alpha_composite(Image.fromarray(a))
    return bg.convert('RGB')


def pair(before, after, box, path, before_label, after_label):
    tiles = [before.crop(box), after.crop(box)]
    w, h = tiles[0].size
    panel = max(w, 320)
    canvas = Image.new('RGB', (panel * 2, h + 48), 'white')
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc', 16)
    for i, (tile, label) in enumerate(zip(tiles, [before_label, after_label])):
        canvas.paste(tile, (panel * i, 48))
        draw.text((panel * i + 8, 8), label, fill=(26, 26, 26), font=font)
    canvas.save(path)


def synthetic(out):
    folder = out / 'calibration'
    folder.mkdir(exist_ok=True)
    h, w = 128, 384
    delta = displacement(h, w)
    report = {}
    for profile in PROFILES:
        stats = {'flat': {}, 'steps': {}}
        for level in [.2, .4, .7]:
            raw = np.full((h, w, 4), 255, np.uint8)
            raw[:, :, :3] = round(level * 255)
            result = pipeline(raw, profile, delta)
            changed = int(np.any(result['final'] != raw, axis=2).sum())
            error = max(float(np.max(np.abs(v - result['Y']['raw']))) for v in result['Y'].values())
            assert changed == 0 and error < 1e-6
            stats['flat'][str(level)] = {'changed_pixels': changed, 'maximum_float_error': error}
        steps = {}
        for sign in ['positive', 'negative']:
            raw = np.full((h, w, 4), 255, np.uint8)
            levels = np.where(np.arange(w) < w // 2, 51, 179)
            if sign == 'negative':
                levels = 230 - levels
            raw[:, :, :3] = levels[None, :, None]
            result = pipeline(raw, profile, delta)
            steps[sign] = result
            tail = result['Y']['tail']
            y0 = result['Y']['raw']
            # The signed tails may overshoot; isolated changes away from the edge are forbidden.
            far = np.abs(np.arange(w) - w // 2) > 18
            outside_error = float(np.max(np.abs(tail[:, far] - y0[:, far])))
            transition = result['Y']['warped'] > (51 + 179) / 510
            edge_count = np.count_nonzero(np.diff(transition.astype(np.int8), axis=1), axis=1)
            assert outside_error < 1e-6 and np.all(edge_count == 1)
            assert max(result['shock_clamp_violation_by_iteration']) == 0
            stats['steps'][sign] = {'far_plateau_error': outside_error,
                                   'one_transition_per_row': bool(np.all(edge_count == 1)),
                                   'tail_min': float(tail.min()), 'tail_max': float(tail.max()),
                                   'clamp_violation': result['shock_clamp_violation_by_iteration']}
            for stage, a in result['stages'].items():
                opaque(a).save(folder / f'{profile}-{sign}-{stage}.png')
            pair(opaque(raw), opaque(result['final']), (0, 0, w, h),
                 folder / f'{profile}-{sign}-comparison.png', f'{sign} input', f'{profile} tail')
        complement = 230 / 255
        symmetry = float(np.max(np.abs(steps['positive']['Y']['tail'] + steps['negative']['Y']['tail'] - complement)))
        assert symmetry < 1e-6
        stats['positive_negative_complement_max_error'] = symmetry
        soft = np.full((h, w, 4), 255, np.uint8)
        ramp = cv2.GaussianBlur(np.where(np.arange(w)[None, :] < w // 2, 51., 179.), (0, 0), 2)
        soft[:, :, :3] = np.rint(ramp).astype(np.uint8)[:, :, None]
        smooth = pipeline(soft, profile, delta)
        lo, hi = 51 / 255, 179 / 255
        widths = {}
        for stage in ['raw', 'shock']:
            row = smooth['Y'][stage][h // 2]
            widths[stage] = int(np.sum((row > lo + .1 * (hi - lo)) & (row < lo + .9 * (hi - lo))))
        assert widths['shock'] <= widths['raw']
        stats['soft_step_10to90_width_px'] = widths
        pair(opaque(soft), opaque(smooth['stages']['shock']), (0, 0, w, h),
             folder / f'{profile}-soft-step-comparison.png', 'blurred step', f'{profile} shock')
        report[profile] = stats
    # Deliberately hostile hidden RGB must not enter visible derivatives or remapping.
    yy, xx = np.indices((h, w))
    raw = np.full((h, w, 4), 255, np.uint8)
    raw[:, :, :3] = np.where(xx[:, :, None] < w // 2, 70, 150)
    raw[:, :, 3] = np.clip((np.minimum.reduce([xx - 16, w - 17 - xx, yy - 16, h - 17 - yy])) * 64, 0, 255)
    hidden = raw[:, :, 3] == 0
    raw[hidden, :3] = [255, 0, 230]
    other = raw.copy()
    other[hidden, :3] = [0, 255, 0]
    result = pipeline(raw, 'proposal', delta)
    alternate = pipeline(other, 'proposal', delta)
    assert np.array_equal(result['final'][~hidden], alternate['final'][~hidden])
    assert np.array_equal(result['final'][hidden], raw[hidden])
    report['hidden_RGB_invariance'] = True
    report['synthetic_inner_boundary_count'] = result['boundary_count']
    pair(opaque(raw), opaque(result['final']), (0, 0, w, h), folder / 'alpha-comparison.png', 'hostile hidden RGB', 'proposal tail')
    return report


def preview(out):
    images = {}
    names = ['base-B-on-black.png']
    for profile in PROFILES:
        names += [f'B-{profile}-{stage}-on-black.png' for stage in ['shock', 'warped', 'tail']]
        names += [f'B-{profile}-comparison-{crop}.png' for crop in CROPS]
    for path in sorted((out / 'calibration').glob('*comparison.png')):
        names.append('calibration/' + path.name)
    for name in names:
        images[name] = 'data:image/png;base64,' + base64.b64encode((out / name).read_bytes()).decode()
    options = ''.join(f'<option value="{html.escape(n)}">{html.escape(n)}</option>' for n in names)
    page = '''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>R30 境界から再構成した画像</title><style>CSS
select,button{font:inherit;padding:var(--sp-2);background:var(--surface);color:var(--ink);max-width:100%}.controls{display:flex;flex-wrap:wrap;gap:var(--sp-2)}.image{max-width:1280px;margin:auto;padding:var(--sp-4)}img{display:block;width:100%;height:auto}.native{overflow:auto}.native img{width:auto;max-width:none}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><main><div class="wrap"><h1>境界から再構成した画像</h1><p class="meta">2026-11-01 / R30 / 入力B</p><p>境界を急峻にしてから、行内の連続したずれと正負の尾を加えています。denoised は最初に粒を抑えた対照です。</p><div class="controls"><select id="mode" aria-label="表示画像">OPTIONS</select><button id="native" aria-pressed="false">原寸で見る</button></div><p id="label" role="status"></p></div><div class="image" id="frame"><img id="image" alt="R30 段階別画像または原寸比較"></div><div class="wrap"><p>元画像の色を保つ輝度差で再構成しています。評価画像はRGB 18の背景へalpha合成しています。</p><p>参照の原因を特定した結果ではありません。採点は行っていません。静止画像だけの試作で、本編への統合と時間方向の検証は未実施です。</p></div></main><script>const images=IMAGES,mode=document.getElementById('mode');function show(){document.getElementById('image').src=images[mode.value];document.getElementById('label').textContent=mode.value}mode.onchange=show;document.getElementById('native').onclick=function(){const active=document.getElementById('frame').classList.toggle('native');this.setAttribute('aria-pressed',String(active));this.textContent=active?'幅に合わせる':'原寸で見る'};show();</script></html>'''
    (out / 'preview.html').write_text(page.replace('CSS', CSS.read_text(encoding='utf-8')).replace('OPTIONS', options).replace('IMAGES', json.dumps(images)), encoding='utf-8')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', type=Path, default=DEFAULT)
    args = ap.parse_args()
    out = args.out
    out.mkdir(parents=True, exist_ok=True)
    assert sha(SOURCE) == EXPECTED
    source_code_sha = sha(Path(__file__))
    raw = np.array(Image.open(SOURCE).convert('RGBA'))
    assert raw.shape == (1030, 1280, 4)
    for name in ['base-B.png', 'B-OFF.png']:
        shutil.copyfile(SOURCE, out / name)
        assert sha(out / name) == EXPECTED
    opaque(raw).save(out / 'base-B-on-black.png')
    shutil.copyfile(Path(__file__), out / Path(__file__).name)
    delta = displacement(*raw.shape[:2])
    np.save(out / 'displacement.npy', delta)
    report = {'source_SHA_unchanged': True, 'OFF_PNG_byte_exact': True,
              'delta_min_px': float(delta.min()), 'delta_max_px': float(delta.max()),
              'delta_RMS_px': float(np.sqrt(np.mean(delta ** 2))),
              'delta_repeat_exact': bool(np.array_equal(delta, displacement(*raw.shape[:2]))), 'candidates': {}}
    assert report['delta_repeat_exact'] and delta.min() >= -4 and delta.max() <= 4
    assert 1.8 < report['delta_RMS_px'] < 2.1
    for profile in PROFILES:
        result = pipeline(raw, profile, delta)
        again = pipeline(raw, profile, delta)
        final = result['final']
        assert np.array_equal(final, again['final'])
        assert np.array_equal(pipeline(raw, profile, enabled=False)['final'], raw)
        assert result['boundary_count'] == 7394
        assert max(result['shock_clamp_violation_by_iteration']) == 0
        for stage, image in result['stages'].items():
            Image.fromarray(image).save(out / f'B-{profile}-{stage}.png')
            opaque(image).save(out / f'B-{profile}-{stage}-on-black.png')
        Image.fromarray(final).save(out / f'B-{profile}.png')
        opaque(final).save(out / f'B-{profile}-on-black.png')
        opaque(final).save(out / f'B-{profile}-comparison-full.png')
        for crop, box in CROPS.items():
            opaque(final).crop(box).save(out / f'B-{profile}-crop-{crop}.png')
            pair(opaque(raw), opaque(final), box, out / f'B-{profile}-comparison-{crop}.png', 'base-B.png', f'{profile}.png')
        visible = raw[:, :, 3] > 0
        changed = np.any(final[:, :, :3] != raw[:, :, :3], axis=2)
        stats = {'alpha_exact': bool(np.array_equal(final[:, :, 3], raw[:, :, 3])),
                 'hidden_RGB_exact': bool(np.array_equal(final[~visible], raw[~visible])),
                 'inner_2px_exact_count': result['boundary_count'], 'repeat_exact': True,
                 'OFF_exact': True, 'shock_clamp_violation': result['shock_clamp_violation_by_iteration'],
                 'changed_visible_fraction': float(changed[visible].mean()),
                 'visible_RGB_delta_RMS_byte': float(np.sqrt(np.mean((final[:, :, :3].astype(float)[visible] - raw[:, :, :3][visible]) ** 2))),
                 'rgba_sha256': sha(out / f'B-{profile}.png'),
                 'opaque_sha256': sha(out / f'B-{profile}-on-black.png')}
        report['candidates'][profile] = stats
        print(profile, json.dumps(stats), flush=True)
    report['calibration'] = synthetic(out)
    assert sha(SOURCE) == EXPECTED
    manifest = {'round': 30, 'hypothesis': 'Reconstruct raw input boundaries instead of adding after stage12; reference cause is unproven.',
                'source': str(SOURCE), 'source_sha256': EXPECTED, 'source_code_sha256': source_code_sha,
                'parameters': PARAMS, 'profiles': {'proposal': 'specified pipeline', 'denoised': 'bilateral only before shock; otherwise identical'},
                'formula': ['Y=encodedRGB@[.2126,.7152,.0722]',
                            'Ynext=clamp(Y-.15*sign(Laplacian(Gaussian(Y,.8)))*sqrt(dx²+dy²),min3x3(Y0),max3x3(Y0)); 4 iterations',
                            'S(x,y)=Y4(x+delta(x,y),y)', 'O=clip(1.15*S(x)-.25*S(x-3)+.10*S(x-8),0,1)',
                            'RGBout=rawRGB+(O-rawY), then alpha-boundary fade and RGB clip'],
                'not_used': ['stage12', 'r12.faults', '23 atlas', '28 kernel', 'reference RGB transfer', 'blue color replacement'],
                'observations': ['Signed tails have expected opposite overshoots at step edges; far plateaus are unchanged.',
                                 'Shock is bounded by fixed 3x3 input extrema; output tails are separately clipped to 0..1.',
                                 'Opened both full outputs, all eight native floor/edge/flat/black comparisons, positive/negative step pairs, soft-step pairs and alpha comparison.',
                                 'Wall boundaries show row-varying lateral zigzags and floor lines shift sideways; the flat-wall grain is visibly reduced in denoised.',
                                 'Visible changes are measured; no aesthetic score is assigned.'],
                'limitations': ['Only input B and static calibration are tested; no temporal validation or Unity integration.',
                                'Encoded luma arithmetic is not linear-light processing.',
                                'Scalar luma reconstruction can change hue at RGB clipping.',
                                'Delta has RMS 2px before clipping; postclip RMS is recorded.',
                                'Nearest valid RGB uses OpenCV approximate Euclidean label propagation, as in r19.'],
                'verification': report, 'opencv': cv2.__version__, 'numpy': np.__version__, 'crops_xyxy': CROPS}
    preview(out)
    page = (out / 'preview.html').read_text(encoding='utf-8')
    assert 'src="http' not in page and 'href="http' not in page and 'OPTIONS' not in page
    assert page.count('data:image/png;base64,') == 22
    report['preview_self_contained_embedded_images'] = 22
    (out / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    manifest['outputs'] = {str(p.relative_to(out)): sha(p) for p in sorted(out.rglob('*')) if p.is_file() and p.name != 'manifest.json'}
    (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'out': str(out), 'source_code_sha256': source_code_sha,
                      'delta_RMS_px': report['delta_RMS_px'], 'calibration': report['calibration']}, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
