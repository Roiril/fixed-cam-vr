"""設営時に固定カメラから無人画を撮り、採用候補として管理する。"""

import base64
import datetime
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import threading
import time
import uuid
import urllib.request

from PIL import Image, UnidentifiedImageError


PLATE_CUES = {
    'A': ('plate_A', 'plate_A_right'),
    'B': ('plate_B',),
    'C': ('plate_C',),
}
MAX_FRAME_BYTES = 4 * 1024 * 1024
DEPENDENTS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'plate-dependents.json')
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# 作り直しの道具が「元の写真と合わない」で断ったときの終了コード（build_flow_right.py）
REALIGN_REFUSED = 3
_realign_lock = threading.Lock()


class PlateError(Exception):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


def _capture_path(url, captures_dir):
    prefix = '/captures/'
    if not isinstance(url, str) or not url.startswith(prefix):
        return None
    name = url[len(prefix):]
    if not name or name != os.path.basename(name) or name in ('.', '..'):
        return None
    root = os.path.realpath(captures_dir)
    path = os.path.realpath(os.path.join(root, name))
    try:
        if os.path.commonpath((root, path)) != root:
            return None
    except ValueError:
        return None
    return path


def _sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def _valid_evidence(url, camera_id, manifest, captures_dir):
    entry = manifest.get(url)
    path = _capture_path(url, captures_dir)
    if (not isinstance(entry, dict) or entry.get('kind') != 'onsite-plate' or
            entry.get('cameraId') != camera_id or not path or not os.path.isfile(path)):
        return None
    expected = entry.get('sha256')
    if not isinstance(expected, str) or len(expected) != 64:
        return None
    try:
        if _sha256(path) != expected.lower():
            return None
    except OSError:
        return None
    return entry


def _camera_assignment(camera):
    try:
        port = int(camera.get('port') or 8080)
    except (TypeError, ValueError):
        port = None
    return {
        'host': str(camera.get('host') or '').strip(),
        'port': port,
        'uuid': str(camera.get('uuid') or ''),
        'sourceId': str(camera.get('sourceId') or ''),
        'path': str(camera.get('path') or camera.get('videoPath') or '/video'),
    }


def _same_assignment(entry, camera):
    return isinstance(entry.get('cameraAssignment'), dict) and \
        entry['cameraAssignment'] == _camera_assignment(camera)


def load_dependents(path=DEPENDENTS_PATH):
    """写真に合わせて作った素材の台帳（plate-dependents.json）。読めなければ None。"""
    try:
        with open(path, encoding='utf-8') as stream:
            data = json.load(stream)
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict) or not isinstance(data.get('dependents'), dict) or \
            not isinstance(data.get('groups'), dict):
        return None
    return data


def dependents_status(dependents, referenced, current_names, captures_dir):
    """台本が使う cue のうち、無人の写真に合わせて作った素材を、カメラごと・作り方ごとにまとめる。

    plate（素材を合わせた写真）が採用中の写真と違えば current=False。そのカメラの写真を
    替えたので、素材は前の写真の位置と明るさのまま出る。
    """
    plate_cue_ids = {cue_id for ids in PLATE_CUES.values() for cue_id in ids}
    if dependents is None:
        return {camera_id: None for camera_id in PLATE_CUES}, None
    rows = dependents['dependents']
    independent = dependents.get('independent') or {}
    groups = dependents['groups']
    out = {camera_id: [] for camera_id in PLATE_CUES}
    unclassified = []
    for cue_id in sorted(referenced):
        if cue_id in plate_cue_ids or cue_id in independent:
            continue
        row = rows.get(cue_id)
        group = groups.get(row.get('group')) if isinstance(row, dict) else None
        camera_id = group.get('camera') if isinstance(group, dict) else None
        plate = row.get('plate') if isinstance(row, dict) else None
        if camera_id not in out or not isinstance(plate, str) or not plate:
            unclassified.append(cue_id)
            continue
        entry = next((e for e in out[camera_id]
                      if e['group'] == row['group'] and e['plate'] == plate), None)
        if entry is None:
            url = '/captures/' + plate
            path = _capture_path(url, captures_dir)
            entry = {'group': row['group'], 'title': str(group.get('title') or row['group']),
                     'how': str(group.get('how') or ''),
                     'needsInternet': group.get('needsInternet') is True,
                     'realign': isinstance(group.get('realign'), dict),
                     'plate': plate, 'plateUrl': url,
                     'plateExists': bool(path and os.path.isfile(path)),
                     'current': plate == current_names.get(camera_id), 'cueIds': []}
            out[camera_id].append(entry)
        entry['cueIds'].append(cue_id)
    return out, unclassified


