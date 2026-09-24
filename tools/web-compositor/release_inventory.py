"""準備済み・同梱・APK・Quest の内容一致を読む。HTTP 経路では書き込まない。"""
import hashlib
import json
import os
import re
import threading
import time
import zipfile
from urllib.parse import quote, unquote

import export_build
import operations

_lock = threading.Lock()
_cache_key = None
_cache_value = None
_apk_lock = threading.Lock()
_apk_cache = {}


def _stage(status, content_id='', title='', action=''):
    return {'status': status, 'contentId': content_id, 'title': title, 'action': action}


def _planned(show, dirs):
    baked, refs = export_build.prepare(show, dirs)
    names, by_source, url_map, assets = set(), {}, {}, []
    for url, fp in refs.items():
        if fp not in by_source:
            base = os.path.basename(fp)
            stem, ext = os.path.splitext(base)
            name, i = base, 1
            while name in names:
                name, i = f'{stem}_{i}{ext}', i + 1
            names.add(name)
            by_source[fp] = name
            assets.append({'path': 'assets/' + name,
                           'sha256': export_build._sha_file(fp), 'size': os.path.getsize(fp)})
        url_map[url] = 'sa://assets/' + quote(by_source[fp])
    def remap(url):
        return url_map.get(url, url)
    for cue in baked.get('cues') or []:
        for key in ('maskUrl', 'sourceUrl'):
            if cue.get(key):
                cue[key] = remap(cue[key])
    for track in baked.get('bgmTracks') or []:
        if track.get('url'):
            track['url'] = remap(track['url'])
    for step in export_build.step_asset_slots(baked):
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
    return export_build.content_id(baked, assets), refs


def _verify(read, names):
    prefix = 'assets/'
    try:
        manifest = json.loads(read('manifest.json'))
        show_bytes = read('show.json')
        if manifest.get('schema') != 1 or manifest.get('policy') != export_build.POLICY:
            return None
        if hashlib.sha256(show_bytes).hexdigest() != manifest.get('configSha256'):
            return None
        assets = manifest.get('assets')
        if not isinstance(assets, list):
            return None
        for asset in assets:
            path = asset['path']
            name = path[len(prefix):]
            if (not path.startswith(prefix) or not name or name in ('.', '..')
                    or '/' in name or '\\' in name or path not in names):
                return None
            raw = read(path)
            if len(raw) != asset['size'] or hashlib.sha256(raw).hexdigest() != asset['sha256']:
                return None
        show = json.loads(show_bytes)
        if export_build.content_id(show, assets) != manifest.get('contentId'):
            return None
        return manifest['contentId']
    except (OSError, ValueError, KeyError, TypeError, UnicodeError, zipfile.BadZipFile):
        return None


def verify_bundle(path):
    if not os.path.isfile(os.path.join(path, 'manifest.json')):
        return None
    names = {'show.json', 'manifest.json'}
    assets = os.path.join(path, 'assets')
    if os.path.isdir(assets):
        names.update('assets/' + n for n in os.listdir(assets))
    def read(p):
        with open(os.path.join(path, *p.split('/')), 'rb') as stream:
            return stream.read()
    return _verify(read, names)


def _apk_signature(path):
    try:
        stat = os.stat(path)
        return os.path.abspath(path), stat.st_size, stat.st_mtime_ns
    except OSError:
        return None


def _verified_apk(path):
    """APK の検証結果をファイル署名単位で共有する。receipt は導入判定だけで別に見る。"""
    signature = _apk_signature(path)
    if signature is None:
        return None
    with _apk_lock:
        if signature in _apk_cache:
            return _apk_cache[signature]
        try:
            with zipfile.ZipFile(path) as apk:
                prefix = 'assets/show/'
                names = {n[len(prefix):] for n in apk.namelist() if n.startswith(prefix)}
                content_id = _verify(lambda p: apk.read(prefix + p), names)
                if not content_id:
                    result = None
                else:
                    manifest = json.loads(apk.read(prefix + 'manifest.json'))
                    show = json.loads(apk.read(prefix + 'show.json'))
                    result = {'contentId': content_id, 'manifest': manifest, 'show': show,
                              'assets': {a['path']: dict(a) for a in manifest['assets']}}
        except (OSError, ValueError, KeyError, TypeError, UnicodeError, zipfile.BadZipFile):
            result = None
        _apk_cache.clear()
        _apk_cache[signature] = result
        return result


