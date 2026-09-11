"""タブレット（体験前の説明と設定）と Quest の枠（`control.visitor`）の判断。

2026-09-11・`canon/LEDGER.md` 0185。クエストα用・β用のタブレット 2 台が `visitor.html` を開き、
体験者が言語とホラー軽減を選ぶ。選んだ値は役ごとの枠に入り、long-poll で Quest へ届く。

流れ（1 本だけ）:
  visitor.html → POST /command setVisitor → control.visitor.<役>（epoch++）
    → long-poll → Quest（注意書きが出ている段で書く。VisitorPrefs.cs）
    → 体験者が始めた瞬間の世代を heartbeat が返す（visitorConsumedEpoch）
    → 卓が同じ世代を見たら枠を既定へ戻す（consume）＝ 次の人に前の人の設定を持ち越さない

役と端末の結び付けは `control.visitorDevices`（役 → 端末 ID）。端末 ID は Quest が heartbeat で
名乗る `SystemInfo.deviceUniqueIdentifier`。結ぶのは visitor.html のスタッフ欄。

⚠ ここは純関数だけ。HTTP・ファイル・時計は capture-server.py 側が持つ。
"""

ROLES = ('alpha', 'beta')
LANGS = ('ja', 'en', 'fr')


def default_slot():
    """枠の既定（＝ 何も選んでいない）。epoch 0 は「卓が一度も書いていない」。"""
    return {'lang': 'ja', 'relief': False, 'epoch': 0}


def default_control_fields():
    """`_default_show()['control']` に足すキー。"""
    return {
        'visitorDevices': {r: '' for r in ROLES},
        'visitor': {r: default_slot() for r in ROLES},
    }


def ensure(ctrl):
    """古い show.json でキーが欠けていても壊れないように埋める（in place）。"""
    devs = ctrl.setdefault('visitorDevices', {})
    slots = ctrl.setdefault('visitor', {})
    for r in ROLES:
        if not isinstance(devs.get(r), str):
            devs[r] = ''
        s = slots.get(r)
        if not isinstance(s, dict):
            s = default_slot()
            slots[r] = s
        s.setdefault('lang', 'ja')
        s.setdefault('relief', False)
        s.setdefault('epoch', 0)
    return devs, slots


def view(ctrl):
    """読むだけの側。ctrl を触らずに、欠けたキーを既定で補った (devices, slots) を返す。
    ⚠ heartbeat の判定はロックの外で読むので、ここで in place に書くと
    `_mutate_show` の json.dump と衝突しうる（dict changed size）。書くのは ensure だけ。"""
    devs_src = ctrl.get('visitorDevices') if isinstance(ctrl.get('visitorDevices'), dict) else {}
    slots_src = ctrl.get('visitor') if isinstance(ctrl.get('visitor'), dict) else {}
    devs = {}
    slots = {}
    for r in ROLES:
        d = devs_src.get(r)
        devs[r] = d if isinstance(d, str) else ''
        s = slots_src.get(r)
        out = default_slot()
        if isinstance(s, dict):
            out['lang'] = s.get('lang') or 'ja'
            out['relief'] = bool(s.get('relief'))
            out['epoch'] = int(s.get('epoch') or 0)
        slots[r] = out
    return devs, slots


def is_default(slot):
    return (slot.get('lang') or 'ja') == 'ja' and not bool(slot.get('relief'))


def set_visitor(ctrl, role, lang, relief):
    """体験者の選択を役の枠へ書く。世代を +1 して返す。"""
    if role not in ROLES:
        raise ValueError(f'setVisitor: role は alpha / beta（{role!r}）')
    lang = (lang or 'ja').lower()
    if lang not in LANGS:
        raise ValueError(f'setVisitor: lang は ja / en / fr（{lang!r}）')
    _, slots = ensure(ctrl)
    s = slots[role]
    s['lang'] = lang
    s['relief'] = bool(relief)
    s['epoch'] = int(s.get('epoch') or 0) + 1
    return s['epoch']


def bind_device(ctrl, role, device_id):
    """役に端末を結ぶ（空なら解く）。同じ端末が別の役に居たらそちらは解く（1 台 1 役）。"""
    if role not in ROLES:
        raise ValueError(f'bindVisitorDevice: role は alpha / beta（{role!r}）')
    device_id = (device_id or '').strip()
    devs, _ = ensure(ctrl)
    if device_id:
        for r in ROLES:
            if r != role and devs.get(r, '').lower() == device_id.lower():
                devs[r] = ''
    devs[role] = device_id


def role_for_device(ctrl, device_id):
    """端末 ID が結ばれている役。無ければ ''。"""
    if not device_id:
        return ''
    devs, _ = view(ctrl)
    for r in ROLES:
        if devs.get(r, '') and devs[r].lower() == device_id.lower():
            return r
    return ''


def needs_consume(ctrl, device_id, consumed_epoch):
    """heartbeat の消費世代が、その端末の役の枠の世代と一致し、枠が既定でないか。"""
    consumed_epoch = int(consumed_epoch or 0)
    if consumed_epoch <= 0:
        return False
    role = role_for_device(ctrl, device_id)
    if not role:
        return False
    _, slots = view(ctrl)
    s = slots[role]
    return s['epoch'] == consumed_epoch and not is_default(s)


def consume(ctrl, device_id, consumed_epoch):
    """体験者が始めたので枠を既定へ戻す（世代は進める＝ Quest が既定を受け取れる）。
    戻したら True。条件が崩れていれば何もせず False（heartbeat との競合に備えて再判定する）。"""
    if not needs_consume(ctrl, device_id, consumed_epoch):
        return False
    role = role_for_device(ctrl, device_id)
    _, slots = ensure(ctrl)
    s = slots[role]
    s['lang'] = 'ja'
    s['relief'] = False
    s['epoch'] = int(s.get('epoch') or 0) + 1
    return True


def device_rows(devices, ctrl, now, alive_sec=6.0):
    """GET /unity/devices の中身。heartbeat を持つ端末ごとに 1 行 ＋ 役の枠。

    devices: {deviceId: heartbeat body（'at' 付き）}
    """
    devs, slots = view(ctrl)
    rows = []
    for did, hb in devices.items():
        at = float(hb.get('at') or 0)
        age = (now - at) if at else None
        rows.append({
            'deviceId': did,
            'shortId': did[:6],
            'deviceModel': hb.get('deviceModel') or '',
            'role': role_for_device(ctrl, did),
            'ageSec': age,
            'alive': age is not None and age < alive_sec,
            'phase': hb.get('phase') or '',
            'lang': hb.get('lang') or 'ja',
            'relief': bool(hb.get('relief')),
            'visitorRole': hb.get('visitorRole') or '',
            'visitorPendingEpoch': int(hb.get('visitorPendingEpoch') or 0),
            'visitorAppliedEpoch': int(hb.get('visitorAppliedEpoch') or 0),
            'visitorConsumedEpoch': int(hb.get('visitorConsumedEpoch') or 0),
            'appliedRev': int(hb.get('appliedRev') or -1),
        })
    rows.sort(key=lambda r: (r['role'] == '', r['role'], r['deviceId']))
    return {
        'devices': rows,
        'roles': {r: {'deviceId': devs.get(r, ''), 'slot': dict(slots[r])} for r in ROLES},
    }