def plate_status(show, manifest, captures_dir, observed_at=None, dependents=None, referenced=None):
    """show と撮影台帳から GET /shoot/plates の応答を作る。

    dependents（plate-dependents.json）と referenced（台本が使う cue の id）を渡すと、
    各カメラに「この写真に合わせて作った素材」を添える。
    """
    cues = {cue.get('id'): cue for cue in (show.get('cues') or [])
            if isinstance(cue, dict) and cue.get('id')}
    cameras = {camera.get('id'): camera for camera in (show.get('cameras') or [])
               if isinstance(camera, dict) and camera.get('id')}
    items = []
    for camera_id, cue_ids in PLATE_CUES.items():
        cue_rows = [cues.get(cue_id) for cue_id in cue_ids]
        urls = [str(cue.get('sourceUrl') or '') if cue else '' for cue in cue_rows]
        current_url = urls[0] if urls and urls[0] else ''
        current = None
        if current_url:
            path = _capture_path(current_url, captures_dir)
            exists = bool(path and os.path.isfile(path))
            evidence = manifest.get(current_url)
            captured_at = (evidence.get('capturedAt')
                           if isinstance(evidence, dict) and
                           evidence.get('kind') == 'onsite-plate' and
                           evidence.get('cameraId') == camera_id else None)
            current = {'url': current_url, 'name': os.path.basename(current_url),
                       'exists': exists, 'capturedAt': captured_at}

        candidates = []
        for url, entry in manifest.items():
            if not isinstance(entry, dict) or \
                    _valid_evidence(url, camera_id, manifest, captures_dir) is not entry:
                continue
            candidates.append({
                'url': url,
                'name': entry.get('name') or os.path.basename(url),
                'capturedAt': entry.get('capturedAt'),
                'width': entry.get('width'),
                'height': entry.get('height'),
            })
        candidates.sort(key=lambda candidate: candidate.get('capturedAt') or '', reverse=True)
        same_source = bool(current_url and all(url == current_url for url in urls))
        current_evidence = _valid_evidence(current_url, camera_id, manifest, captures_dir)
        ready = (all(cue_rows) and same_source and current_evidence is not None and
                 camera_id in cameras and _same_assignment(current_evidence, cameras[camera_id]))
        items.append({'cameraId': camera_id, 'cueIds': list(cue_ids), 'current': current,
                      'candidates': candidates, 'ready': bool(ready)})
    current_names = {item['cameraId']: (item['current'] or {}).get('name') for item in items}
    derived, unclassified = dependents_status(
        dependents if referenced is not None else None, referenced or (), current_names, captures_dir)
    for item in items:
        item['dependents'] = derived[item['cameraId']]
    return {'ok': True, 'observedAt': int(observed_at if observed_at is not None
                                          else datetime.datetime.now().timestamp()),
            'items': items, 'unclassified': unclassified}


def _read_jpeg(response, byte_limit, deadline_sec=6.0):
    data = bytearray()
    start = -1
    deadline = time.monotonic() + deadline_sec
    read = getattr(response, 'read1', response.read)
    while len(data) < byte_limit and time.monotonic() < deadline:
        chunk = read(min(65536, byte_limit - len(data)))
        if not chunk:
            break
        data.extend(chunk)
        if start < 0:
            start = data.find(b'\xff\xd8')
        if start >= 0:
            end = data.find(b'\xff\xd9', start + 2)
            if end >= 0:
                return bytes(data[start:end + 2])
    raise PlateError('配信から JPEG 1 枚を取得できませんでした', 502)


def _image_size(jpeg):
    try:
        with Image.open(io.BytesIO(jpeg)) as image:
            if image.format != 'JPEG':
                raise PlateError('配信画像が JPEG ではありません', 422)
            image.verify()
        with Image.open(io.BytesIO(jpeg)) as image:
            width, height = image.size
            image.load()
    except PlateError:
        raise
    except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as exc:
        raise PlateError(f'配信画像が壊れています: {exc}', 422) from exc
    if width <= 0 or height <= 0:
        raise PlateError('配信画像の寸法が不正です', 422)
    return width, height


