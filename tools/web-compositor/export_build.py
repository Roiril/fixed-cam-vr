"""演出と使用素材を StreamingAssets/show へ検証済みで焼き込む。"""
import copy
import hashlib
import json
import os
import shutil
import tempfile
import time
from urllib.parse import quote, unquote

import operations

POLICY = 'baked-only-v1'


def local_url_dirs(root):
    return {prefix: os.path.join(root, *parts) for prefix, parts in {
        '/masks/': ('masks',), '/captures/': ('captures',),
        '/recordings/': ('recordings',), '/static-inputs/': ('static-inputs',),
        '/audio/': ('audio',), '/testassets/': ('testassets',),
        '/eyejack/norm/': ('eyejack', 'norm'),
    }.items()}


def resolve_local_asset(url, dirs):
    if not isinstance(url, str) or not url.startswith('/') or url.startswith('//'):
        return None
    clean = url.split('?', 1)[0].split('#', 1)[0]
    for prefix, base in dirs.items():
        if clean.startswith(prefix):
            name = unquote(clean[len(prefix):])
            fp = os.path.abspath(os.path.join(base, name))
            try:
                if os.path.commonpath([fp, os.path.abspath(base)]) != os.path.abspath(base):
                    return None
            except ValueError:
                return None
            return fp if os.path.isfile(fp) else None
    return None


def step_asset_slots(show):
    return [step for seg in ((show.get('timeline') or {}).get('segments') or [])
            for take in (seg.get('takes') or []) for step in (take.get('steps') or [])
            if step.get('assetUrl')]


def referenced_cue_ids(show):
    ids = set()
    timeline = show.get('timeline') or {}
    segments = timeline.get('segments') or []
    if not (timeline.get('schema') == 3 and segments):
        for e in ((show.get('schedule') or {}).get('entries') or []):
            if e.get('cueId'):
                ids.add(e['cueId'])
    for seg in segments:
        for take in (seg.get('takes') or []):
            for step in (take.get('steps') or []):
                for key in ('cueId', 'overlay2CueId'):
                    if step.get(key):
                        ids.add(step[key])
        for cue in (seg.get('cues') or []):
            if cue.get('cueId'):
                ids.add(cue['cueId'])
        ins = seg.get('insert') or {}
        if seg.get('hasInsert') and ins.get('cueId'):
            ids.add(ins['cueId'])
    ac = (show.get('control') or {}).get('activeCue')
    if ac:
        ids.add(ac)
    return ids


def _track_ids(show):
    ids = set()
    bgm = show.get('bgm') or {}
    if bgm.get('action') == 'play' and bgm.get('trackId'):
        ids.add(bgm['trackId'])
    for seg in ((show.get('timeline') or {}).get('segments') or []):
        bgm = seg.get('bgm') or {}
        if seg.get('hasBgm') and bgm.get('action') == 'play' and bgm.get('trackId'):
            ids.add(bgm['trackId'])
        for take in seg.get('takes') or []:
            for directive in [take, *(take.get('steps') or [])]:
                bgm = directive.get('bgm') or {}
                if directive.get('hasBgm') and bgm.get('action') == 'play' and bgm.get('trackId'):
                    ids.add(bgm['trackId'])
    return ids


