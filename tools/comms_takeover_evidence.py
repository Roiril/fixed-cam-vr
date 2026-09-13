"""Verify applied output loss without treating a phase transition as rendered evidence."""
from __future__ import annotations


def analyze_takeover(events):
    samples = [e for e in events if e.get('ev') == 'commsTakeover']
    if not samples:
        return []
    output = []
    captured = False
    started = None
    for e in samples:
        if e.get('started') != started:
            started = e.get('started')
            captured = False
        phase = e.get('phase')
        if phase not in ('Output', 'Pursuit', 'Seized', 'Complete'):
            continue
        try:
            reveal, ink = float(e['reveal']), float(e['glyph'])
            cut = int(e['cut'])
            if phase in ('Seized', 'Complete'):
                captured = True
                if not 0 < reveal < 1 or ink > .004 or cut < 1:
                    output.append(('FAIL', '未完成の出力と打鍵が同時に奪われていない'))
                elif phase == 'Seized':
                    output.append(('OK', '未完成の出力が消え、打鍵の停止が実行された'))
                elif float(e['faceInk']) > .004 or float(e['collapse']) < .99:
                    output.append(('FAIL', '捕捉後に顔か表示の残骸が戻っている'))
                else:
                    output.append(('OK', '捕捉後も文字と顔は戻っていない'))
            elif captured and ink > .004:
                output.append(('FAIL', '同じ報告が捕捉後に再開している'))
        except (KeyError, ValueError, TypeError):
            output.append(('WARN', '出力の捕捉を確認する描画値が足りない'))
    if not captured:
        output.append(('WARN', '侵食は途中までの記録。中断またはログの範囲を確認する'))
    return output