def capture_plate(camera_id, camera, captures_dir, manifest_put, opener=None,
                  now=None, byte_limit=MAX_FRAME_BYTES):
    """show 登録済み MJPEG から JPEG を 1 枚保存する。"""
    if camera_id not in PLATE_CUES or not isinstance(camera, dict) or camera.get('id') != camera_id:
        raise PlateError('cameraId が不正です')
    host = str(camera.get('host') or '').strip()
    try:
        port = int(camera.get('port') or 8080)
    except (TypeError, ValueError) as exc:
        raise PlateError('カメラの port が不正です') from exc
    path = str(camera.get('path') or camera.get('videoPath') or '/video')
    if not host or not 1 <= port <= 65535 or not path.startswith('/') or '://' in path:
        raise PlateError('カメラの接続設定が不正です')
    request = urllib.request.Request(f'http://{host}:{port}{path}')
    auth = str(camera.get('auth') or '')
    if not auth and (camera.get('username') or camera.get('password')):
        auth = f"{camera.get('username') or ''}:{camera.get('password') or ''}"
    if auth:
        token = base64.b64encode(auth.encode('utf-8')).decode('ascii')
        request.add_header('Authorization', f'Basic {token}')
    open_url = opener or urllib.request.urlopen
    try:
        with open_url(request, timeout=5.0) as response:
            jpeg = _read_jpeg(response, byte_limit)
    except PlateError:
        raise
    except (OSError, TimeoutError) as exc:
        raise PlateError(f'カメラから撮影できません: {exc}', 502) from exc
    width, height = _image_size(jpeg)

    captured = now or datetime.datetime.now().astimezone()
    captured_at = captured.isoformat(timespec='seconds')
    stamp = captured.strftime('%Y%m%d_%H%M%S')
    name = f'plate_{camera_id}_{stamp}_{uuid.uuid4().hex[:8]}.jpg'
    os.makedirs(captures_dir, exist_ok=True)
    destination = os.path.join(captures_dir, name)
    temporary = destination + '.tmp'
    try:
        with open(temporary, 'wb') as stream:
            stream.write(jpeg)
        os.replace(temporary, destination)
    finally:
        try:
            if os.path.isfile(temporary):
                os.remove(temporary)
        except OSError:
            pass
    url = '/captures/' + name
    entry = {
        'kind': 'onsite-plate', 'name': name, 'shot': f'plate_{camera_id}',
        'cameraId': camera_id, 'capturedAt': captured_at, 'width': width,
        'height': height, 'bytes': len(jpeg), 'sha256': hashlib.sha256(jpeg).hexdigest(),
        'cameraAssignment': _camera_assignment(camera),
    }
    manifest_put(url, entry)
    return {'url': url, 'name': name, 'capturedAt': captured_at,
            'width': width, 'height': height}


def _set_group_plate(path, group_id, plate):
    """台帳の 1 行 1 cue の書式を保ったまま、group の行の plate だけを書き換える。"""
    with open(path, encoding='utf-8') as stream:
        text = stream.read()
    pattern = re.compile(r'("group":\s*"' + re.escape(group_id) + r'",\s*"plate":\s*")[^"]*(")')
    text, count = pattern.subn(lambda m: m.group(1) + plate + m.group(2), text)
    data = json.loads(text)
    rows = [row for row in data['dependents'].values()
            if isinstance(row, dict) and row.get('group') == group_id]
    if count != len(rows) or any(row.get('plate') != plate for row in rows):
        raise PlateError('台帳の書式が想定と違うので plate を書き換えられません', 500)
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8', newline='') as stream:
        stream.write(text)
    os.replace(tmp, path)


def _tail(proc, lines=14):
    text = ((proc.stdout or '') + '\n' + (proc.stderr or '')).strip()
    return [line for line in text.splitlines() if line.strip()][-lines:]


def _retarget(names, tag):
    """いまの版の名前から、どの版の名前にも当たる正規表現を作る（札の部分だけを差し替える）。"""
    out = {}
    for kind, name in names.items():
        if tag not in name:
            continue
        head, tail = name.split(tag, 1)
        out[kind] = re.compile('^/static-inputs/' + re.escape(head) + r'\d{8}_[A-Za-z0-9_]+?' +
                               re.escape(tail) + '$')
    return out