def _sha_file(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def prepare(show, dirs):
    """書き込み前に参照を全解決する。失敗時は元の show を変更しない。"""
    baked = copy.deepcopy(show)
    operations.normalize_show(baked)
    cues = baked.get('cues') or []
    cue_ids = referenced_cue_ids(baked)
    tracks = baked.get('bgmTracks') or []
    track_ids = _track_ids(baked)
    missing_cues = sorted(cue_ids - {c.get('id') for c in cues})
    missing_tracks = sorted(track_ids - {t.get('id') for t in tracks})
    if ((baked.get('timeline') or {}).get('segments') or
            (baked.get('schedule') or {}).get('entries')):
        baked['cues'] = [c for c in cues if c.get('id') in cue_ids]
        baked['bgmTracks'] = [t for t in tracks if t.get('id') in track_ids]
    slots = {s.get('name'): s.get('url') for s in
             ((baked.get('control') or {}).get('slots') or []) if isinstance(s, dict)}
    refs = {}
    used_slots = set()
    errors = []

    def resolve(url):
        original = url
        seen = set()
        while isinstance(url, str) and url.startswith('slot://'):
            name = url[len('slot://'):]
            if name in seen or not slots.get(name):
                errors.append(f'未束縛 slot: {original}')
                return
            seen.add(name)
            used_slots.add(name)
            url = slots[name]
        fp = resolve_local_asset(url, dirs)
        if not fp:
            errors.append(f'素材を解決できません: {original}')
        else:
            refs[original] = fp

    for cue in baked.get('cues') or []:
        for key in ('maskUrl', 'sourceUrl'):
            if cue.get(key):
                resolve(cue[key])
    for track in baked.get('bgmTracks') or []:
        if track.get('url'):
            resolve(track['url'])
    for step in step_asset_slots(baked):
        resolve(step['assetUrl'])
    for url in ((baked.get('eyejack') or {}).get('photos') or []):
        if url:
            resolve(url)
    if missing_cues:
        errors.append('未定義 cue: ' + ', '.join(missing_cues))
    if missing_tracks:
        errors.append('未定義 BGM: ' + ', '.join(missing_tracks))
    if errors:
        raise ValueError('; '.join(dict.fromkeys(errors)))
    ctrl = baked.get('control') or {}
    if 'slots' in ctrl:
        ctrl['slots'] = [s for s in ctrl['slots'] if isinstance(s, dict)
                         and s.get('name') in used_slots]
    return baked, refs


def _without_metadata(value):
    if isinstance(value, dict):
        return {k: _without_metadata(v) for k, v in value.items()
                if k not in ('rev', 'assetMap')}
    if isinstance(value, list):
        return [_without_metadata(v) for v in value]
    return value


def content_id(show, assets):
    payload = {'show': _without_metadata(show), 'assets': assets}
    raw = json.dumps(payload, sort_keys=True, ensure_ascii=False,
                     separators=(',', ':')).encode('utf-8')
    return hashlib.sha256(raw).hexdigest()


def export_build(show, repo_root, dirs, out_dir=None):
    out_dir = os.path.abspath(out_dir or os.path.join(repo_root, 'Assets', 'StreamingAssets', 'show'))
    baked, refs = prepare(show, dirs)
    parent = os.path.dirname(out_dir)
    os.makedirs(parent, exist_ok=True)
    stage = tempfile.mkdtemp(prefix='.show-stage-', dir=parent)
    backup = None
    try:
        assets_dir = os.path.join(stage, 'assets')
        os.makedirs(assets_dir)
        names, source_names, copied, url_map, assets = set(), {}, [], {}, []
        for url, fp in refs.items():
            if fp not in source_names:
                base = os.path.basename(fp)
                stem, ext = os.path.splitext(base)
                name, i = base, 1
                while name in names:
                    name, i = f'{stem}_{i}{ext}', i + 1
                names.add(name)
                source_names[fp] = name
                dest = os.path.join(assets_dir, name)
                shutil.copy2(fp, dest)
                path = 'assets/' + name
                size = os.path.getsize(dest)
                digest = _sha_file(dest)
                if digest != _sha_file(fp):
                    raise ValueError(f'コピー中に素材が変わりました: {url}')
                assets.append({'path': path, 'sha256': digest, 'size': size})
                copied.append({'from': url, 'to': path, 'size': size})
            url_map[url] = 'sa://assets/' + quote(source_names[fp])
        def remap(url):
            return url_map.get(url, url)
        for cue in baked.get('cues') or []:
            for key in ('maskUrl', 'sourceUrl'):
                if cue.get(key):
                    cue[key] = remap(cue[key])
        for track in baked.get('bgmTracks') or []:
            if track.get('url'):
                track['url'] = remap(track['url'])
        for step in step_asset_slots(baked):
            step['assetUrl'] = remap(step['assetUrl'])
        eyejack = baked.get('eyejack') or {}
        if eyejack.get('photos'):
            eyejack['photos'] = [remap(u) for u in eyejack['photos'] if u]
        baked['assetMap'] = [{'from': k, 'to': v} for k, v in sorted(url_map.items())]
        ctrl = baked.get('control') or {}
        if 'slots' in ctrl:
            ctrl['slots'] = [{'name': s.get('name'), 'url': remap('slot://' + s.get('name', ''))}
                             for s in ctrl['slots'] if isinstance(s, dict) and s.get('name')]
        assets.sort(key=lambda a: a['path'])
        show_path = os.path.join(stage, 'show.json')
        with open(show_path, 'w', encoding='utf-8', newline='\n') as stream:
            json.dump(baked, stream, ensure_ascii=False, indent=2)
        manifest = {'schema': 1, 'policy': POLICY, 'contentId': content_id(baked, assets),
                    'configSha256': _sha_file(show_path), 'assets': assets}
        with open(os.path.join(stage, 'manifest.json'), 'w', encoding='utf-8', newline='\n') as stream:
            json.dump(manifest, stream, ensure_ascii=False, indent=2)
        with open(show_path, encoding='utf-8') as stream:
            written_show = json.load(stream)
        if (_sha_file(show_path) != manifest['configSha256']
                or content_id(written_show, assets) != manifest['contentId']
                or any(_sha_file(os.path.join(stage, *a['path'].split('/'))) != a['sha256']
                       for a in assets)):
            raise ValueError('一時同梱内容の検証に失敗しました')
        if os.path.exists(out_dir):
            backup = tempfile.mkdtemp(prefix='.show-backup-', dir=parent)
            os.rmdir(backup)
            os.replace(out_dir, backup)
        try:
            os.replace(stage, out_dir)
        except OSError:
            if backup:
                os.replace(backup, out_dir)
                backup = None
            raise
        if backup:
            shutil.rmtree(backup)
        total = sum(a['size'] for a in assets)
        hosts = [{'id': c.get('id', '?'), 'host': c.get('host', ''),
                  'port': c.get('port', 8080), 'pinned': bool(c.get('pinned'))}
                 for c in baked.get('cameras', [])]
        return {'ok': True, 'outDir': out_dir, 'showJson': os.path.join(out_dir, 'show.json'),
                'copied': copied, 'count': len(assets), 'totalBytes': total,
                'referencedCues': len(referenced_cue_ids(baked)), 'missingCues': [],
                'missingTracks': [], 'unresolvedAssets': [], 'contentId': manifest['contentId'],
                'exportedAt': time.strftime('%Y-%m-%d %H:%M:%S'),
                'showRev': baked.get('rev', 0),
                'timelineRev': (baked.get('timeline') or {}).get('rev', 0), 'hosts': hosts}
    finally:
        if os.path.isdir(stage):
            shutil.rmtree(stage)