def verify_apk(path):
    verified = _verified_apk(path)
    return verified['contentId'] if verified else None


def verify_receipt(path, apk_content_id):
    receipt_path = path + '.content.json'
    if not apk_content_id or not os.path.isfile(receipt_path):
        return None
    try:
        with open(receipt_path, encoding='utf-8') as stream:
            receipt = json.load(stream)
        if (receipt.get('schema') != 1 or receipt.get('policy') != export_build.POLICY
                or receipt.get('contentId') != apk_content_id
                or not isinstance(receipt.get('buildGuid'), str)
                or not re.fullmatch(r'[a-f0-9]{32}', _guid(receipt['buildGuid']))
                or receipt.get('apkSha256') != export_build._sha_file(path)):
            return None
        return receipt['buildGuid']
    except (OSError, ValueError, TypeError, AttributeError):
        return None


def _apk_has_manifest(path):
    try:
        with zipfile.ZipFile(path) as apk:
            return 'assets/show/manifest.json' in apk.namelist()
    except (OSError, ValueError, zipfile.BadZipFile):
        return True


def _guid(value):
    return str(value or '').replace('-', '').lower()


def _effects(show, refs, prepared_id, bundle_id, apk_id, dirs):
    cues = {c.get('id'): c for c in show.get('cues') or []}
    slots = {s.get('name'): s.get('url') for s in
             ((show.get('control') or {}).get('slots') or []) if isinstance(s, dict)}
    def source_ok(url):
        if url in refs:
            return True
        seen = set()
        while isinstance(url, str) and url.startswith('slot://'):
            name = url[len('slot://'):]
            if name in seen or not slots.get(name):
                return False
            seen.add(name)
            url = slots[name]
        fp = export_build.resolve_local_asset(url, dirs)
        if not fp:
            return False
        try:
            export_build._sha_file(fp)
            return True
        except OSError:
            return False
    rows = []
    for seg in ((show.get('timeline') or {}).get('segments') or []):
        for take in seg.get('takes') or []:
            urls = set()
            for step in take.get('steps') or []:
                if step.get('assetUrl'):
                    urls.add(step['assetUrl'])
                cue = cues.get(step.get('cueId')) or {}
                urls.update(cue[k] for k in ('maskUrl', 'sourceUrl') if cue.get(k))
            response = cues.get(take.get('markStartCueId')) or {}
            urls.update(response[k] for k in ('maskUrl', 'sourceUrl') if response.get(k))
            urls = {u for u in urls if u}
            source_ready = all(source_ok(u) for u in urls)
            bundled = source_ready and bundle_id == prepared_id
            in_apk = bundled and apk_id == prepared_id
            status = 'ok' if in_apk else ('error' if not source_ready else 'unknown')
            rows.append({'id': take.get('id') or '', 'name': take.get('name') or '',
                         'status': status,
                         'title': '実機版に同梱済み' if in_apk else '実機版への反映が必要です',
                         'action': '' if in_apk else '素材を確認し APK を焼き直してください',
                         'sourceReady': source_ready, 'bundled': bundled,
                         'inApk': in_apk, 'assetCount': len(urls)})
    return rows


def _slot_url(url, show):
    slots = {s.get('name'): s.get('url') for s in
             ((show.get('control') or {}).get('slots') or []) if isinstance(s, dict)}
    seen = set()
    while isinstance(url, str) and url.startswith('slot://'):
        name = url[len('slot://'):]
        if name in seen or not slots.get(name):
            return None
        seen.add(name)
        url = slots[name]
    return url


def _asset_kind(name):
    ext = os.path.splitext(name.lower())[1]
    if ext in ('.jpg', '.jpeg', '.png', '.webp', '.bmp', '.gif'):
        return 'image'
    if ext in ('.mp4', '.webm', '.mov', '.m4v', '.avi'):
        return 'video'
    if ext in ('.mp3', '.wav', '.ogg', '.m4a', '.aac', '.flac'):
        return 'audio'
    return 'file'