def realign_group(group_id, current_plate, captures_dir, mutate_show, dependents_path=DEPENDENTS_PATH,
                  root=REPO_ROOT, run=subprocess.run, python=sys.executable):
    """写真に合わせて作った素材を、採用中の写真へ位置合わせし直す（生成し直さない・外の回線が要らない）。

    台帳の group に realign（script / pointer）がある素材だけ。道具が新しい写真の名前で素材を書き、
    いまの版の名前を pointer に置く。台本の cue をその名前へ向け、台帳の plate を書き換える。
    道具が断った（カメラの位置か物の並びが変わった）ら何も書き換えず、生成し直すための首尾フレームを書く。
    戻り値は (HTTP の状態, 応答)。
    """
    ledger = load_dependents(dependents_path)
    group = (ledger or {}).get('groups', {}).get(group_id)
    spec = group.get('realign') if isinstance(group, dict) else None
    if not isinstance(spec, dict) or not spec.get('script') or not spec.get('pointer'):
        raise PlateError('この素材は位置合わせだけでは作り直せません')
    plate_path = _capture_path('/captures/' + str(current_plate or ''), captures_dir)
    if not plate_path or not os.path.isfile(plate_path):
        raise PlateError('採用中の写真がありません。先に無人の写真を採用してください', 409)
    script = os.path.join(root, spec['script'])
    pointer = os.path.join(root, spec['pointer'])
    timeout = int(spec.get('timeoutSec') or 900)
    if not _realign_lock.acquire(blocking=False):
        raise PlateError('作り直しを実行中です', 409)
    try:
        proc = run([python, script, '--plate', plate_path], cwd=root, capture_output=True, text=True,
                   encoding='utf-8', errors='replace', timeout=timeout)
        log = _tail(proc)
        if proc.returncode == REALIGN_REFUSED:
            inputs = run([python, script, '--plate', plate_path, '--flow-inputs'], cwd=root,
                         capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
            return 409, {'ok': False, 'refused': True, 'log': log, 'inputsLog': _tail(inputs, 4),
                         'error': '元の写真と合わないので位置合わせできません。動画を生成し直します（手順は当日スキル）'}
        if proc.returncode != 0:
            return 500, {'ok': False, 'log': log, 'error': f'作り直しの道具が失敗しました（終了コード {proc.returncode}）'}
        with open(pointer, encoding='utf-8') as stream:
            version = json.load(stream)
        names, tag, plate = version.get('names') or {}, str(version.get('tag') or ''), version.get('plate')
        if plate != os.path.basename(plate_path) or not tag:
            return 500, {'ok': False, 'log': log, 'error': '道具が書いた版が採用中の写真と一致しません'}
        patterns = _retarget(names, tag)
        cue_ids = [cue_id for cue_id, row in ledger['dependents'].items()
                   if isinstance(row, dict) and row.get('group') == group_id]
        changed = []

        def apply(show):
            cues = {cue.get('id'): cue for cue in (show.get('cues') or [])
                    if isinstance(cue, dict) and cue.get('id')}
            used = 0
            for cue_id in cue_ids:
                cue = cues.get(cue_id)
                if cue is None:
                    continue
                for key, value in list(cue.items()):
                    if not isinstance(value, str):
                        continue
                    kind = next((k for k, pattern in patterns.items() if pattern.match(value)), None)
                    if kind is None:
                        continue
                    used += 1
                    new = '/static-inputs/' + names[kind]
                    if new != value:
                        cue[key] = new
                        changed.append(f'{cue_id}.{key}')
            if used == 0:
                raise PlateError('台本の cue がこの素材を使っていません', 409)

        rev = mutate_show(apply)
        _set_group_plate(dependents_path, group_id, plate)
        return 200, {'ok': True, 'rev': rev, 'plate': plate, 'changed': changed, 'log': log}
    finally:
        _realign_lock.release()


def adopt_plate(camera_id, url, manifest, captures_dir, camera, mutate_show):
    """同じカメラで撮った候補を、対象 plate cue へ原子的に採用する。"""
    if camera_id not in PLATE_CUES:
        raise PlateError('cameraId が不正です')
    evidence = _valid_evidence(url, camera_id, manifest, captures_dir)
    if not evidence:
        raise PlateError('このカメラで撮影した未変更の候補だけ採用できます', 403)
    if not isinstance(camera, dict) or camera.get('id') != camera_id or \
            not _same_assignment(evidence, camera):
        raise PlateError('撮影時からカメラの割当が変わったため採用できません', 409)
    cue_ids = PLATE_CUES[camera_id]

    def apply(show):
        cues = {cue.get('id'): cue for cue in (show.get('cues') or [])
                if isinstance(cue, dict) and cue.get('id')}
        missing = [cue_id for cue_id in cue_ids if cue_id not in cues]
        if missing:
            raise PlateError('採用先の cue がありません: ' + ', '.join(missing), 409)
        for cue_id in cue_ids:
            cues[cue_id]['sourceUrl'] = url

    rev = mutate_show(apply)
    return {'ok': True, 'url': url, 'rev': rev}
