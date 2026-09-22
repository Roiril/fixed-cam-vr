"""機ごとの heartbeat（`GET /unity/devices`）の整形。

2026-09-11（`canon/LEDGER.md` 0185 / 0187）。Quest は heartbeat で `deviceId` を名乗るようになり、
卓は機ごとに持ち分ける（それまでは 1 スロットを 2 台が交互に上書きしていた・`onsite_day_ops.md` §3）。

⚠ タブレットの設定（言語・軽減）は**ここを通らない**。タブレットは Quest 自身の口（`VisitorPortal` :8090）へ
直接繋ぐ（0187「メインのウェブ卓は経由せずにクエストとタブレットを直接つなぐ」）。
卓に載るのは「口が開いているか」「受けた累計」「実値」だけで、スタッフが眺めるためのもの。

⚠ ここは純関数だけ。HTTP・時計は capture-server.py 側が持つ。
"""


def device_rows(devices, now, alive_sec=6.0):
    """`GET /unity/devices` の中身。heartbeat を持つ端末ごとに 1 行。

    devices: {deviceId: heartbeat body（'at' 付き）}
    """
    rows = []
    for did, hb in devices.items():
        at = float(hb.get('at') or 0)
        age = (now - at) if at else None
        rows.append({
            'deviceId': did,
            'shortId': did[:6],
            'deviceModel': hb.get('deviceModel') or '',
            'localIp': hb.get('localIp') or '',
            'ageSec': age,
            'alive': age is not None and age < alive_sec,
            'phase': hb.get('phase') or '',
            'titleStage': hb.get('titleStage') or '',
            'lang': hb.get('lang') or 'ja',
            'relief': bool(hb.get('relief')),
            # タブレットの口（Quest 自身の HTTP）。0 = 開けなかった。
            'visitorPort': int(hb.get('visitorPort') or 0),
            'visitorReceived': int(hb.get('visitorReceived') or 0),
            'visitorPending': bool(hb.get('visitorPending')),
            'appliedRev': int(hb.get('appliedRev') or -1),
            'status': dict(hb['status']) if isinstance(hb.get('status'), dict) else {},
        })
    rows.sort(key=lambda r: (r['localIp'] == '', r['localIp'], r['deviceId']))
    return {'devices': rows}