def _take_asset_refs(show, take):
    cues = {c.get('id'): c for c in show.get('cues') or []}
    refs = {}
    for index, step in enumerate(take.get('steps') or []):
        cue_id = step.get('cueId') or ''
        cue = cues.get(cue_id) or {}
        cue_name = cue.get('name') or cue.get('label') or cue_id or '演出'
        if step.get('assetUrl'):
            refs[f'step:{index}:assetUrl'] = {
                'label': cue_name + '・ステップ素材', 'cueId': cue_id,
                'url': step['assetUrl']}
        if cue.get('sourceUrl'):
            refs[f'step:{index}:sourceUrl'] = {
                'label': cue_name + '・映像', 'cueId': cue_id, 'url': cue['sourceUrl']}
        if cue.get('maskUrl'):
            refs[f'step:{index}:maskUrl'] = {
                'label': cue_name + '・マスク', 'cueId': cue_id, 'url': cue['maskUrl']}
    response_id = take.get('markStartCueId') or ''
    response = cues.get(response_id) or {}
    response_name = response.get('name') or response_id
    if response.get('sourceUrl'):
        refs['markStart:sourceUrl'] = {
            'label': response_name + '・報告開始の映像', 'cueId': response_id,
            'url': response['sourceUrl']}
    if response.get('maskUrl'):
        refs['markStart:maskUrl'] = {
            'label': response_name + '・マスク', 'cueId': response_id,
            'url': response['maskUrl']}
    return refs


def _current_asset(ref, show, dirs):
    if ref is None:
        return None
    url = _slot_url(ref.get('url'), show)
    local_url = ''
    clean = url.split('?', 1)[0].split('#', 1)[0] if isinstance(url, str) else ''
    for prefix, base in dirs.items():
        if not clean.startswith(prefix):
            continue
        candidate = os.path.abspath(os.path.join(base, unquote(clean[len(prefix):])))
        try:
            if os.path.commonpath([candidate, os.path.abspath(base)]) == os.path.abspath(base):
                local_url = url
        except ValueError:
            pass
        break
    path = export_build.resolve_local_asset(local_url, dirs) if local_url else None
    name = unquote(local_url.split('?', 1)[0].rsplit('/', 1)[-1]) if local_url else ''
    return {'url': local_url, 'name': name, 'exists': bool(path),
            'sha256': export_build._sha_file(path) if path else ''}


def _built_asset(ref, verified):
    if ref is None:
        return None
    source = ref.get('url')
    prefix = 'sa://assets/'
    name = unquote(source[len(prefix):]) if isinstance(source, str) and source.startswith(prefix) else ''
    path = 'assets/' + name if name else ''
    record = verified['assets'].get(path) if verified and path else None
    url = ('/ops/apk-asset?contentId=' + quote(verified['contentId'], safe='')
           + '&path=' + quote(path, safe='')) if record else ''
    return {'url': url, 'name': name, 'exists': bool(record),
            'sha256': record.get('sha256', '') if record else ''}


def _asset_status(current, built, apk_verified):
    if not apk_verified or (current is not None and not current['exists']):
        return 'unknown'
    if current is None:
        return 'removed' if built and built['exists'] else 'unknown'
    if built is None or not built['exists']:
        return 'unbuilt'
    return 'same' if current['sha256'] == built['sha256'] else 'changed'


