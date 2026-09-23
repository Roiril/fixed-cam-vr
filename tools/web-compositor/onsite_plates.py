"""設営時に固定カメラから無人画を撮り、採用候補として管理する。"""

import base64
import datetime
import hashlib
import io
import os
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


def plate_status(show, manifest, captures_dir, observed_at=None):
    """show と撮影台帳から GET /shoot/plates の応答を作る。"""
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
    return {'ok': True, 'observedAt': int(observed_at if observed_at is not None
                                          else datetime.datetime.now().timestamp()),
            'items': items}


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
