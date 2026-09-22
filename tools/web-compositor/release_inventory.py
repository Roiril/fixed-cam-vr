"""準備済み・同梱・APK・Quest の内容一致を読む。HTTP 経路では書き込まない。"""
import hashlib
import json
import os
import re
import threading
import time
import zipfile
from urllib.parse import quote

import export_build
import operations

_lock = threading.Lock()
_cache_key = None
_cache_value = None


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


def verify_apk(path):
    if not os.path.isfile(path):
        return None
    try:
        with zipfile.ZipFile(path) as apk:
            prefix = 'assets/show/'
            names = {n[len(prefix):] for n in apk.namelist() if n.startswith(prefix)}
            if 'manifest.json' not in names:
                return None
            return _verify(lambda p: apk.read(prefix + p), names)
    except (OSError, ValueError, zipfile.BadZipFile):
        return None


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
    prep = (_stage('error', title='演出素材の準備に問題があります', action=error)
            if error else _stage('ok', prepared_id, '演出素材は準備済み', ''))
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