def _effect_rows(show, built_show, verified, dirs):
    def indexed(source):
        rows = []
        for seg_index, segment in enumerate(((source.get('timeline') or {}).get('segments') or [])):
            for take_index, take in enumerate(segment.get('takes') or []):
                identity = take.get('id') or f'@{seg_index}:{take_index}'
                rows.append((identity, segment, take))
        return rows

    current_rows = indexed(show)
    built_rows = indexed(built_show or {})
    current_by_id = {identity: (segment, take) for identity, segment, take in current_rows}
    built_by_id = {identity: (segment, take) for identity, segment, take in built_rows}
    order = [identity for identity, _, _ in current_rows]
    order.extend(identity for identity, _, _ in built_rows if identity not in current_by_id)
    effects = []
    for identity in order:
        current_pair = current_by_id.get(identity)
        built_pair = built_by_id.get(identity)
        segment, take = current_pair or built_pair
        current_refs = _take_asset_refs(show, current_pair[1]) if current_pair else {}
        built_refs = _take_asset_refs(built_show, built_pair[1]) if built_pair else {}
        keys = list(current_refs)
        keys.extend(key for key in built_refs if key not in current_refs)
        assets = []
        for key in keys:
            current_ref = current_refs.get(key)
            built_ref = built_refs.get(key)
            descriptor = current_ref or built_ref
            current = _current_asset(current_ref, show, dirs)
            built = _built_asset(built_ref, verified)
            name = (current or {}).get('name') or (built or {}).get('name') or ''
            assets.append({'key': key, 'label': descriptor['label'],
                           'cueId': descriptor.get('cueId') or '',
                           'kind': _asset_kind(name), 'current': current, 'built': built,
                           'status': _asset_status(current, built, bool(verified))})
        take_id = take.get('id') or ''
        segment_id = segment.get('id') or take_id.split('#', 1)[0]
        match = re.match(r'^L(\d+)C(\d+)', take_id)
        derived_segment = (f'{match.group(1)}周目{chr(ord("A") + int(match.group(2)))}'
                           if match and int(match.group(2)) < 26 else '')
        cue_names = []
        cue_table = {}
        if built_show:
            cue_table.update({c.get('id'): c for c in (built_show.get('cues') or [])})
        cue_table.update({c.get('id'): c for c in (show.get('cues') or [])})
        for step in take.get('steps') or []:
            cue = cue_table.get(step.get('cueId')) or {}
            cue_name = cue.get('name') or cue.get('label')
            if cue_name and cue_name not in cue_names:
                cue_names.append(cue_name)
        name = take.get('name') or (cue_names[0] if cue_names else take_id)
        effects.append({'id': identity, 'name': name,
                        'segmentId': segment_id,
                        'segmentName': segment.get('name') or derived_segment or segment_id,
                        'assets': assets,
                        'procedural': not any(a['kind'] in ('image', 'video')
                                              and not a['key'].endswith(':maskUrl')
                                              for a in assets)})
    return effects


def media_library(show, dirs, apk_path, now=None):
    """現在採用中の素材と、検証済み APK に実在する素材を用途ごとに比較する。"""
    now = time.time() if now is None else now
    verified = _verified_apk(apk_path)
    try:
        prepared_id, _ = _planned(show, dirs)
    except (OSError, ValueError, TypeError):
        prepared_id = ''
    built_show = verified['show'] if verified else None
    return {'ok': True, 'observedAt': int(now), 'apkVerified': bool(verified),
            'apkContentId': verified['contentId'] if verified else '',
            'preparedContentId': prepared_id,
            'effects': _effect_rows(show, built_show, verified, dirs)}


def read_apk_asset(apk_path, content_id, asset_path):
    """検証済み manifest の allowlist にある 1 ファイルだけを APK から読む。"""
    verified = _verified_apk(apk_path)
    if not verified:
        return 404, None, None
    if content_id != verified['contentId']:
        return 409, None, None
    if (not isinstance(asset_path, str) or not asset_path.startswith('assets/')
            or not asset_path[len('assets/'):] or '/' in asset_path[len('assets/'):]
            or '\\' in asset_path or asset_path not in verified['assets']):
        return 404, None, None
    record = verified['assets'][asset_path]
    try:
        with zipfile.ZipFile(apk_path) as apk:
            raw = apk.read('assets/show/' + asset_path)
    except (OSError, KeyError, zipfile.BadZipFile):
        return 404, None, None
    if len(raw) != record['size'] or hashlib.sha256(raw).hexdigest() != record['sha256']:
        return 409, None, None
    return 200, raw, record


def content_status(show, dirs, bundle_dir, apk_path, devices, now=None):
    global _cache_key, _cache_value
    now = time.time() if now is None else now
    with _lock:
        prepared_id, refs, error = '', {}, ''
        try:
            _, refs = export_build.prepare(show, dirs)
        except (OSError, ValueError, TypeError) as exc:
            error = str(exc)
        signatures = tuple(sorted((fp, os.stat(fp).st_size, os.stat(fp).st_mtime_ns)
                                  for fp in set(refs.values()))) if refs else ()
        manifest_path = os.path.join(bundle_dir, 'manifest.json')
        receipt_path = apk_path + '.content.json'
        asset_dir = os.path.join(bundle_dir, 'assets')
        bundle_signatures = tuple(sorted((name, os.stat(os.path.join(asset_dir, name)).st_size,
                                          os.stat(os.path.join(asset_dir, name)).st_mtime_ns)
                                         for name in os.listdir(asset_dir))) if os.path.isdir(asset_dir) else ()
        show_key = hashlib.sha256(json.dumps(show, sort_keys=True, ensure_ascii=False,
                                               separators=(',', ':')).encode('utf-8')).hexdigest()
        key = (show_key, error, signatures, bundle_signatures,
               tuple((p, os.stat(p).st_size, os.stat(p).st_mtime_ns) if os.path.isfile(p)
                     else (p, None, None) for p in
                     (manifest_path, os.path.join(bundle_dir, 'show.json'),
                      apk_path, receipt_path)))
        if key == _cache_key:
            prepared_id, bundle_id, apk_id, build_guid, apk_has_manifest = _cache_value
        else:
            if not error:
                try:
                    prepared_id, refs = _planned(show, dirs)
                except (OSError, ValueError, TypeError) as exc:
                    error = str(exc)
            bundle_id = verify_bundle(bundle_dir)
            apk_id = verify_apk(apk_path)
            build_guid = verify_receipt(apk_path, apk_id)
            apk_has_manifest = _apk_has_manifest(apk_path) if os.path.isfile(apk_path) else False
            _cache_key, _cache_value = key, (prepared_id, bundle_id, apk_id,
                                            build_guid, apk_has_manifest)
    prep = (_stage('error', title='参照ファイルに問題があります', action=error)
            if error else _stage('ok', prepared_id, '参照ファイルは揃っています', ''))
    bundle_exists = os.path.isfile(manifest_path)
    apk_exists = os.path.isfile(apk_path)
    bundle = (_stage('error' if bundle_exists else 'unknown',
                     title='同梱内容を確認できません', action='書き出してください')
              if not bundle_id else _stage('ok' if bundle_id == prepared_id else 'error',
                                           bundle_id, '同梱内容を確認しました' if bundle_id == prepared_id
                                           else '準備済みの内容と違います',
                                           '' if bundle_id == prepared_id else 'APK 用に書き出してください'))
    if not apk_id:
        apk = _stage('error' if apk_exists and apk_has_manifest else 'unknown',
                     title='対応する APK がありません', action='APK を焼き直してください')
    elif not build_guid:
        apk = _stage('error' if os.path.isfile(receipt_path) else 'unknown',
                     apk_id, 'APK の導入版を確認できません',
                     'APK を焼き直してください')
    else:
        apk = _stage('ok' if apk_id == prepared_id else 'error', apk_id,
                     'APK を確認しました' if apk_id == prepared_id
                     else '準備済みの内容と違います',
                     '' if apk_id == prepared_id else 'APK を焼き直してください')
    apk['buildGuid'] = build_guid or ''
    quests = []
    for q in operations.FLEET['quests']:
        matches = [(did, h) for did, h in devices.items()
                   if h.get('localIp') == q['host'] and 0 <= now - float(h.get('at') or 0) < 6]
        did, hb = matches[0] if len(matches) == 1 else ('', None)
        identity_ok = not q.get('deviceId') or did == q['deviceId']
        fresh = hb and 0 <= now - float(hb.get('at') or 0) < 6
        matched = (fresh and identity_ok and hb.get('contentPolicy') == export_build.POLICY
                   and hb.get('contentVerified') is True and prepared_id and apk_id == prepared_id
                   and hb.get('contentId') == prepared_id and bool(build_guid)
                   and _guid(hb.get('buildGuid')) == _guid(build_guid))
        quest_error = len(matches) > 1 or bool(fresh and (not identity_ok or hb.get('contentVerified') is False
                            or (hb.get('contentPolicy') and
                                hb.get('contentPolicy') != export_build.POLICY)
                            or (hb.get('contentId') and prepared_id
                                and hb.get('contentId') != prepared_id)
                            or (hb.get('buildGuid') and build_guid
                                and _guid(hb.get('buildGuid')) != _guid(build_guid))))
        quests.append({'id': q['id'], 'status': 'ok' if matched else
                       ('error' if quest_error else 'unknown'),
                       'contentId': hb.get('contentId', '') if fresh else '',
                       'buildGuid': hb.get('buildGuid', '') if fresh else '',
                       'title': '実機版が一致しています' if matched else '実機版を確認できません',
                       'action': '' if matched else '対応する APK を入れ起動してください'})
    effects = _effects(show, refs, prepared_id, bundle_id,
                       apk_id if build_guid else None, dirs)
    return {'ok': True, 'observedAt': int(now), 'policy': export_build.POLICY,
            'ready': bool(prepared_id and build_guid and prepared_id == bundle_id == apk_id
                          and all(q['status'] == 'ok' for q in quests)),
            'preparation': prep, 'bundle': bundle, 'apk': apk,
            'effects': effects, 'quests': quests}
